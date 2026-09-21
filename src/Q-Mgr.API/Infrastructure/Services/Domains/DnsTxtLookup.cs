using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using QMgr.Application.Interfaces;

namespace QMgr.Infrastructure.Services.Domains;

/// <summary>
/// A TXT lookup over plain DNS/UDP, written by hand.
///
/// WHY BY HAND: .NET's <see cref="System.Net.Dns"/> resolves names to addresses and nothing else —
/// there is no TXT query in the base class library at all. The usual answer is a NuGet DNS client,
/// and this project's standing rule is to leave a feature out rather than add a dependency for it.
/// A DNS query is a header, a name and two 16-bit fields (RFC 1035 §4), and the only part with any
/// subtlety is message compression when skipping over names — about a hundred lines, against a
/// package this deployment would then carry for ever.
///
/// It is NOT a general-purpose resolver and does not pretend to be: no EDNS0, no TCP fallback for
/// a truncated answer, no DNSSEC validation. It asks the system's own resolvers, which are already
/// doing the recursion and the validation, and reads TXT strings out of the reply. That is the
/// whole job. A truncated reply is read as "nothing found", which fails the verification closed.
/// </summary>
public sealed class DnsTxtLookup : IDnsTxtLookup
{
    private const ushort TypeTxt = 16;
    private const ushort ClassInternet = 1;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(4);

    private readonly ILogger<DnsTxtLookup> _logger;
    private readonly IConfiguration _configuration;

    public DnsTxtLookup(ILogger<DnsTxtLookup> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];

        foreach (var server in Resolvers())
        {
            try
            {
                var answers = await QueryAsync(server, name.Trim().TrimEnd('.'), cancellationToken);
                if (answers != null) return answers;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Try the next resolver. One unreachable server is not an answer about the zone.
                _logger.LogDebug(ex, "TXT lookup for {Name} failed against {Server}", name, server);
            }
        }

        return [];
    }

    /// <summary>
    /// The machine's own resolvers first, then the two public ones as a fallback. The fallback
    /// matters in a container, where <c>GetIPProperties()</c> often reports nothing at all, and it
    /// is overridable (<c>Dns:Resolvers</c>) for a network that blocks outbound 53.
    /// </summary>
    private IEnumerable<IPAddress> Resolvers()
    {
        var configured = _configuration.GetSection("Dns:Resolvers").Get<string[]>();
        if (configured is { Length: > 0 })
        {
            foreach (var entry in configured)
                if (IPAddress.TryParse(entry, out var parsed)) yield return parsed;
            yield break;
        }

        var seen = new HashSet<string>();
        IEnumerable<IPAddress> system = [];
        try
        {
            system = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().DnsAddresses)
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the system DNS servers; falling back to the public resolvers");
        }

        foreach (var address in system)
            if (seen.Add(address.ToString())) yield return address;

        foreach (var fallback in new[] { "1.1.1.1", "8.8.8.8" })
            if (seen.Add(fallback)) yield return IPAddress.Parse(fallback);
    }

    /// <summary>Null means "this server did not answer"; an empty list means "it answered, nothing there".</summary>
    private static async Task<IReadOnlyList<string>?> QueryAsync(IPAddress server, string name, CancellationToken ct)
    {
        using var udp = new UdpClient(server.AddressFamily);
        udp.Connect(new IPEndPoint(server, 53));

        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var request = BuildQuery(id, name);
        await udp.SendAsync(request, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeout);

        UdpReceiveResult reply;
        try
        {
            reply = await udp.ReceiveAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // this resolver timed out
        }

        return ReadTxtAnswers(reply.Buffer, id);
    }

    private static byte[] BuildQuery(ushort id, string name)
    {
        var body = new List<byte>(64)
        {
            (byte)(id >> 8), (byte)(id & 0xFF),
            0x01, 0x00,             // recursion desired
            0x00, 0x01,             // one question
            0x00, 0x00,             // no answers
            0x00, 0x00,             // no authority
            0x00, 0x00              // no additional
        };

        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63) throw new ArgumentException("DNS label too long", nameof(name));
            body.Add((byte)bytes.Length);
            body.AddRange(bytes);
        }
        body.Add(0x00); // root

        body.Add((byte)(TypeTxt >> 8)); body.Add((byte)(TypeTxt & 0xFF));
        body.Add((byte)(ClassInternet >> 8)); body.Add((byte)(ClassInternet & 0xFF));
        return [.. body];
    }

    private static IReadOnlyList<string>? ReadTxtAnswers(byte[] buffer, ushort expectedId)
    {
        if (buffer.Length < 12) return null;
        if (((buffer[0] << 8) | buffer[1]) != expectedId) return null;

        // TC (truncated) set: the real answer did not fit in a datagram and TCP fallback is not
        // implemented. Read it as "nothing found" rather than as a partial truth.
        if ((buffer[2] & 0x02) != 0) return [];

        var rcode = buffer[3] & 0x0F;
        if (rcode == 3) return [];          // NXDOMAIN: the name does not exist — an answer, not a failure
        if (rcode != 0) return null;        // SERVFAIL and friends: ask the next resolver

        var questions = (buffer[4] << 8) | buffer[5];
        var answers = (buffer[6] << 8) | buffer[7];

        var offset = 12;
        for (var q = 0; q < questions; q++)
        {
            if (!SkipName(buffer, ref offset)) return null;
            offset += 4;                    // QTYPE + QCLASS
        }

        var found = new List<string>();
        for (var a = 0; a < answers && offset < buffer.Length; a++)
        {
            if (!SkipName(buffer, ref offset)) return null;
            if (offset + 10 > buffer.Length) return null;

            var type = (buffer[offset] << 8) | buffer[offset + 1];
            var length = (buffer[offset + 8] << 8) | buffer[offset + 9];
            offset += 10;
            if (offset + length > buffer.Length) return null;

            if (type == TypeTxt)
            {
                // A TXT record is one or more length-prefixed strings; a value over 255 bytes is
                // split across several and must be joined back, which is why this is not a single read.
                var end = offset + length;
                var text = new StringBuilder();
                var cursor = offset;
                while (cursor < end)
                {
                    int partLength = buffer[cursor++];
                    if (cursor + partLength > end) break;
                    text.Append(Encoding.ASCII.GetString(buffer, cursor, partLength));
                    cursor += partLength;
                }
                found.Add(text.ToString());
            }

            offset += length;
        }

        return found;
    }

    /// <summary>
    /// Walks past a name, following a compression pointer (RFC 1035 §4.1.4) exactly once — the
    /// pointer's own two bytes end the name, so there is nothing after it to read.
    /// </summary>
    private static bool SkipName(byte[] buffer, ref int offset)
    {
        while (true)
        {
            if (offset >= buffer.Length) return false;
            int length = buffer[offset];

            if ((length & 0xC0) == 0xC0)
            {
                offset += 2;
                return offset <= buffer.Length;
            }

            offset++;
            if (length == 0) return true;
            offset += length;
        }
    }
}

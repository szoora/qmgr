using System.Security.Cryptography;
using System.Text;
using QMgr.Application.DTOs;
using QMgr.Domain.Payments;

namespace QMgr.Application.Import.Programme;

/// <summary>A member of staff as the name resolver sees them.</summary>
public sealed record StaffCandidate
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? EmployeeNumber { get; init; }
    public string? Email { get; init; }
    public string? Username { get; init; }
    /// <summary>
    /// A one-way key of the person's phone (<see cref="StaffNameResolver.PhoneKey"/>), never the number: the page
    /// can recognise "the number in the document is this person's" without every colleague's number being sent to it.
    /// </summary>
    public string? PhoneKey { get; init; }
    public string? JobTitle { get; init; }
    public List<Guid> DepartmentIds { get; init; } = new();
    public bool IsActive { get; init; } = true;
}

/// <summary>Which rung of the ladder (plan §6) produced a match.</summary>
public enum NameMatchRung
{
    Alias = 0,
    Code = 1,
    Phone = 2,
    SameWords = 3,
    Initials = 4,
    Similar = 5,
    Ambiguous = 6,
    NotFound = 7
}

public enum NameMatchVerdict
{
    /// <summary>Certain enough to use without asking.</summary>
    Match = 0,
    /// <summary>Probably this person — confirm.</summary>
    Suggest = 1,
    /// <summary>Several people fit, or the name is one word — choose.</summary>
    Ask = 2,
    /// <summary>Nobody on the staff list fits.</summary>
    NotFound = 3
}

public sealed record NameMatchCandidate(Guid UserId, string FullName, NameMatchRung Rung, double Score, string Reason);

public sealed record NameMatchResult
{
    public string Written { get; init; } = string.Empty;
    public NameMatchVerdict Verdict { get; init; }
    public NameMatchRung Rung { get; init; }
    /// <summary>Set when <see cref="Verdict"/> is Match.</summary>
    public Guid? UserId { get; init; }
    public List<NameMatchCandidate> Candidates { get; init; } = new();
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Matching a name as a document writes it to a person on the staff list — plan §6, the one home for it.
/// NEVER SPLITS A NAME and never trusts word order: "MRS. KARUGABA GRACE TUSHABE", "Karugaba Grace T." and
/// "Grace Tushabe" are compared as sets of words.
///
/// <list type="number">
/// <item>Staff number, email or username written exactly.</item>
/// <item>Phone, through <see cref="UgandaPhone"/> — which restores a leading zero a spreadsheet dropped.</item>
/// <item>The same set of words once honorifics are gone.</item>
/// <item>Initials agree: "Grace T." ↔ "Grace Tushabe".</item>
/// <item>Most words agree, each at Jaro–Winkler ≥ 0.92 → SUGGEST (the reader confirms). "Married name?" when the
/// document's words are all there and the directory has one more, or one surname differs.</item>
/// <item>A single word, or several people fit → ASK.</item>
/// <item>Nobody → NOT FOUND.</item>
/// </list>
/// A confirmed answer becomes an alias (<see cref="ImportAliasesDto.People"/>) so the next import does not ask again.
/// </summary>
public static class StaffNameResolver
{
    public const double WordThreshold = 0.92;

    /// <summary>The key a learned alias is stored under: <c>ImportMatching.NameKey</c> of the name without its titles.</summary>
    public static string AliasKey(string? written) => ImportMatching.NameKey(ProgrammeText.StripTitles(written)) ?? string.Empty;

    /// <summary>
    /// A one-way key of a phone number, salted with the organization: SHA-256 of "org:256XXXXXXXXX", first 16 hex
    /// characters. Null when the number cannot be normalised.
    /// </summary>
    public static string? PhoneKey(Guid organizationId, string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var n = UgandaPhone.Normalize(phone);
        if (n.Length < 9) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{organizationId:N}:{n}"));
        return Convert.ToHexString(bytes, 0, 8);
    }

    public static NameMatchResult Resolve(string? written, string? phoneKey, IReadOnlyList<StaffCandidate> staff, ImportAliasesDto? aliases = null)
    {
        var text = (written ?? string.Empty).Trim();
        var result = new NameMatchResult { Written = text };
        var active = staff.Where(s => s.IsActive).ToList();
        if (text.Length == 0) return result with { Verdict = NameMatchVerdict.NotFound, Rung = NameMatchRung.NotFound, Reason = "No name written." };

        // ---- 0. An answer somebody gave before.
        if (aliases?.People != null && aliases.People.TryGetValue(AliasKey(text), out var aliasId))
        {
            var person = active.FirstOrDefault(s => s.UserId == aliasId);
            if (person != null)
                return Single(result, person, NameMatchRung.Alias, 1.0, "Matched before, and remembered.");
        }

        // ---- 1. A staff number, email or username written exactly.
        var code = text.ToUpperInvariant();
        var byCode = active.Where(s =>
            (!string.IsNullOrWhiteSpace(s.EmployeeNumber) && s.EmployeeNumber.Trim().ToUpperInvariant() == code)
            || (!string.IsNullOrWhiteSpace(s.Email) && s.Email.Trim().ToUpperInvariant() == code)
            || (!string.IsNullOrWhiteSpace(s.Username) && s.Username.Trim().ToUpperInvariant() == code)).ToList();
        if (byCode.Count == 1) return Single(result, byCode[0], NameMatchRung.Code, 1.0, "Staff number, email or username.");

        // ---- 2. The phone number.
        if (!string.IsNullOrEmpty(phoneKey))
        {
            var byPhone = active.Where(s => s.PhoneKey == phoneKey).ToList();
            if (byPhone.Count == 1) return Single(result, byPhone[0], NameMatchRung.Phone, 0.99, "Same phone number.");
        }

        var words = NameWords(text);
        if (words.Count == 0) return result with { Verdict = NameMatchVerdict.NotFound, Rung = NameMatchRung.NotFound, Reason = "No name written." };

        // ---- 3. The same words, in any order.
        var key = string.Join(' ', words.OrderBy(w => w, StringComparer.Ordinal));
        var same = active.Where(s => string.Join(' ', NameWords(s.FullName).OrderBy(w => w, StringComparer.Ordinal)) == key).ToList();
        if (same.Count == 1 && words.Count >= 2) return Single(result, same[0], NameMatchRung.SameWords, 0.98, "The same names, in a different order or with a title.");
        if (same.Count > 1)
            return result with
            {
                Verdict = NameMatchVerdict.Ask, Rung = NameMatchRung.Ambiguous,
                Candidates = same.Select(s => new NameMatchCandidate(s.UserId, s.FullName, NameMatchRung.SameWords, 0.98, "Same names")).ToList(),
                Reason = $"{same.Count} people on the staff list have exactly this name."
            };

        // ---- 4. Initials agree.
        if (words.Any(w => w.Length == 1) && words.Count(w => w.Length > 1) >= 1)
        {
            var byInitials = active.Where(s => InitialsAgree(words, NameWords(s.FullName))).ToList();
            if (byInitials.Count == 1 && words.Count >= 2)
                return Single(result, byInitials[0], NameMatchRung.Initials, 0.95, "The initial agrees with the full name on file.");
            if (byInitials.Count > 1)
                return result with
                {
                    Verdict = NameMatchVerdict.Ask, Rung = NameMatchRung.Ambiguous,
                    Candidates = byInitials.Select(s => new NameMatchCandidate(s.UserId, s.FullName, NameMatchRung.Initials, 0.9, "Initials agree")).ToList(),
                    Reason = "Several people fit these initials."
                };
        }

        // ---- 5. Most words agree.
        var similar = new List<NameMatchCandidate>();
        foreach (var s in active)
        {
            var theirs = NameWords(s.FullName);
            if (theirs.Count == 0) continue;
            var (agree, score, reason) = Compare(words, theirs);
            if (agree) similar.Add(new NameMatchCandidate(s.UserId, s.FullName, NameMatchRung.Similar, score, reason));
        }
        similar = similar.OrderByDescending(c => c.Score).ToList();

        // ---- 6. One word, or several fit: ask.
        if (words.Count == 1)
        {
            var oneWord = active.Where(s => NameWords(s.FullName).Any(w => w == words[0] || JaroWinkler.Similarity(w, words[0]) >= WordThreshold))
                .Select(s => new NameMatchCandidate(s.UserId, s.FullName, NameMatchRung.Ambiguous, 0.5, "Shares this one name")).ToList();
            return result with
            {
                Verdict = oneWord.Count == 0 ? NameMatchVerdict.NotFound : NameMatchVerdict.Ask,
                Rung = oneWord.Count == 0 ? NameMatchRung.NotFound : NameMatchRung.Ambiguous,
                Candidates = oneWord,
                Reason = oneWord.Count == 0 ? "Nobody on the staff list has this name." : "Only one name is written — choose who it is."
            };
        }
        if (similar.Count == 1)
            return result with
            {
                Verdict = NameMatchVerdict.Suggest, Rung = NameMatchRung.Similar, Candidates = similar,
                Reason = similar[0].Reason
            };
        if (similar.Count > 1)
            return result with
            {
                Verdict = NameMatchVerdict.Ask, Rung = NameMatchRung.Ambiguous, Candidates = similar,
                Reason = $"{similar.Count} people could be \"{text}\" — choose."
            };

        // ---- 7. Nobody.
        return result with { Verdict = NameMatchVerdict.NotFound, Rung = NameMatchRung.NotFound, Reason = "Nobody on the staff list has this name." };
    }

    /// <summary>
    /// Two written names that may be one person ("Ms Asiimwe Aisha Peace" in one rota, "MRS. TWINOMUJUNI AISHA ASIIMWE
    /// PEACE" in another). Not identical, but most words agree — the reason says what differs.
    /// </summary>
    public static (bool Likely, string Reason) Likeness(string? a, string? b)
    {
        var wa = NameWords(a);
        var wb = NameWords(b);
        if (wa.Count < 2 || wb.Count < 2) return (false, string.Empty);
        if (string.Join(' ', wa.OrderBy(w => w, StringComparer.Ordinal)) == string.Join(' ', wb.OrderBy(w => w, StringComparer.Ordinal)))
            return (false, string.Empty);
        if ((wa.Any(w => w.Length == 1) || wb.Any(w => w.Length == 1)) && (InitialsAgree(wa, wb) || InitialsAgree(wb, wa)))
            return (true, "The initials agree with the full name.");
        var (agree, _, reason) = Compare(wa, wb);
        return (agree, reason);
    }

    /// <summary>A name's words: titles gone, upper case, letters and digits.</summary>
    public static List<string> NameWords(string? name)
        => ProgrammeText.Words(ProgrammeText.StripTitles(name)).Where(w => !Honorifics.IsHonorific(w)).ToList();

    private static NameMatchResult Single(NameMatchResult result, StaffCandidate person, NameMatchRung rung, double score, string reason)
        => result with
        {
            Verdict = NameMatchVerdict.Match, Rung = rung, UserId = person.UserId, Reason = reason,
            Candidates = new List<NameMatchCandidate> { new(person.UserId, person.FullName, rung, score, reason) }
        };

    /// <summary>Every full word of the document's name is on file, and every initial matches a different remaining word.</summary>
    private static bool InitialsAgree(List<string> written, List<string> onFile)
    {
        if (onFile.Count == 0 || onFile.Count > written.Count + 1) return false;
        var remaining = new List<string>(onFile);
        foreach (var w in written.Where(w => w.Length > 1))
        {
            var hit = remaining.FirstOrDefault(r => r == w);
            if (hit == null) return false;
            remaining.Remove(hit);
        }
        foreach (var initial in written.Where(w => w.Length == 1))
        {
            var hit = remaining.FirstOrDefault(r => r[0] == initial[0]);
            if (hit == null) return false;
            remaining.Remove(hit);
        }
        return true;
    }

    /// <summary>
    /// "Most words agree": at least two words, and at least two thirds of the shorter name's words, each find a
    /// word on the other side at Jaro–Winkler ≥ 0.92. The reason says what differs, because that is what the reader
    /// needs to decide — a married name looks exactly like this.
    /// </summary>
    private static (bool Agree, double Score, string Reason) Compare(List<string> written, List<string> onFile)
    {
        var remaining = new List<string>(onFile);
        var agreed = new List<string>();
        var unmatched = new List<string>();
        double total = 0;
        foreach (var w in written.Where(w => w.Length > 1))
        {
            string? best = null;
            double bestScore = 0;
            foreach (var r in remaining)
            {
                var sc = w == r ? 1.0 : JaroWinkler.Similarity(w, r);
                if (sc > bestScore) { bestScore = sc; best = r; }
            }
            if (best != null && bestScore >= WordThreshold)
            {
                agreed.Add(w);
                remaining.Remove(best);
                total += bestScore;
            }
            else unmatched.Add(w);
        }

        var fullWritten = written.Count(w => w.Length > 1);
        var shorter = Math.Min(fullWritten, onFile.Count);
        var needed = Math.Max(2, (int)Math.Ceiling(shorter * 2.0 / 3.0));
        if (agreed.Count < needed) return (false, 0, string.Empty);

        var score = Math.Round(total / Math.Max(fullWritten, onFile.Count), 3);
        string reason;
        if (unmatched.Count == 0 && remaining.Count > 0)
            reason = $"{Title(agreed)} agree; the staff list also has {Title(remaining)} — a married name?";
        else if (unmatched.Count > 0 && remaining.Count > 0)
            reason = $"{Title(agreed)} agree; \"{Title(unmatched)}\" here, \"{Title(remaining)}\" on file — a married name, or somebody else?";
        else if (unmatched.Count > 0)
            reason = $"{Title(agreed)} agree; \"{Title(unmatched)}\" is not on file.";
        else
            reason = "The names are spelt slightly differently.";
        return (true, score, reason);
    }

    private static string Title(IEnumerable<string> words)
        => string.Join(' ', words.Select(w => w.Length <= 1 ? w : w[0] + w[1..].ToLowerInvariant()));
}

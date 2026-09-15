using QMgr.Infrastructure.Services.Storage;

namespace QMgr.Infrastructure.Data;

/// <summary>
/// Moves an existing upload store out of the API's wwwroot into the gated store.
///
/// Until 2026-09-15 every upload was written to wwwroot/uploads/media, where the static-file
/// middleware served it before authentication ran. The store is now outside wwwroot (see
/// <see cref="LocalDiskMediaStorageService.ResolveStoreDirectory"/>) and served by
/// <c>UploadsController</c>. This carries the files an existing install already holds across, so
/// nothing that was uploaded before the change goes missing — the stored URLs are unchanged, only
/// the bytes' home moved.
///
/// Runs at startup, after the schema is ready and before anything can serve a file. Idempotent: a
/// second run finds an empty legacy folder and does nothing. A failure is logged, never fatal — the
/// API starting with one stray file left behind is better than the API not starting.
///
/// In production the legacy folder is a symlink to the persistent uploads directory that
/// install.sh created; that link is what made the files reachable through wwwroot, so it is
/// removed rather than walked (the target IS the new store). install.sh removes it too; this is
/// the belt to that brace, for an install that has not been re-run yet.
/// </summary>
public class UploadStoreRelocation
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<UploadStoreRelocation> _logger;

    public UploadStoreRelocation(IConfiguration configuration, IWebHostEnvironment environment, ILogger<UploadStoreRelocation> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public void Run()
    {
        try
        {
            var store = LocalDiskMediaStorageService.ResolveStoreDirectory(_configuration, _environment);
            var legacy = LocalDiskMediaStorageService.LegacyStoreDirectory(_environment);
            var legacyRoot = Path.GetDirectoryName(legacy)!; // wwwroot/uploads

            Directory.CreateDirectory(store);

            // Production shape: wwwroot/uploads -> $UploadsPath (a symlink). Removing the link is
            // the whole fix; the files stay exactly where they are and the store already points
            // at them. Directory.Delete on a symlink removes the link, not what it points to.
            var rootInfo = new DirectoryInfo(legacyRoot);
            if (rootInfo.Exists && rootInfo.LinkTarget != null)
            {
                try
                {
                    rootInfo.Delete();
                    _logger.LogInformation("Removed the wwwroot/uploads symlink; uploads are now served only through the gated store at {Store}", store);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "wwwroot/uploads is a symlink that could not be removed; run install.sh so the static-file path is closed");
                }
                return;
            }

            if (!Directory.Exists(legacy)) return;

            if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(store), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("MediaStorage:LocalPath points INSIDE wwwroot ({Store}); uploads there are served without any authorisation check", store);
                return;
            }

            var moved = 0;
            var skipped = 0;
            foreach (var file in Directory.EnumerateFiles(legacy))
            {
                var target = Path.Combine(store, Path.GetFileName(file));
                if (File.Exists(target))
                {
                    // The same upload on both sides (a half-finished earlier run). The copy in the
                    // gated store wins; the exposed one is deleted so it stops being servable.
                    File.Delete(file);
                    skipped++;
                    continue;
                }
                File.Move(file, target);
                moved++;
            }

            if (!Directory.EnumerateFileSystemEntries(legacy).Any())
            {
                Directory.Delete(legacy);
                if (Directory.Exists(legacyRoot) && !Directory.EnumerateFileSystemEntries(legacyRoot).Any())
                    Directory.Delete(legacyRoot);
            }

            if (moved > 0 || skipped > 0)
                _logger.LogInformation("Upload store relocation: moved {Moved} file(s) from wwwroot into {Store}, removed {Skipped} duplicate(s)", moved, store, skipped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload store relocation failed; files left under wwwroot/uploads remain reachable without authorisation until it succeeds");
        }
    }
}

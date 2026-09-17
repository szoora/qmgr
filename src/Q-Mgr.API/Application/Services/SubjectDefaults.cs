using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Staff;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// The subject catalogue a school starts with (duty rota plan §5.2), and the one seeder of it. Called on the
/// first read of the Subjects page AND by the staff import before it applies a Teaches column, because an
/// import can run before anybody has opened that page.
/// </summary>
public static class SubjectDefaults
{
    public static readonly IReadOnlyList<SaveSubjectRequest> Catalogue = new List<SaveSubjectRequest>
    {
        // Uganda's lower-secondary curriculum (NCDC, 2020). Tenant-editable; retired rather than deleted.
        new() { Name = "English Language", Code = "ENG", Color = "#7a2847", SortOrder = 1 },
        new() { Name = "Mathematics", Code = "MATH", Color = "#1f6f8b", SortOrder = 2 },
        new() { Name = "Physics", Code = "PHY", Color = "#3d5a80", SortOrder = 3 },
        new() { Name = "Chemistry", Code = "CHEM", Color = "#5a9c92", SortOrder = 4 },
        new() { Name = "Biology", Code = "BIO", Color = "#4f7942", SortOrder = 5 },
        new() { Name = "Geography", Code = "GEO", Color = "#8a6d3b", SortOrder = 6 },
        new() { Name = "History and Political Education", Code = "HPE", Color = "#8c2f52", SortOrder = 7 },
        new() { Name = "Christian Religious Education", Code = "CRE", Color = "#6b5b95", SortOrder = 8 },
        new() { Name = "Islamic Religious Education", Code = "IRE", Color = "#2e7d32", SortOrder = 9 },
        new() { Name = "Kiswahili", Code = "KIS", Color = "#b5651d", SortOrder = 10 },
        new() { Name = "Physical Education", Code = "PE", Color = "#c0392b", SortOrder = 11 },
        new() { Name = "Entrepreneurship", Code = "ENT", Color = "#d4a017", SortOrder = 12 },
        new() { Name = "Agriculture", Code = "AGR", Color = "#556b2f", SortOrder = 13 },
        new() { Name = "Information and Communication Technology", Code = "ICT", Color = "#34495e", SortOrder = 14 },
        new() { Name = "Literature in English", Code = "LIT", Color = "#7d3c98", SortOrder = 15 },
        new() { Name = "Art and Design", Code = "ART", Color = "#e67e22", SortOrder = 16 },
        new() { Name = "Performing Arts", Code = "PA", Color = "#a93226", SortOrder = 17 },
        new() { Name = "Nutrition and Food Technology", Code = "NFT", Color = "#28b463", SortOrder = 18 },
        new() { Name = "Technology and Design", Code = "TD", Color = "#566573", SortOrder = 19 },
        new() { Name = "Local Language", Code = "LL", Color = "#6e2c00", SortOrder = 20 },
        new() { Name = "French", Code = "FRE", Color = "#1a5276", SortOrder = 21 },
    };

    /// <summary>Seeds the catalogue under an advisory lock when the organization has no subject rows at all (retired included).</summary>
    public static async Task SeedIfEmptyAsync(QMgrDbContext db, Guid organizationId, ILogger logger, CancellationToken ct = default)
    {
        if (await db.Subjects.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == organizationId, ct)) return;

        // Called from a request (no transaction yet) and from inside the staff import and a join approval, which
        // may already hold one. The retrying execution strategy refuses a user transaction opened outside it, so
        // a fresh one goes through the strategy; an existing one is joined, and the xact lock lives as long as it.
        if (db.Database.CurrentTransaction != null)
        {
            await SeedLockedAsync();
            return;
        }
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await SeedLockedAsync();
            await tx.CommitAsync(ct);
        });

        async Task SeedLockedAsync()
        {
            var lockKey = $"subjects-seed:{organizationId}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)", ct);
            if (!await db.Subjects.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == organizationId, ct))
            {
                var added = new List<Subject>();
                foreach (var request in Catalogue)
                {
                    var subject = new Subject
                    {
                        OrganizationId = organizationId,
                        Name = request.Name,
                        Code = request.Code,
                        Color = request.Color,
                        SortOrder = request.SortOrder
                    };
                    db.Subjects.Add(subject);
                    added.Add(subject);
                }
                await db.SaveChangesAsync(ct);
                foreach (var subject in added) db.Entry(subject).State = EntityState.Detached;
                logger.LogInformation("Seeded the default subject catalogue for organization {OrganizationId}", organizationId);
            }
        }
    }
}

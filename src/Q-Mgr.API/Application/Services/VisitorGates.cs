using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Controllers.v1;
using QMgr.Application;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Visitor;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// The API's side of the gate rule (plan TERM_PROGRAMME_CALENDAR_AND_GATES §10). The decision itself is
/// <see cref="VisitorGateRule"/> in Shared, which the Web runs before it posts; this file only reads the branch's
/// list, words the refusal as a field error, and names the people who admitted and saw out a visitor.
///
/// <para>Every check-in and check-out path calls <see cref="ResolveAsync"/> — the desk walk-in, the pre-registered
/// arrival and the badge scan. A path that sets <c>CheckedInAt</c> without it records no gate, which is the gap
/// this exists to close.</para>
/// </summary>
public static class VisitorGates
{
    /// <summary>The branch's gates, active and retired, in the list's order. Stored with the branch lists.</summary>
    public static List<VocabularyItemDto> Read(string? branchSettingsJson) =>
        StudentsController.ReadVocabularies(branchSettingsJson).Gates.OrderBy(g => g.SortOrder).ToList();

    /// <summary>Reads the branch's gates and resolves the one a visit records, or the refusal sentence.</summary>
    public static async Task<(string? Gate, string? Error)> ResolveAsync(QMgrDbContext db, Guid branchId, string? requested, bool leaving = false)
    {
        var json = await db.Branches.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        return VisitorGateRule.Resolve(Read(json), requested, leaving);
    }

    /// <summary>A 400 filed under the <c>Gate</c> field, with the sentence as its title so a toast reads cleanly.</summary>
    public static IActionResult Refusal(string error) =>
        new BadRequestObjectResult(new ValidationProblemDetails(new Dictionary<string, string[]>
        {
            [VisitorGateRule.Field] = new[] { error }
        })
        {
            Title = error,
            Status = StatusCodes.Status400BadRequest
        });

    /// <summary>
    /// The names of the staff who admitted and saw out these visits, written the way their organisation writes a
    /// name (<see cref="PersonNames"/>). Ignores the tenant filter because the ids came off this branch's own
    /// visits — a platform administrator checking somebody in belongs to no tenant.
    /// </summary>
    public static Task<Dictionary<Guid, string>> StaffNamesAsync(QMgrDbContext db, IEnumerable<Visitor> visits) =>
        StaffNamesAsync(db, visits.SelectMany(v => new[] { v.CheckedInByUserId, v.CheckedOutByUserId }));

    /// <summary>The same lookup over bare user ids — for the report's export, which reads rows rather than visits.</summary>
    public static async Task<Dictionary<Guid, string>> StaffNamesAsync(QMgrDbContext db, IEnumerable<Guid?> userIds)
    {
        var ids = userIds
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        var users = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.OrganizationId, u.FirstName, u.LastName, u.Username })
            .ToListAsync();

        return users.ToDictionary(u => u.Id, u => PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName, u.Username));
    }
}

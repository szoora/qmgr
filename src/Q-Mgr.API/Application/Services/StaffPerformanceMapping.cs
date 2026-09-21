using System.Text.Json;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Audit;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Services.Storage;

namespace QMgr.API.Application.Services;

/// <summary>
/// Hand-written entity → DTO mapping for the Staff Performance module, in ONE place so the five
/// controllers and the portal cannot drift from each other (there is no auto-mapper in this
/// project; a mismatched field is a silent runtime bug). Names are resolved from a dictionary the
/// caller loads once (<see cref="NameLookup"/>), never per row.
///
/// Every upload link that leaves through here is SIGNED (<see cref="UploadLinks.Sign"/>); every
/// write that accepts one back must strip it. JSON columns are parsed here and only here.
/// </summary>
public static class StaffPerformanceMapping
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>userId → display name, loaded once per request by the caller.</summary>
    public sealed class NameLookup
    {
        private readonly IReadOnlyDictionary<Guid, string> _names;
        public NameLookup(IReadOnlyDictionary<Guid, string> names) => _names = names;
        public string this[Guid? id] => id.HasValue && _names.TryGetValue(id.Value, out var n) ? n : string.Empty;
        public string? Optional(Guid? id) => id.HasValue && _names.TryGetValue(id.Value, out var n) ? n : null;
        public static readonly NameLookup Empty = new(new Dictionary<Guid, string>());
    }

    public static string FullName(User u) => $"{u.FirstName} {u.LastName}".Trim() is { Length: > 0 } n ? n : u.Username;

    // ---- Structure -----------------------------------------------------------------------------

    public static DepartmentDto ToDto(Department d, NameLookup names, int memberCount) => new()
    {
        Id = d.Id,
        BranchId = d.BranchId,
        Name = d.Name,
        Code = d.Code,
        HeadUserId = d.HeadUserId,
        HeadName = names.Optional(d.HeadUserId),
        DeputyHeadUserId = d.DeputyHeadUserId,
        DeputyHeadName = names.Optional(d.DeputyHeadUserId),
        MemberCount = memberCount,
        SortOrder = d.SortOrder,
        IsActive = d.IsActive
    };

    public static StaffMemberDto ToDto(User u, NameLookup names, IReadOnlyDictionary<Guid, string> departmentNames, StaffScoreDto? score = null, DateTime? lastRecordAt = null)
    {
        var deptIds = u.DepartmentIds?.ToList() ?? new List<Guid>();
        return new StaffMemberDto
        {
            UserId = u.Id,
            FullName = FullName(u),
            Email = u.Email ?? string.Empty,
            Username = u.Username,
            RoleCode = u.Role?.Code ?? string.Empty,
            RoleName = u.Role?.Name ?? string.Empty,
            RoleColor = u.Role?.Color,
            JobTitle = u.JobTitle,
            EmployeeNumber = u.EmployeeNumber,
            BranchId = u.AssignedBranchId,
            DepartmentIds = deptIds,
            DepartmentNames = deptIds.Select(id => departmentNames.TryGetValue(id, out var n) ? n : null).Where(n => n != null).Select(n => n!).ToList(),
            LineManagerUserId = u.LineManagerUserId,
            LineManagerName = names.Optional(u.LineManagerUserId),
            StaffGroup = RoleCodes.IsSupportStaff(u.Role?.Code) ? StaffGroup.SupportStaff : StaffGroup.TeachingStaff,
            IsActive = u.IsActive,
            CurrentBand = score?.Band,
            CurrentBandName = score?.BandName,
            CurrentComposite = score?.Composite,
            LastRecordAt = lastRecordAt
        };
    }

    // ---- Parameters -----------------------------------------------------------------------------

    public static PerformanceParameterDto ToDto(PerformanceParameter p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        Kind = p.Kind,
        AppliesTo = p.AppliesTo,
        DefaultPoints = p.DefaultPoints,
        MaxPointsPerEntry = p.MaxPointsPerEntry,
        MaxPointsPerPeriod = p.MaxPointsPerPeriod,
        Weight = p.Weight,
        RatingScale = p.RatingScale,
        Rubric = ParseList<string>(p.RubricJson),
        DefaultVisibility = p.DefaultVisibility,
        Purpose = p.Purpose,
        Color = p.Color,
        SortOrder = p.SortOrder,
        IsActive = p.IsActive,
        IsSystemSource = p.IsSystemSource,
        OffsetsParameterId = p.OffsetsParameterId
    };

    public static void Apply(PerformanceParameter p, SavePerformanceParameterRequest r)
    {
        p.Name = r.Name.Trim();
        p.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        p.Kind = r.Kind;
        p.AppliesTo = r.AppliesTo;
        // Wellbeing is never scored: no points, no weight, whatever the client sent.
        p.DefaultPoints = r.Kind == ParameterKind.Wellbeing ? null : r.DefaultPoints;
        p.MaxPointsPerEntry = r.Kind == ParameterKind.Wellbeing ? 0 : Math.Max(0, r.MaxPointsPerEntry);
        p.MaxPointsPerPeriod = r.Kind == ParameterKind.Wellbeing ? null : r.MaxPointsPerPeriod;
        p.Weight = r.Kind == ParameterKind.Wellbeing ? 0 : Math.Max(0, r.Weight);
        p.RatingScale = r.Kind == ParameterKind.Observation ? (r.RatingScale is > 1 ? r.RatingScale : 4) : null;
        p.RubricJson = r.Kind == ParameterKind.Observation && r.Rubric.Count > 0 ? JsonSerializer.Serialize(r.Rubric, Json) : null;
        p.DefaultVisibility = r.Kind == ParameterKind.Wellbeing && r.DefaultVisibility < WelfareVisibility.Confidential
            ? WelfareVisibility.Confidential
            : r.DefaultVisibility;
        p.Purpose = r.Purpose.Trim();
        p.Color = string.IsNullOrWhiteSpace(r.Color) ? null : r.Color.Trim();
        p.SortOrder = r.SortOrder;
        p.IsSystemSource = r.IsSystemSource;
        p.OffsetsParameterId = r.OffsetsParameterId == p.Id ? null : r.OffsetsParameterId;
    }

    // ---- Records --------------------------------------------------------------------------------

    public static StaffPerformanceRecordDto ToDto(StaffPerformanceRecord r, NameLookup names, int lateEntryThresholdDays, bool includeNotesAndAttachments = true)
    {
        var dto = new StaffPerformanceRecordDto
        {
            Id = r.Id,
            BranchId = r.BranchId,
            SubjectUserId = r.SubjectUserId,
            SubjectName = r.Subject != null ? FullName(r.Subject) : names[r.SubjectUserId],
            ParameterId = r.ParameterId,
            ParameterName = r.Parameter?.Name ?? string.Empty,
            ParameterKind = r.Parameter?.Kind ?? ParameterKind.Contribution,
            ParameterColor = r.Parameter?.Color,
            ParameterPurpose = r.Parameter?.Purpose ?? string.Empty,
            DutyId = r.DutyId,
            DutyTitle = r.Duty?.Title,
            Outcome = r.Outcome,
            Points = r.Points,
            Rating = r.Rating,
            RatingScale = r.Parameter?.RatingScale,
            Description = r.Description,
            OccurredAt = r.OccurredAt,
            Source = r.Source,
            Status = r.Status,
            Visibility = r.Visibility,
            LoggedByUserId = r.LoggedByUserId,
            LoggedByName = names[r.LoggedByUserId],
            AcknowledgedAt = r.AcknowledgedAt,
            CreatedAt = r.CreatedAt,
            IsLateEntry = (r.CreatedAt - r.OccurredAt).TotalDays > lateEntryThresholdDays,
            Notes = includeNotesAndAttachments
                ? r.Notes.OrderBy(n => n.CreatedAt).Select(n => ToDto(n, names)).ToList()
                : new List<StaffPerformanceNoteDto>(),
            Attachments = includeNotesAndAttachments
                ? r.Attachments.OrderBy(a => a.CreatedAt).Select(ToDto).ToList()
                : new List<StaffPerformanceAttachmentDto>()
        };
        return dto;
    }

    public static StaffPerformanceNoteDto ToDto(StaffPerformanceNote n, NameLookup names) => new()
    {
        Id = n.Id,
        Body = n.Body,
        AuthorUserId = n.AuthorUserId,
        AuthorName = names[n.AuthorUserId],
        Kind = n.Kind,
        CreatedAt = n.CreatedAt
    };

    public static StaffPerformanceAttachmentDto ToDto(StaffPerformanceAttachment a) => new()
    {
        Id = a.Id,
        FileUrl = UploadLinks.Sign(a.FileUrl) ?? a.FileUrl,
        FileName = a.FileName,
        ContentType = a.ContentType,
        FileSizeBytes = a.FileSizeBytes,
        UploadedByUserId = a.UploadedByUserId,
        CreatedAt = a.CreatedAt
    };

    // ---- Duties ---------------------------------------------------------------------------------

    public static StaffDutyDto ToDto(StaffDuty d, NameLookup names, Guid callerId, bool callerMayManage, int expectedCount, int markedCount, DutyOutcome? myOutcome, string? minutesUrl)
    {
        var recorders = d.RecorderUserIds?.ToList() ?? new List<Guid>();
        var expected = d.ExpectedUserIds?.ToList();
        var supervisors = d.SupervisorUserIds?.ToList() ?? new List<Guid>();
        var acks = ParseAcknowledgements(d.Acknowledgements);
        return new StaffDutyDto
        {
            Id = d.Id,
            BranchId = d.BranchId,
            ParameterId = d.ParameterId,
            ParameterName = d.Parameter?.Name ?? string.Empty,
            ParameterKind = d.Parameter?.Kind ?? ParameterKind.Duty,
            Title = d.Title,
            Description = d.Description,
            Location = d.Location,
            StartsAt = d.StartsAt,
            EndsAt = d.EndsAt,
            ExpectedUserIds = expected,
            ExpectedCount = expectedCount,
            RecorderUserIds = recorders,
            RecorderNames = recorders.Select(id => names[id]).Where(n => n.Length > 0).ToList(),
            RegisterOpenedAt = d.RegisterOpenedAt,
            RegisterClosedAt = d.RegisterClosedAt,
            RegisterClosedByName = names.Optional(d.RegisterClosedByUserId),
            MarkedCount = markedCount,
            MinutesMediaContentId = d.MinutesMediaContentId,
            MinutesFileUrl = minutesUrl == null ? null : UploadLinks.Sign(minutesUrl) ?? minutesUrl,
            CreatedByUserId = d.CreatedByUserId,
            IsActive = d.IsActive,
            IsExpectedOfMe = expected == null || expected.Contains(callerId),
            CanIRecord = callerMayManage || recorders.Contains(callerId),
            MyOutcome = myOutcome,
            Kind = d.Kind,
            SeriesId = d.SeriesId,
            // Names only resolve for ids the caller put in the lookup; an expected list of "everyone" has none.
            ExpectedNames = expected?.Select(id => names[id]).Where(n => n.Length > 0).ToList() ?? new List<string>(),
            SupervisorUserIds = supervisors,
            SupervisorNames = supervisors.Select(id => names[id]).Where(n => n.Length > 0).ToList(),
            ReportCadence = d.ReportCadence,
            ReportDueLocalTime = d.ReportDueLocalTime?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            IsSupervisedByMe = supervisors.Contains(callerId),
            MyAcknowledgedAt = acks.TryGetValue(callerId, out var mine) ? mine : null,
            AcknowledgedCount = acks.Keys.Count(k => expected?.Contains(k) ?? true),
            // Who has and has not acknowledged is the supervisor's and the duty manager's business, not a colleague's.
            Acknowledgements = callerMayManage || supervisors.Contains(callerId) ? acks : null,
            TimetableLessonId = d.TimetableLessonId,
            ClassName = d.ClassName,
            SubjectId = d.SubjectId,
            Room = d.Room,
            RecoversDutyId = d.RecoversDutyId
        };
    }

    // ---- Notices --------------------------------------------------------------------------------

    public static Dictionary<Guid, DateTime> ParseAcknowledgements(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<Guid, DateTime>();
        try { return JsonSerializer.Deserialize<Dictionary<Guid, DateTime>>(json, Json) ?? new(); }
        catch (JsonException) { return new Dictionary<Guid, DateTime>(); }
    }

    public static string SerializeAcknowledgements(Dictionary<Guid, DateTime> acks) => JsonSerializer.Serialize(acks, Json);

    public static StaffNoticeDto ToDto(StaffNotice n, NameLookup names, Guid callerId, int recipientCount, List<NoticeAttachmentDto> attachments)
    {
        var acks = ParseAcknowledgements(n.Acknowledgements);
        return new StaffNoticeDto
        {
            Id = n.Id,
            BranchId = n.BranchId,
            Title = n.Title,
            BodyHtml = n.BodyHtml,
            AudienceDepartmentIds = n.AudienceDepartmentIds?.ToList(),
            AudienceRoleCodes = n.AudienceRoleCodes?.ToList(),
            AudienceStaffGroup = n.AudienceStaffGroup,
            PublishAt = n.PublishAt,
            ExpiresAt = n.ExpiresAt,
            IsPinned = n.IsPinned,
            RequiresAcknowledgement = n.RequiresAcknowledgement,
            Attachments = attachments,
            PublishedByUserId = n.PublishedByUserId,
            PublishedByName = names[n.PublishedByUserId],
            NotificationsSentAt = n.NotificationsSentAt,
            RecipientCount = recipientCount,
            AcknowledgedCount = acks.Count,
            AcknowledgedByMeAt = acks.TryGetValue(callerId, out var at) ? at : null,
            IsActive = n.IsActive,
            CreatedAt = n.CreatedAt
        };
    }

    // ---- Appraisals -----------------------------------------------------------------------------

    public static StaffAppraisalDto ToDto(StaffAppraisal a, NameLookup names, string? subjectDepartments, string? finalRatingName, StaffScoreDto? liveScore, Guid callerId, bool callerMayApprove, bool callerMayConduct)
    {
        var isSubject = a.SubjectUserId == callerId;
        var isAppraiser = a.AppraiserUserId == callerId;
        return new StaffAppraisalDto
        {
            Id = a.Id,
            BranchId = a.BranchId,
            SubjectUserId = a.SubjectUserId,
            SubjectName = a.Subject != null ? FullName(a.Subject) : names[a.SubjectUserId],
            SubjectDepartmentNames = subjectDepartments,
            PeriodKey = a.PeriodKey,
            PeriodStart = a.PeriodStart,
            PeriodEnd = a.PeriodEnd,
            AppraiserUserId = a.AppraiserUserId,
            AppraiserName = a.Appraiser != null ? FullName(a.Appraiser) : names[a.AppraiserUserId],
            ModeratorUserId = a.ModeratorUserId,
            ModeratorName = names.Optional(a.ModeratorUserId),
            Stage = a.Stage,
            Targets = ParseList<AppraisalTargetDto>(a.TargetsJson),
            ComputedScore = a.ComputedScore,
            ComputedBreakdown = ParseList<ParameterScoreDto>(a.ComputedBreakdownJson),
            LiveScore = liveScore,
            SelfRatings = ParseList<ParameterRatingDto>(a.SelfRatingsJson),
            SelfRating = a.SelfRating,
            SelfComments = a.SelfComments,
            AppraiserRating = a.AppraiserRating,
            AppraiserComments = a.AppraiserComments,
            FinalRating = a.FinalRating,
            FinalRatingName = finalRatingName,
            Strengths = a.Strengths,
            DevelopmentAreas = a.DevelopmentAreas,
            SupportPlan = ParseList<SupportPlanItemDto>(a.SupportPlanJson),
            NextTargets = ParseList<AppraisalTargetDto>(a.NextTargetsJson),
            SelfSubmittedAt = a.SelfSubmittedAt,
            AppraiserSubmittedAt = a.AppraiserSubmittedAt,
            ModeratedAt = a.ModeratedAt,
            ModerationReason = a.ModerationReason,
            SignedAt = a.SignedAt,
            SignedByName = names.Optional(a.SignedByUserId),
            AppealNote = a.AppealNote,
            ReportMediaContentId = a.ReportMediaContentId,
            CreatedAt = a.CreatedAt,
            CanISelfAssess = isSubject && a.Stage is AppraisalStage.Open or AppraisalStage.SelfAssessment,
            CanIAppraise = (isAppraiser && callerMayConduct || callerMayApprove) && a.Stage is AppraisalStage.SelfAssessment or AppraisalStage.AppraiserReview,
            CanIModerate = callerMayApprove && a.Stage is AppraisalStage.Moderation or AppraisalStage.Appealed,
            CanIAppeal = isSubject && a.Stage == AppraisalStage.Signed
        };
    }

    // ---- Activity -------------------------------------------------------------------------------

    public static ActivityEventDto ToDto(ActivityEvent e, NameLookup names) => new()
    {
        Id = e.Id,
        ActorUserId = e.ActorUserId,
        ActorName = names.Optional(e.ActorUserId),
        SubjectUserId = e.SubjectUserId,
        SubjectName = names.Optional(e.SubjectUserId),
        Action = e.Action,
        EntityType = e.EntityType,
        EntityId = e.EntityId,
        Summary = e.Summary,
        Visibility = e.Visibility,
        IpAddress = e.IpAddress,
        UserAgent = e.UserAgent,
        OccurredAt = e.OccurredAt
    };

    // ---- Helpers --------------------------------------------------------------------------------

    public static List<T> ParseList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<T>();
        try { return JsonSerializer.Deserialize<List<T>>(json, Json) ?? new List<T>(); }
        catch (JsonException) { return new List<T>(); }
    }

    public static string? SerializeList<T>(List<T>? list) => list == null || list.Count == 0 ? null : JsonSerializer.Serialize(list, Json);

    /// <summary>The visibility rungs a caller may read, as a set (not a ladder): the welfare rule, on the staff axis.</summary>
    public static HashSet<WelfareVisibility> VisibleLevels(bool confidential, bool restricted)
    {
        var set = new HashSet<WelfareVisibility> { WelfareVisibility.Standard };
        if (confidential) set.Add(WelfareVisibility.Confidential);
        if (restricted) set.Add(WelfareVisibility.Restricted);
        return set;
    }
}

namespace QMgr.Domain.Enums
{
    /// <summary>
    /// Shape of a configurable survey question shown on the public feedback form, after the
    /// core 1-5 service rating. Lives here (in Q-Mgr.Shared) rather than in the API project
    /// because both the entity (Q-Mgr.API) and the DTOs/Razor pages (Q-Mgr.Web) need it —
    /// same SSoT rule the rest of this folder follows.
    /// </summary>
    public enum FeedbackQuestionType
    {
        /// <summary>1-5 star scale, same visual language as the core service rating.</summary>
        Rating = 0,

        /// <summary>Two-option yes/no toggle. Stored as the literal string "Yes"/"No".</summary>
        YesNo = 1,

        /// <summary>Exactly one choice from the question's configured Options list.</summary>
        SingleChoice = 2,

        /// <summary>Short free-text answer.</summary>
        FreeText = 3
    }
}

namespace QMgr.Application.DTOs
{
    using QMgr.Domain.Enums;

    public record FeedbackDto
    {
        public Guid Id { get; init; }
        public Guid BranchId { get; init; }
        public Guid? TokenId { get; init; }
        public Guid? ServiceTypeId { get; init; }
        public Guid? CounterId { get; init; }
        public Guid? ServedByUserId { get; init; }

        public string FeedbackCode { get; init; } = string.Empty;
        public int Rating { get; init; }

        /// <summary>
        /// Net Promoter Score answer, 0-10, null when the customer skipped the NPS step (it is
        /// always optional). Never average this raw — see FeedbackStatsDto.Nps.
        /// </summary>
        public int? NpsScore { get; init; }

        public string? Comment { get; init; }
        public FeedbackCategory Category { get; init; }
        public FeedbackSource Source { get; init; }

        public string? CustomerName { get; init; }
        public string? CustomerPhone { get; init; }
        public string? CustomerEmail { get; init; }
        public string? TokenDisplayNumber { get; init; }

        public DateTime? ServiceDate { get; init; }
        public DateTime CreatedAt { get; init; }

        // Response from staff
        public string? Response { get; init; }
        public DateTime? RespondedAt { get; init; }
        public Guid? RespondedByUserId { get; init; }

        /// <summary>
        /// The customer's answers to the configured survey questions. Deserialized from the
        /// feedback row's ResponsesJson column — deliberately NOT a separate answers table,
        /// since answers are only ever read back alongside their parent feedback.
        /// </summary>
        public List<FeedbackAnswerDto> QuestionAnswers { get; init; } = new();

        // Navigation properties for display
        public string? ServiceTypeName { get; init; }
        public string? CounterName { get; init; }
        public string? ServedByName { get; init; }
        public string? RespondedByName { get; init; }
    }

    /// <summary>
    /// One customer answer to one configured survey question. The question text and type are
    /// snapshotted at submission time so a later edit/delete of the question definition can't
    /// silently rewrite (or orphan) historical answers.
    /// </summary>
    public record FeedbackAnswerDto
    {
        public Guid QuestionId { get; init; }
        public string QuestionText { get; init; } = string.Empty;
        public FeedbackQuestionType QuestionType { get; init; }

        /// <summary>
        /// Answer as text: "1".."5" for Rating, "Yes"/"No" for YesNo, the chosen option label
        /// for SingleChoice, the raw text for FreeText.
        /// </summary>
        public string Answer { get; init; } = string.Empty;
    }

    // ---------------------------------------------------------------------------------------
    // Submission requests. NpsScore and Answers are both optional additions — an existing
    // feedback link that posts only Rating/Comment/Category keeps working unchanged.
    // ---------------------------------------------------------------------------------------

    public record SubmitFeedbackRequest
    {
        public int Rating { get; init; }
        public int? NpsScore { get; init; }
        public string? Comment { get; init; }
        public FeedbackCategory Category { get; init; } = FeedbackCategory.General;
        public string? CustomerName { get; init; }
        public string? CustomerPhone { get; init; }
        public string? CustomerEmail { get; init; }
        public List<SubmitFeedbackAnswer>? Answers { get; init; }
    }

    public record SubmitFeedbackByCodeRequest
    {
        public string FeedbackCode { get; init; } = string.Empty;
        public int Rating { get; init; }
        public int? NpsScore { get; init; }
        public string? Comment { get; init; }
        public FeedbackCategory Category { get; init; } = FeedbackCategory.General;
        public string? CustomerName { get; init; }
        public string? CustomerPhone { get; init; }
        public string? CustomerEmail { get; init; }
        public List<SubmitFeedbackAnswer>? Answers { get; init; }
    }

    /// <summary>Wire shape a public page posts: just the question id and the raw answer.</summary>
    public record SubmitFeedbackAnswer
    {
        public Guid QuestionId { get; init; }
        public string? Answer { get; init; }
    }

    public record RespondToFeedbackRequest
    {
        public string Response { get; init; } = string.Empty;
    }

    // ---------------------------------------------------------------------------------------
    // Survey question definitions
    // ---------------------------------------------------------------------------------------

    public record FeedbackQuestionDto
    {
        public Guid Id { get; init; }
        public Guid OrganizationId { get; init; }

        /// <summary>Null means the question applies to every branch in the organization.</summary>
        public Guid? BranchId { get; init; }

        public string QuestionText { get; init; } = string.Empty;
        public FeedbackQuestionType QuestionType { get; init; }

        /// <summary>Choice labels; only meaningful for <see cref="FeedbackQuestionType.SingleChoice"/>.</summary>
        public List<string> Options { get; init; } = new();

        /// <summary>Null means the question is asked for every service type.</summary>
        public Guid? ServiceTypeId { get; init; }
        public string? ServiceTypeName { get; init; }

        public int DisplayOrder { get; init; }
        public bool IsRequired { get; init; }
        public bool IsActive { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }
    }

    public record SaveFeedbackQuestionRequest
    {
        public string QuestionText { get; init; } = string.Empty;
        public FeedbackQuestionType QuestionType { get; init; } = FeedbackQuestionType.Rating;
        public List<string>? Options { get; init; }
        public Guid? ServiceTypeId { get; init; }

        /// <summary>Null on create appends to the end of the current list.</summary>
        public int? DisplayOrder { get; init; }

        public bool IsRequired { get; init; }
        public bool IsActive { get; init; } = true;

        /// <summary>
        /// True creates the question at organization scope (asked at every branch). Ignored on
        /// update — a question's scope is fixed once created.
        /// </summary>
        public bool AppliesToAllBranches { get; init; }
    }

    public record ReorderFeedbackQuestionsRequest
    {
        /// <summary>Question ids in the order they should be shown, first = DisplayOrder 0.</summary>
        public List<Guid> QuestionIds { get; init; } = new();
    }

    // ---------------------------------------------------------------------------------------
    // Analytics
    // ---------------------------------------------------------------------------------------

    public record FeedbackSummaryDto
    {
        public int TotalFeedbacks { get; init; }
        public double AverageRating { get; init; }
        public int FiveStarCount { get; init; }
        public int FourStarCount { get; init; }
        public int ThreeStarCount { get; init; }
        public int TwoStarCount { get; init; }
        public int OneStarCount { get; init; }
        public Dictionary<FeedbackCategory, int> CategoryBreakdown { get; init; } = new();
        public Dictionary<FeedbackSource, int> SourceBreakdown { get; init; } = new();
        public int PendingResponseCount { get; init; }
    }

    /// <summary>
    /// Richer analytics payload: everything FeedbackSummaryDto has, plus Net Promoter Score and
    /// per-survey-question aggregates.
    /// </summary>
    public record FeedbackStatsDto
    {
        /// <summary>Number of submitted feedback rows in range (Rating &gt; 0).</summary>
        public int ResponseCount { get; init; }

        public double AverageRating { get; init; }

        /// <summary>Star value (1-5) -> count. Always contains all five keys, zeros included.</summary>
        public Dictionary<int, int> RatingDistribution { get; init; } = new();

        // --- Net Promoter Score ---

        /// <summary>How many respondents actually answered the NPS question (the NPS denominator).</summary>
        public int NpsResponseCount { get; init; }
        public int PromoterCount { get; init; }
        public int PassiveCount { get; init; }
        public int DetractorCount { get; init; }

        /// <summary>
        /// NPS in the range -100..+100. See FeedbackController.CalculateNps for the formula.
        /// Null when nobody in range answered the NPS question — an NPS of "0" and "no data"
        /// are very different things and must not be conflated in the UI.
        /// </summary>
        public double? Nps { get; init; }

        public List<FeedbackQuestionAggregateDto> QuestionAggregates { get; init; } = new();
    }

    public record FeedbackQuestionAggregateDto
    {
        public Guid QuestionId { get; init; }
        public string QuestionText { get; init; } = string.Empty;
        public FeedbackQuestionType QuestionType { get; init; }

        /// <summary>How many respondents answered this specific question.</summary>
        public int ResponseCount { get; init; }

        /// <summary>Mean of the 1-5 answers; only set for <see cref="FeedbackQuestionType.Rating"/>.</summary>
        public double? AverageRating { get; init; }

        /// <summary>
        /// Answer label -> count. Populated for Rating ("1".."5"), YesNo ("Yes"/"No") and
        /// SingleChoice (option labels). Empty for FreeText.
        /// </summary>
        public Dictionary<string, int> AnswerCounts { get; init; } = new();

        /// <summary>Most recent free-text answers (capped), only for FreeText questions.</summary>
        public List<string> TextAnswers { get; init; } = new();
    }

    public record FeedbackLinkDto
    {
        public Guid TokenId { get; init; }
        public string FeedbackCode { get; init; } = string.Empty;
        public string FeedbackUrl { get; init; } = string.Empty;
        public string TokenDisplayNumber { get; init; } = string.Empty;
        public DateTime ExpiresAt { get; init; }
    }

    /// <summary>
    /// What the anonymous public feedback page gets when it resolves a feedback code. Shape is
    /// additive over what the endpoint already returned (BranchId/ServiceTypeId/Questions are
    /// new) so an older client reading only the original fields is unaffected.
    /// </summary>
    public record FeedbackCodeInfoDto
    {
        public string FeedbackCode { get; init; } = string.Empty;
        public Guid BranchId { get; init; }
        public Guid? ServiceTypeId { get; init; }
        public string? TokenDisplayNumber { get; init; }
        public string? ServiceTypeName { get; init; }
        public string? BranchName { get; init; }
        public DateTime? ServiceDate { get; init; }
        public bool AlreadySubmitted { get; init; }
        public int? ExistingRating { get; init; }
        public List<FeedbackQuestionDto> Questions { get; init; } = new();
    }
}

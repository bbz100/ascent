using Ascent.Web.Infrastructure;
namespace Ascent.Web.Planning;

public sealed class Expedition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New expedition";
    public bool TitleExtractionAttempted {get;set;}
    public string FileName { get; set; } = "";
    public string? OriginalDocumentFile {get;set;}
    public string DocumentText { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int Revision { get; set; }
    public Analysis? Analysis { get; set; }
    public List<Clarification> Questions { get; set; } = [];
    public List<Answer> Answers { get; set; } = [];
    public List<WorkItem> WorkItems { get; set; } = [];
    public List<StudyRoute> Routes { get; set; } = [];
    public List<DeadlineReview> DateReviews { get; set; } = [];
    public bool DatesReviewed { get; set; }
    public bool TasksReviewed { get; set; }
    public string? ApprovedRouteId { get; set; }
}

public sealed record Evidence(string ParagraphId = "", string Quote = "")
{
    public List<string> ParagraphIds { get; init; } = [];
}
public sealed record Requirement(string Id, string Title, [property: JsonChoices("task", "workshop", "deadline", "constraint", "deliverable")] string Kind, bool Mandatory, int Count, Evidence Source)
{
    public string Description { get; init; } = "";
    public List<AnswerCondition> AppliesWhen { get; init; } = [];
}
public sealed record Analysis(string ModuleTitle, string Summary, List<Requirement> Requirements,
    List<string> Warnings, List<Clarification> Questions)
{
    public int SchemaVersion { get; init; } = 1;
    public List<AssessmentDeadline> Deadlines { get; init; } = [];
    public string? InterviewWarning { get; init; }
}
public sealed record DeadlineReview(string DeadlineId, DateOnly? Date, string? Time, bool UseForPlan);
public sealed record DateReviewSubmission(List<DeadlineReview> Dates, bool TasksReviewed);
public sealed record Clarification(string Id, string GapKey, string Question, string Why,
    string AnswerHint, List<string> RequirementIds, Evidence Source)
{
    [JsonChoices("text", "date", "choice", "multi_choice", "fields")]
    public string InputType { get; init; } = "text";
    public List<QuestionOption> Options { get; init; } = [];
    public List<QuestionField> Fields { get; init; } = [];
    public int RepeatCount { get; init; } = 1;
    public List<AnswerCondition> AppliesWhen { get; init; } = [];
}
public sealed record AnswerCondition(string QuestionId, string OptionId);
public sealed record QuestionOption(string Id, string Label, string Hint);
public sealed record QuestionField(string Id, string Label, [property: JsonChoices("text", "date")] string Type, bool Required);
public sealed class QuestionValue
{
    public string FieldId { get; set; } = "";
    public int Row { get; set; }
    public string Text { get; set; } = "";
    public DateOnly? Date { get; set; }
}
public sealed record AssessmentDeadline(string Id, string Label, [property: JsonChoices("submission", "feedback", "other")] string Purpose,
    List<string> RequirementIds, DateOnly? Date, [property: JsonClockTime, System.Text.Json.Serialization.JsonConverter(typeof(PrintedTimeConverter))] string? Time, string? Timezone,
    string OriginalDateText, Evidence Source, List<AnswerCondition> AppliesWhen)
{
    [JsonChoices("day", "week", "month", "unknown")]
    public string DatePrecision { get; init; } = "day";
    public int? Occurrence { get; init; }
}
public sealed record Answer(string QuestionId, string GapKey, string Text, bool Unknown, QuestionResponse? Data = null);
public sealed class WorkshopDetail
{
    public string Title { get; set; } = "";
    public DateOnly? Date { get; set; }
}
public sealed class QuestionResponse
{
    public string Choice { get; set; } = "";
    public DateOnly? Date { get; set; }
    public List<WorkshopDetail> Workshops { get; set; } = [];
    public List<string> Items { get; set; } = [];
    public List<string> SelectedOptions { get; set; } = [];
    public List<QuestionValue> Values { get; set; } = [];
}
public sealed record QuestionSubmission(string Text, bool Unknown, QuestionResponse? Data);
public sealed record FollowUp(List<Clarification> Questions);
public sealed class WorkItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string RequirementId { get; set; } = "";
    public int Occurrence { get; set; } = 1;
    public List<string> DependsOn { get; set; } = [];
    public bool Placeholder { get; set; }
    public DateOnly? DueDate { get; set; }
    public int? EstimatedMinutes { get; set; }
    public string? DateEvidence { get; set; }
    public string? DeadlineId { get; set; }
    public bool KeepSeparate { get; set; }
}
public sealed record WorkPlan(List<WorkItem> Items);
public sealed class StudyRoute
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<Milestone> Milestones { get; set; } = [];
}
public sealed class Milestone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public List<string> WorkItemIds { get; set; } = [];
    public List<string> DependsOn { get; set; } = [];
    public bool Complete { get; set; }
    public string Notes { get; set; } = "";
    public bool Optional { get; set; }
}
public sealed record Paragraph(string Id, string Text);

public sealed class PlanningException(string message) : Exception(message);

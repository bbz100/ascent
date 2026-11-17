namespace Ascent.Web.Planning;

// Evaluate model-supplied references, never assessment terminology or wording.
public static class QuestionFlow
{
    public static bool? Applies(List<AnswerCondition> conditions, Expedition expedition)
    {
        bool unresolved = false;
        foreach (var condition in conditions)
        {
            var answer = expedition.Answers.SingleOrDefault(a => a.QuestionId == condition.QuestionId);
            if (answer is null || answer.Unknown) { unresolved = true; continue; }
            if (!(answer.Data?.SelectedOptions ?? []).Contains(condition.OptionId) && answer.Data?.Choice != condition.OptionId) return false;
        }
        return unresolved ? null : true;
    }
    public static List<Clarification> Pending(Expedition expedition) => expedition.Questions
        .Where(q => !expedition.Answers.Any(a => a.QuestionId == q.Id) && Applies(q.AppliesWhen, expedition) == true).ToList();
    public static List<Requirement> IncludedRequirements(Expedition expedition) => expedition.Analysis!.Requirements
        .Where(r => Applies(r.AppliesWhen, expedition) != false).ToList();
    public static List<AssessmentDeadline> SelectedDeadlines(Expedition expedition) => expedition.Analysis!.Deadlines
        .Where(d => expedition.DatesReviewed
            ? expedition.DateReviews.Any(r => r.DeadlineId == d.Id && r.UseForPlan && r.Date is not null)
            : Applies(d.AppliesWhen, expedition) == true)
        .Select(d => expedition.DatesReviewed
            ? d with { Date=expedition.DateReviews.Single(r=>r.DeadlineId==d.Id).Date,
                Time=expedition.DateReviews.Single(r=>r.DeadlineId==d.Id).Time, DatePrecision="day", AppliesWhen=[] }
            : d).ToList();
}

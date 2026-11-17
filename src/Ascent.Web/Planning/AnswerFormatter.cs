namespace Ascent.Web.Planning;

public static class AnswerFormatter
{
    public static string ValidateAndFormat(Clarification question, Analysis analysis, QuestionResponse response)
    {
        if (response.Items is null || response.SelectedOptions is null || response.Values is null ||
            response.Items.Count > 30 || response.Items.Any(i => i is null || i.Length > 1000))
            throw new PlanningException("Keep each answer within the field limits.");
        switch (question.InputType)
        {
            case "choice":
            case "multi_choice":
                var selected = response.SelectedOptions.Count > 0 ? response.SelectedOptions : new List<string> { response.Choice };
                if (selected.Count == 0 || selected.Distinct().Count() != selected.Count ||
                    (question.InputType == "choice" && selected.Count != 1) ||
                    selected.Any(id => !question.Options.Any(o => o.Id == id)))
                    throw new PlanningException("Choose one of the supplied answers, or ‘I don’t know yet’.");
                response.SelectedOptions = selected;
                response.Choice = question.InputType == "choice" ? selected[0] : "";
                return string.Join("; ", selected.Select(id => question.Options.Single(o => o.Id == id).Label));
            case "date":
                return response.Date?.ToString("yyyy-MM-dd") ?? throw new PlanningException("Choose a date, or ‘I don’t know yet’.");
            case "fields":
                if (response.Values.Count != question.Fields.Count * question.RepeatCount ||
                    response.Values.Select(v => (v.FieldId, v.Row)).Distinct().Count() != response.Values.Count)
                    throw new PlanningException("The answer fields do not match this question.");
                foreach (var value in response.Values)
                {
                    var field = question.Fields.SingleOrDefault(f => f.Id == value.FieldId);
                    if (field is null || value.Row < 0 || value.Row >= question.RepeatCount || value.Text is null || value.Text.Length > 1000 ||
                        (field.Required && (field.Type == "date" ? value.Date is null : string.IsNullOrWhiteSpace(value.Text))))
                        throw new PlanningException("Complete the required fields, or ‘I don’t know yet’.");
                }
                var lines = response.Values.Where(v => v.Date is not null || !string.IsNullOrWhiteSpace(v.Text))
                    .Select(v => $"{question.Fields.Single(f => f.Id == v.FieldId).Label}{(question.RepeatCount > 1 ? $" #{v.Row + 1}" : "")}: {(question.Fields.Single(f => f.Id == v.FieldId).Type == "date" ? v.Date?.ToString("yyyy-MM-dd") : v.Text.Trim())}").ToList();
                if (lines.Count == 0) throw new PlanningException("Add what you know, or ‘I don’t know yet’.");
                return string.Join('\n', lines);
            case "text":
                return response.Items.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i))?.Trim()
                    ?? throw new PlanningException("Enter an answer or choose ‘I don’t know yet’.");
            default: throw new PlanningException("This question uses an unsupported input format. Please analyse the brief again.");
        }
    }
}

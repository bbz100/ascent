using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ascent.Web.Planning;

namespace Ascent.Web.Infrastructure;

// Week notation belongs to a deadline, not to an exact date entered by a user.
// Preserve its precision while allowing the calendar-date converter to read the anchor.
public sealed class PrintedDeadlineConverter : JsonConverter<AssessmentDeadline>
{
    static readonly Regex WeekPrefix = new(
        @"^\s*(?:w\s*/\s*c\b|w\.c\.|week\s+(?:commencing|beginning|starting)\b)\s*:?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static JsonSerializerOptions Inner(JsonSerializerOptions options)
    {
        var inner = new JsonSerializerOptions(options);
        for (var i = inner.Converters.Count - 1; i >= 0; i--)
            if (inner.Converters[i] is PrintedDeadlineConverter) inner.Converters.RemoveAt(i);
        return inner;
    }

    public override AssessmentDeadline Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("Expected a deadline object.");
        var date = node["date"] is JsonValue value && value.TryGetValue<string>(out var printed) ? printed : null;
        var original = node["originalDateText"] is JsonValue source && source.TryGetValue<string>(out var phrase) ? phrase : null;
        var datePrefix = date is null ? Match.Empty : WeekPrefix.Match(date);
        if (datePrefix.Success)
        {
            // Do not round, calculate a weekday, or accept an incomplete calendar date.
            node["date"] = date![datePrefix.Length..];
            node["datePrecision"] = "week";
        }
        if (original is not null && WeekPrefix.IsMatch(original)) node["datePrecision"] = "week";
        return node.Deserialize<AssessmentDeadline>(Inner(options)) ?? throw new JsonException("Expected a deadline.");
    }

    public override void Write(Utf8JsonWriter writer, AssessmentDeadline value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, Inner(options));
}

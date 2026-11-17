using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ascent.Web.Infrastructure;

// Converts calendar notation only. It never selects a deadline or adds an extension.
public static class CalendarNotation
{
    static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-GB");
    static readonly string[] Dates = ["yyyy-MM-dd", "d MMMM yyyy", "d MMM yyyy",
        "MMMM d yyyy", "MMM d yyyy", "MMMM d, yyyy", "MMM d, yyyy", "d/M/yyyy", "yyyy/M/d", "d-M-yyyy", "d.M.yyyy",
        "dddd d MMMM yyyy", "ddd d MMM yyyy", "dddd d MMM yyyy", "dddd, d MMMM yyyy", "ddd, d MMM yyyy"];
    static readonly string[] Times = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "htt", "hhtt", "h:mmtt", "hh:mmtt"];

    public static bool TryDate(string? text, out DateOnly date)
    {
        text = Regex.Replace(text ?? "", @"\b(\d{1,2})(st|nd|rd|th)\b", "$1", RegexOptions.IgnoreCase);
        text = Regex.Replace(text.Trim(), @"\s+", " ");
        // A time may accompany a printed date, but must itself be valid.
        var parts = Regex.Split(text, @"\s+at\s+", RegexOptions.IgnoreCase);
        if (parts.Length > 2 || parts.Length == 2 && !TryTime(parts[1], out _)) { date = default; return false; }
        return DateOnly.TryParseExact(parts[0], Dates, Culture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    public static bool TryTime(string? text, out TimeOnly time) => TimeOnly.TryParseExact(
        Regex.Replace(text ?? "", @"\s+", "").ToUpperInvariant(), Times, Culture, DateTimeStyles.None, out time);
}

public sealed class PrintedDateConverter : JsonConverter<DateOnly>
{
    public override DateOnly Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && CalendarNotation.TryDate(reader.GetString(), out var date)) return date;
        throw new JsonException("Expected a complete calendar date, for example 12th January 2027 or 2027-01-12. Numeric dates use day/month/year.");
    }
    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

public sealed class PrintedTimeConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && CalendarNotation.TryTime(reader.GetString(), out var time))
            return time.ToString("HH:mm", CultureInfo.InvariantCulture);
        throw new JsonException("Expected a clock time, for example 2pm, 2 pm or 14:00.");
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

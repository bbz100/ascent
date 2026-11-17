using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Ascent.Web.Planning;

public static class SourceReferences
{
    public static List<Paragraph> Chunks(Evidence? evidence, string document)
    {
        var paragraphs = DocumentReader.Paragraphs(document);
        if (evidence is null || evidence.ParagraphIds is null)
            throw new PlanningException("A source must cite paragraphIds from the uploaded brief.");
        var ids = evidence.ParagraphIds;
        if (ids.Count == 0)
        {
            // Read earlier snapshots by saved locations, never by searching for generated quotations.
            var span = Regex.Match(evidence.ParagraphId ?? "", @"^(P\d+)-(P\d+)$");
            if (span.Success)
            {
                var first = paragraphs.FindIndex(p => p.Id == span.Groups[1].Value);
                var last = paragraphs.FindIndex(p => p.Id == span.Groups[2].Value);
                if (first >= 0 && last >= first && last - first < 16)
                    ids = paragraphs.Skip(first).Take(last - first + 1).Select(p => p.Id).ToList();
            }
            else if (!string.IsNullOrEmpty(evidence.ParagraphId)) ids = [evidence.ParagraphId];
        }
        if (ids.Count is < 1 or > 16 || ids.Distinct().Count() != ids.Count || ids.Any(id => !paragraphs.Any(p => p.Id == id)))
            throw new PlanningException("A source contains missing, duplicate or unknown paragraph IDs. Cite 1–16 IDs from the supplied source paragraphs.");
        var chunks = paragraphs.Where(p => ids.Contains(p.Id)).ToList();
        if (evidence.ParagraphIds.Count == 0 && (string.IsNullOrWhiteSpace(evidence.Quote) ||
            !Normalize(string.Join(' ', chunks.Select(p => p.Text))).Contains(Normalize(evidence.Quote), StringComparison.OrdinalIgnoreCase)))
            throw new PlanningException("The saved source quotation does not match its referenced paragraphs.");
        return chunks;
    }

    public static string Text(Evidence evidence, string document) => string.Join(' ', Chunks(evidence, document).Select(p => p.Text));

    public static string Context(Evidence evidence, string document)
    {
        var paragraphs = DocumentReader.Paragraphs(document);
        var cited = Chunks(evidence, document).Select(p => paragraphs.FindIndex(candidate => candidate.Id == p.Id)).ToList();
        // Show nearby original lines to the model for correction; never move a citation automatically.
        return string.Join('\n', paragraphs.Where((p, i) => cited.Any(at => Math.Abs(at - i) <= 2)).Take(24)
            .Select(p => $"[{p.Id}] {p.Text}"));
    }

    public static void Resolve(object value, string document)
    {
        void Visit(object? node)
        {
            if (node is null || node is string || node.GetType().IsValueType) return;
            if (node is Evidence evidence)
            {
                var chunks = Chunks(evidence, document);
                typeof(Evidence).GetProperty(nameof(Evidence.ParagraphId))!.SetValue(evidence, string.Join(", ", chunks.Select(p => p.Id)));
                typeof(Evidence).GetProperty(nameof(Evidence.Quote))!.SetValue(evidence, string.Join(' ', chunks.Select(p => p.Text)));
                typeof(Evidence).GetProperty(nameof(Evidence.ParagraphIds))!.SetValue(evidence, chunks.Select(p => p.Id).ToList());
                return;
            }
            if (node is IEnumerable sequence) { foreach (var child in sequence) Visit(child); return; }
            foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0)) Visit(property.GetValue(node));
        }
        Visit(value);
    }

    static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

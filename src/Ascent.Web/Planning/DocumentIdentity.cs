using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace Ascent.Web.Planning;

public static class DocumentIdentity
{
    // Compare extracted content, not filenames or DOCX ZIP metadata. Formatting-only changes and
    // renamed copies still match; a substantive text change creates a different identity.
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Regex.Replace(text.Normalize(NormalizationForm.FormC),@"\s+"," ").Trim())));
    public static DocumentMatch Match(Expedition e)
    {
        var route=e.Routes.SingleOrDefault(r=>r.Id==e.ApprovedRouteId);
        var status=route is not null?$"{route.Milestones.Count(m=>m.Complete)} of {route.Milestones.Count} checkpoints completed"
            :e.Routes.Count>0?"Route options ready":$"{e.Answers.Count} of {e.Questions.Count} questions answered";
        return new(e.Id,e.Title,e.FileName,e.CreatedUtc,status);
    }
}
public sealed record DocumentMatch(Guid Id,string Title,string FileName,DateTime CreatedUtc,string Status);
public sealed class DuplicateDocumentException(List<DocumentMatch> matches) : Exception("This brief already has a saved project.")
{
    public List<DocumentMatch> Matches {get;}=matches;
}

using Ascent.Web.Infrastructure;
namespace Ascent.Web.Planning;

public sealed record SavedBriefGroup(List<ExpeditionSummary> Projects)
{
    public ExpeditionSummary Latest=>Projects[0];
}
public static class SavedBriefGroups
{
    public static List<SavedBriefGroup> Build(IEnumerable<ExpeditionSummary> projects)=>projects
        .OrderByDescending(p=>p.CreatedUtc)
        .GroupBy(p=>string.IsNullOrEmpty(p.DocumentHash)?"project:"+p.Id:"document:"+p.DocumentHash)
        .Select(g=>new SavedBriefGroup(g.ToList())).ToList();
}

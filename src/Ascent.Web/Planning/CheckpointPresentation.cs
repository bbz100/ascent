namespace Ascent.Web.Planning;

public static class CheckpointPresentation
{
    public static string Title(Milestone milestone,List<WorkItem> items,List<Requirement> requirements)
    {
        var work=milestone.WorkItemIds.Select(id=>items.Single(i=>i.Id==id)).ToList();
        if(work.Count<=1)return milestone.Title;
        if(work.Select(w=>w.RequirementId).Distinct().Count()==1)
        {
            var verbs=work.Select(w=>w.Title.StartsWith("Prepare: ")?"prepare":w.Title.StartsWith("Work on: ")?"draft":w.Title.StartsWith("Review: ")?"review":null).ToList();
            if(verbs.All(v=>v is not null))
            {
                var action=verbs.Count==2?string.Join(" and ",verbs):string.Join(", ",verbs.Take(verbs.Count-1))+" and "+verbs.Last();
                return char.ToUpperInvariant(action[0])+action[1..]+": "+requirements.Single(r=>r.Id==work[0].RequirementId).Title;
            }
        }
        return string.Join(" → ",work.Select(w=>w.Title));
    }
    public static IEnumerable<Evidence> Sources(IEnumerable<WorkItem> items,List<Requirement> requirements)=>items
        .Select(w=>requirements.Single(r=>r.Id==w.RequirementId).Source)
        .DistinctBy(e=>System.Text.RegularExpressions.Regex.Replace(e.Quote,@"\s+"," ").Trim());
}

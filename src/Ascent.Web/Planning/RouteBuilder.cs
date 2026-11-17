namespace Ascent.Web.Planning;

// Build all three routes from ONE validated graph. AI cannot quietly drop work between options.
public static class RouteBuilder
{
    public static List<StudyRoute> Build(List<WorkItem> items, List<Requirement> requirements) =>
    [
        BuildOne("Scenic Route", "Small checkpoints. One focused action at a time.", 1, items, requirements),
        BuildOne("Steady Climb", "Related actions grouped into manageable checkpoints.", 2, items, requirements),
        BuildOne("Direct Ascent", "Larger checkpoints for longer study sessions.", 4, items, requirements)
    ];

    static StudyRoute BuildOne(string name, string description, int groupSize, List<WorkItem> items, List<Requirement> requirements)
    {
        var ordered = new List<WorkItem>();
        void Add(WorkItem item)
        {
            if (ordered.Contains(item)) return;
            foreach (var dep in item.DependsOn) Add(items.Single(i => i.Id == dep));
            ordered.Add(item);
        }
        foreach (var item in items.OrderBy(i=>!requirements.Single(r=>r.Id==i.RequirementId).Mandatory)) Add(item);
        var route = new StudyRoute { Name = name, Description = description };
        var assigned = new Dictionary<string, Milestone>();
        foreach (var item in ordered)
        {
            Milestone? previous = null;
            if (item.DependsOn.Count == 1)
            {
                var predecessor = items.Single(i => i.Id == item.DependsOn[0]);
                previous = assigned[predecessor.Id];
                // Only merge an unbranched chain with the same optionality. Never merge workshops or dates.
                if (previous.WorkItemIds.Count >= groupSize || previous.WorkItemIds.Last() != predecessor.Id ||
                    items.Count(i => i.DependsOn.Contains(predecessor.Id)) != 1 || item.Placeholder || predecessor.Placeholder ||
                    item.DueDate is not null || predecessor.DueDate is not null ||
                    item.KeepSeparate || predecessor.KeepSeparate ||
                    previous.Optional != !requirements.Single(r => r.Id == item.RequirementId).Mandatory) previous = null;
            }
            var milestone = previous ?? new Milestone
            {
                Title = item.Title,
                Optional = !requirements.Single(r => r.Id == item.RequirementId).Mandatory
            };
            if (previous is null) route.Milestones.Add(milestone);
            milestone.WorkItemIds.Add(item.Id);
            assigned[item.Id] = milestone;
        }
        foreach (var milestone in route.Milestones)
        {
            milestone.Title=CheckpointPresentation.Title(milestone,items,requirements);
            milestone.DependsOn = milestone.WorkItemIds.SelectMany(id => items.Single(i => i.Id == id).DependsOn)
                .Select(id => assigned[id].Id).Where(id => id != milestone.Id).Distinct().ToList();
        }
        PlanValidator.Graph(route.Milestones.Select(m => (m.Id, m.DependsOn)));
        return route;
    }
    public static bool Available(Milestone milestone, StudyRoute route) => !milestone.Complete &&
        milestone.DependsOn.All(id => route.Milestones.Single(m => m.Id == id).Complete);
}

namespace Ascent.Web.Planning;

public static class RouteCompletion
{
    public static bool IsComplete(StudyRoute route)
    {
        var required = route.Milestones.Where(m => !m.Optional).ToList();
        return required.Count > 0 && required.All(m => m.Complete);
    }
}

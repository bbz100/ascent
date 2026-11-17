namespace Ascent.Web.Planning;

public sealed record MapCheckpoint(string Id,int X,int Y,int Row);
public sealed record RouteMapGeometry(int Height,List<MapCheckpoint> Checkpoints);

public static class RouteMapLayout
{
    // Geometry depends ONLY on the approved route, never Complete, availability or selection.
    public static RouteMapGeometry Build(StudyRoute route)
    {
        var levels=new Dictionary<string,int>();
        int Level(Milestone m)
        {
            if(levels.TryGetValue(m.Id,out var value)) return value;
            return levels[m.Id]=m.DependsOn.Count==0?0:m.DependsOn.Max(id=>Level(route.Milestones.Single(p=>p.Id==id)))+1;
        }
        var groups=route.Milestones.GroupBy(Level).OrderBy(g=>g.Key).Select(g=>g.OrderBy(m=>m.Optional).ToList()).ToList();
        var rows=route.Milestones.Count;
        var height=Math.Max(1000,600+rows*120);
        List<MapCheckpoint> points=[];
        var row=0;
        foreach(var group in groups)
        {
            for(var i=0;i<group.Count;i++)
            {
                var step=row+i;
                var center=TrailCenter(step);
                // Independent branches leave the winding trail at staggered heights. A single
                // continuation follows its bends. These are not paired rows or fixed columns.
                var x=group.Count==1?center:Math.Clamp(center+(step%2==0?-18:18),21,79);
                points.Add(new(group[i].Id,x,height-125-step*120,step));
            }
            row+=group.Count;
        }
        return new(height,points);
    }
    public static int TrailCenter(int step)=>50+(int)Math.Round(12*Math.Sin(step*.85-.6));
    public static HashSet<string> VisibleCheckpoints(StudyRoute route,RouteMapGeometry geometry)
    {
        var completedRequired=geometry.Checkpoints.Where(p=>route.Milestones.Any(m=>m.Id==p.Id && m.Complete && !m.Optional))
            .Select(p=>p.Row).DefaultIfEmpty(-1).Max();
        return geometry.Checkpoints.Where(p=>p.Row<=completedRequired+3 || route.Milestones.Any(m=>m.Id==p.Id && (m.Complete || RouteBuilder.Available(m,route))))
            .Select(p=>p.Id).ToHashSet();
    }
}

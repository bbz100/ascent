using System.Text.Json;
using Ascent.Web.Planning;
using Microsoft.EntityFrameworkCore;
namespace Ascent.Web.Infrastructure;

public interface IExpeditionStore
{
    Task<List<ExpeditionSummary>> ListAsync(CancellationToken ct = default);
    Task<Expedition> GetAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(Expedition expedition, CancellationToken ct = default);
    Task ReplaceDocumentAsync(Expedition expedition, CancellationToken ct = default);
    Task DeleteAsync(Guid id,CancellationToken ct=default);
    async Task<List<DocumentMatch>> FindByDocumentAsync(string document,CancellationToken ct=default)
    {
        var hash=DocumentIdentity.Hash(document);
        List<DocumentMatch> matches=[];
        foreach(var summary in await ListAsync(ct))
        {
            var e=await GetAsync(summary.Id,ct);
            if(DocumentIdentity.Hash(e.DocumentText)==hash)matches.Add(DocumentIdentity.Match(e));
        }
        return matches.OrderByDescending(m=>m.CreatedUtc).ToList();
    }
}
public sealed record ExpeditionSummary(Guid Id,string Title,DateTime CreatedUtc,string DocumentHash="",string Status="Not started",int RequiredCompleted=0,int RequiredTotal=0,int OptionalCompleted=0,int OptionalTotal=0,bool HasApprovedRoute=false)
{
    public int ProgressPercent=>RequiredTotal==0?0:RequiredCompleted*100/RequiredTotal;
    public static ExpeditionSummary From(Expedition e,string hash="")
    {
        var route=e.Routes.SingleOrDefault(r=>r.Id==e.ApprovedRouteId);
        var required=route?.Milestones.Where(m=>!m.Optional).ToList()??[];
        var optional=route?.Milestones.Where(m=>m.Optional).ToList()??[];
        var completed=required.Count(m=>m.Complete);
        var status=route is null?(e.Routes.Count>0?"Choose your route":$"Setting up · {e.Answers.Count}/{e.Questions.Count} questions answered")
            :required.Count>0 && completed==required.Count?"Completed ✓":completed==0?"Not started":"In progress";
        return new(e.Id,e.Title,e.CreatedUtc,hash,status,completed,required.Count,optional.Count(m=>m.Complete),optional.Count,route is not null);
    }
}
public sealed class SqlExpeditionStore(IDbContextFactory<PlanningDb> factory, IWorkspaceSession workspace, DemoRun run) : IExpeditionStore
{
    public async Task<List<DocumentMatch>> FindByDocumentAsync(string document,CancellationToken ct=default)
    {
        var session=await workspace.GetIdAsync();
        await using var db=await OpenAsync(ct);
        var hash=DocumentIdentity.Hash(document);
        var snapshots=await (from identity in db.DocumentFingerprints.AsNoTracking()
            join module in db.Modules.AsNoTracking() on identity.ModuleId equals module.Id
            where module.SessionId==session && identity.TextHash==hash orderby module.CreatedUtc descending select module.SnapshotJson).ToListAsync(ct);
        return snapshots.Select(json=>DocumentIdentity.Match(JsonSerializer.Deserialize<Expedition>(json,CompatiblePlanningAi.Json)!)).ToList();
    }
    public async Task<List<ExpeditionSummary>> ListAsync(CancellationToken ct = default)
    {
        var session=await workspace.GetIdAsync();
        await using var db = await OpenAsync(ct);
        var rows=await (from module in db.Modules.AsNoTracking()
            join identity in db.DocumentFingerprints.AsNoTracking() on module.Id equals identity.ModuleId into identities
            from identity in identities.DefaultIfEmpty()
            where module.SessionId==session orderby module.CreatedUtc descending
            select new {module.SnapshotJson,Hash=identity==null?"":identity.TextHash}).ToListAsync(ct);
        return rows.Select(row=>ExpeditionSummary.From(JsonSerializer.Deserialize<Expedition>(row.SnapshotJson,CompatiblePlanningAi.Json)!,row.Hash)).ToList();
    }
    public async Task DeleteAsync(Guid id,CancellationToken ct=default)
    {
        var session=await workspace.GetIdAsync();
        await using var db=await OpenAsync(ct);
        if(!await db.Modules.AnyAsync(m=>m.Id==id && m.SessionId==session,ct))throw new PlanningException("This expedition wasn't found.");
        var routes=db.Routes.Where(r=>r.ModuleId==id).Select(r=>r.Id);
        var milestones=db.Milestones.Where(m=>routes.Contains(m.RouteId)).Select(m=>m.Id);
        await db.Dependencies.Where(d=>milestones.Contains(d.MilestoneId)||milestones.Contains(d.PrerequisiteId)).ExecuteDeleteAsync(ct);
        await db.Modules.Where(m=>m.Id==id && m.SessionId==session).ExecuteDeleteAsync(ct);
        await db.Database.CurrentTransaction!.CommitAsync(ct);
    }
    public async Task<Expedition> GetAsync(Guid id, CancellationToken ct = default)
    {
        var session=await workspace.GetIdAsync();
        await using var db = await OpenAsync(ct);
        var row = await db.Modules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.SessionId == session, ct) ?? throw new PlanningException("This expedition wasn't found.");
        var expedition=JsonSerializer.Deserialize<Expedition>(row.SnapshotJson, CompatiblePlanningAi.Json)!;
        if(expedition.Analysis is not null)
            foreach(var milestone in expedition.Routes.SelectMany(r=>r.Milestones))
                milestone.Title=CheckpointPresentation.Title(milestone,expedition.WorkItems,expedition.Analysis.Requirements);
        return expedition;
    }
    public Task SaveAsync(Expedition expedition, CancellationToken ct = default) => PersistAsync(expedition, false, ct);
    public Task ReplaceDocumentAsync(Expedition expedition, CancellationToken ct = default) => PersistAsync(expedition, true, ct);
    private async Task PersistAsync(Expedition expedition, bool replaceDocument, CancellationToken ct)
    {
        var session=await workspace.GetIdAsync();
        await using var db = await OpenAsync(ct);
        var tx = db.Database.CurrentTransaction!;
        if (replaceDocument)
        {
            var hash = DocumentIdentity.Hash(expedition.DocumentText);
            var oldModules = from f in db.DocumentFingerprints join m in db.Modules on f.ModuleId equals m.Id where f.TextHash==hash && m.SessionId==session select m.Id;
            var oldRoutes = db.Routes.Where(r => oldModules.Contains(r.ModuleId)).Select(r => r.Id);
            var oldMilestones = db.Milestones.Where(m => oldRoutes.Contains(m.RouteId)).Select(m => m.Id);
            await db.Dependencies.Where(d => oldMilestones.Contains(d.MilestoneId) || oldMilestones.Contains(d.PrerequisiteId)).ExecuteDeleteAsync(ct);
            await db.Modules.Where(m => oldModules.Contains(m.Id)).ExecuteDeleteAsync(ct);
        }
        var row = await db.Modules.SingleOrDefaultAsync(x => x.Id == expedition.Id, ct);
        if(row is not null && row.SessionId!=session) throw new PlanningException("This expedition wasn't found.");
        var oldRevision = expedition.Revision;
        if (row is null)
        {
            if (oldRevision != 0) throw new PlanningException("This expedition no longer exists.");
            await db.Database.ExecuteSqlInterpolatedAsync($"IF NOT EXISTS (SELECT 1 FROM [Workspaces] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={session}) INSERT INTO [Workspaces] ([Id],[CreatedUtc]) VALUES ({session},{DateTime.UtcNow});",ct);
            row = new ModuleRow { Id = expedition.Id, CreatedUtc = expedition.CreatedUtc, SessionId=session }; db.Modules.Add(row);
        }
        else if (row.Revision != oldRevision) throw new PlanningException("This expedition changed in another window. Reopen it before saving.");
        row.Revision = oldRevision + 1; row.Title = expedition.Title;
        var snapshot = JsonSerializer.SerializeToNode(expedition, CompatiblePlanningAi.Json)!;
        snapshot["revision"] = row.Revision; row.SnapshotJson = snapshot.ToJsonString();
        try
        {
            await db.SaveChangesAsync(ct);
            var fingerprint=await db.DocumentFingerprints.SingleOrDefaultAsync(f=>f.ModuleId==expedition.Id,ct);
            if(fingerprint is null)db.DocumentFingerprints.Add(new(){ModuleId=expedition.Id,TextHash=DocumentIdentity.Hash(expedition.DocumentText)});
            else fingerprint.TextHash=DocumentIdentity.Hash(expedition.DocumentText);
            var routes = db.Routes.Where(r => r.ModuleId == expedition.Id).Select(r => r.Id);
            var milestones = db.Milestones.Where(m => routes.Contains(m.RouteId)).Select(m => m.Id);
            await db.Dependencies.Where(d => milestones.Contains(d.MilestoneId)).ExecuteDeleteAsync(ct);
            await db.Milestones.Where(m => routes.Contains(m.RouteId)).ExecuteDeleteAsync(ct);
            await db.Routes.Where(r => r.ModuleId == expedition.Id).ExecuteDeleteAsync(ct);
            await db.Requirements.Where(r => r.ModuleId == expedition.Id).ExecuteDeleteAsync(ct);
            foreach (var req in expedition.Analysis?.Requirements ?? [])
                db.Requirements.Add(new RequirementRow { ModuleId = expedition.Id, Id = req.Id, Title = req.Title, Kind = req.Kind, Mandatory = req.Mandatory, Count = req.Count, EvidenceJson = JsonSerializer.Serialize(req.Source, CompatiblePlanningAi.Json) });
            foreach (var route in expedition.Routes)
            {
                db.Routes.Add(new RouteRow { Id = route.Id, ModuleId = expedition.Id, Name = route.Name, Approved = route.Id == expedition.ApprovedRouteId });
                foreach (var m in route.Milestones)
                    db.Milestones.Add(new MilestoneRow { Id = m.Id, RouteId = route.Id, Title = m.Title, Complete = m.Complete, Optional = m.Optional, Notes = m.Notes });
            }
            await db.SaveChangesAsync(ct);
            foreach (var m in expedition.Routes.SelectMany(r => r.Milestones))
                foreach (var d in m.DependsOn) db.Dependencies.Add(new DependencyRow { MilestoneId = m.Id, PrerequisiteId = d });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); expedition.Revision = row.Revision;
        }
        catch (DbUpdateConcurrencyException) { throw new PlanningException("Another window saved changes first. Reopen this expedition before saving."); }
    }
    private async Task<PlanningDb> OpenAsync(CancellationToken ct)
    {
        var db=await factory.CreateDbContextAsync(ct);
        try
        {
            await db.Database.BeginTransactionAsync(ct);
            await DemoWorkspace.LockAsync(db,false,ct);
            if(!await db.DemoRuntime.AnyAsync(r=>r.Id==1 && r.RunId==run.Id,ct))
                throw new PlanningException("The demo restarted. Refresh to start a new workspace.");
            return db;
        }
        catch {await db.DisposeAsync();throw;}
    }}


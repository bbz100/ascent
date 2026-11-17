using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
namespace Ascent.Web.Infrastructure;

public interface IWorkspaceSession { Task<string> GetIdAsync(); }
public sealed class DemoRun { public string Id {get;}=Guid.NewGuid().ToString("N"); }
public sealed class BlazorWorkspaceSession(AuthenticationStateProvider authentication,DemoRun run):IWorkspaceSession
{
    public async Task<string> GetIdAsync()
    {
        var user=(await authentication.GetAuthenticationStateAsync()).User;
        var id=user.FindFirstValue("ascent:session");
        if(string.IsNullOrWhiteSpace(id) || user.FindFirstValue("ascent:run")!=run.Id)
            throw new Planning.PlanningException("This demo session has ended. Refresh to start a new workspace.");
        return id;
    }
}
public static class DemoWorkspace
{
    // Shared locks protect requests from an overlapping IIS worker's startup reset.
    public static async Task LockAsync(PlanningDb db,bool exclusive,CancellationToken ct=default)
    {
        var mode=exclusive?"Exclusive":"Shared";
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=N'Ascent.DemoStartup', @LockMode={mode}, @LockOwner=N'Transaction', @LockTimeout=30000; IF @result<0 THROW 51000, 'Could not lock demo workspace.', 1;",ct);
    }
    public static async Task<int> ResetAsync(PlanningDb db,DemoRun run,CancellationToken ct=default)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db,true,ct);
        await db.Dependencies.ExecuteDeleteAsync(ct);
        await db.Milestones.ExecuteDeleteAsync(ct);
        await db.Routes.ExecuteDeleteAsync(ct);
        await db.Requirements.ExecuteDeleteAsync(ct);
        await db.DocumentFingerprints.ExecuteDeleteAsync(ct);
        var count=await db.Modules.ExecuteDeleteAsync(ct);
        await db.Workspaces.ExecuteDeleteAsync(ct);
        await db.DemoRuntime.ExecuteDeleteAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [DemoRuntime] ([Id],[RunId]) VALUES (1,{run.Id});",ct);
        await tx.CommitAsync(ct);
        return count;
    }
}

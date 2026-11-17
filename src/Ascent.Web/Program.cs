using Ascent.Web.Components;
using Ascent.Web.Infrastructure;
using Ascent.Web.Planning;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options=>{
    options.Cookie.Name=".Ascent.Workspace";
    options.Cookie.HttpOnly=true;
    options.Cookie.IsEssential=true;
    options.Cookie.SameSite=SameSiteMode.Lax;
    options.Cookie.SecurePolicy=builder.Environment.IsDevelopment()?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;
    options.IdleTimeout=TimeSpan.FromDays(1);
});
builder.Services.AddSingleton<DemoRun>();
builder.Services.AddScoped<IWorkspaceSession,BlazorWorkspaceSession>();
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));
builder.Services.AddHttpClient<IPlanningAi, CompatiblePlanningAi>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(services => new SocketsHttpHandler {
        UseProxy = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value.Provider != "KoboldCpp",
        PooledConnectionLifetime = TimeSpan.Zero
    });
builder.Services.AddDbContextFactory<PlanningDb>(options => options.UseSqlServer(builder.Configuration.GetConnectionString("Planning")));
builder.Services.AddScoped<IExpeditionStore, SqlExpeditionStore>();
builder.Services.AddScoped<StudyPlanningService>();
builder.Services.AddSingleton<DocumentReader>();
builder.Services.Configure<UdocOptions>(builder.Configuration.GetSection("DocumentExtraction"));
builder.Services.Configure<UploadArchiveOptions>(options=>{
    if(!builder.Environment.IsDevelopment())options.Path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Ascent","Uploads");
    builder.Configuration.GetSection("UploadArchive").Bind(options);
});
builder.Services.AddSingleton<DocumentArchive>();

var app = builder.Build();
if (args.Contains("--reset-demo-data"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PlanningDb>();
    if (db.Database.GetDbConnection().Database != "AscentDemo")
        throw new InvalidOperationException("Demo reset is restricted to the AscentDemo database.");
    var removed=await DemoWorkspace.ResetAsync(db,app.Services.GetRequiredService<DemoRun>());
    Console.WriteLine($"Cleared {removed} saved demo projects and their workspaces. Database schema retained.");
    return;
}
if (args.Contains("--init-db") || args.Contains("--upgrade-db") || args.Contains("--schema"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PlanningDb>();
    if (args.Contains("--schema")) Console.WriteLine(db.Database.GenerateCreateScript());
    else
    {
        if(args.Contains("--init-db"))await db.Database.EnsureCreatedAsync();
        await DocumentIdentityUpgrade.RunAsync(db);
        if(args.Contains("--upgrade-db"))
        {
            using var resource=typeof(PlanningDb).Assembly.GetManifestResourceStream("Ascent.WorkspaceUpgrade.sql")!;
            using var reader=new StreamReader(resource);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync());
        }
        Console.WriteLine("Ascent SQL Server database and document identities are ready.");
    }
    return;
}

// Complete the reset before accepting requests. Never expose previous demo work.
await using(var scope=app.Services.CreateAsyncScope())
{
    var db=scope.ServiceProvider.GetRequiredService<PlanningDb>();
    if(db.Database.GetDbConnection().Database is not ("Ascent" or "AscentDemo"))
        throw new InvalidOperationException("Startup cleanup is restricted to the Ascent demo databases.");
    var removed=await DemoWorkspace.ResetAsync(db,app.Services.GetRequiredService<DemoRun>());
    app.Logger.LogInformation("Demo startup cleared {Count} previous projects and workspaces.",removed);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();

app.UseSession();
app.Use(async(context,next)=>{
    await context.Session.LoadAsync(context.RequestAborted);
    context.Session.SetString("workspace","active");
    await context.Session.CommitAsync(context.RequestAborted);
    context.User=new ClaimsPrincipal(new ClaimsIdentity(new[]{
        new Claim("ascent:session",context.Session.Id),
        new Claim("ascent:run",app.Services.GetRequiredService<DemoRun>().Id)
    },"DemoWorkspace"));
    context.Response.Headers.CacheControl="no-store";
    await next(context);
});
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/documents/{id:guid}/original",async(Guid id,HttpContext context,IDbContextFactory<PlanningDb> factory,DemoRun run,DocumentArchive archive)=>{
    var store=new SqlExpeditionStore(factory,new DownloadWorkspace(context.Session.Id),run);
    try
    {
        var project=await store.GetAsync(id,context.RequestAborted);
        var path=project.OriginalDocumentFile is null?null:archive.FindPath(project.OriginalDocumentFile);
        if(path is null)return Results.NotFound();
        context.Response.Headers.XContentTypeOptions="nosniff";
        return Results.File(path,"application/octet-stream",Path.GetFileName(project.FileName),enableRangeProcessing:false);
    }
    catch(PlanningException){return Results.NotFound();}
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

internal sealed class DownloadWorkspace(string id):IWorkspaceSession
{public Task<string> GetIdAsync()=>Task.FromResult(id);}


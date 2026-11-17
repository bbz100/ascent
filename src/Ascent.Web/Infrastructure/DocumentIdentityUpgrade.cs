using System.Text.Json;
using Ascent.Web.Planning;
using Microsoft.EntityFrameworkCore;
namespace Ascent.Web.Infrastructure;

public static class DocumentIdentityUpgrade
{
    // Additive and repeatable. Existing routes, snapshots, answers and progress are untouched.
    public static async Task RunAsync(PlanningDb db,CancellationToken ct=default)
    {
        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'[DocumentFingerprints]',N'U') IS NULL
            BEGIN
                CREATE TABLE [DocumentFingerprints] (
                    [ModuleId] uniqueidentifier NOT NULL PRIMARY KEY,
                    [TextHash] char(64) NOT NULL,
                    CONSTRAINT [FK_DocumentFingerprints_Modules] FOREIGN KEY ([ModuleId]) REFERENCES [Modules]([Id]) ON DELETE CASCADE
                );
                CREATE INDEX [IX_DocumentFingerprints_TextHash] ON [DocumentFingerprints]([TextHash]);
            END;
            """,ct);
        var missing=await db.Modules.AsNoTracking().Where(m=>!db.DocumentFingerprints.Any(f=>f.ModuleId==m.Id))
            .Select(m=>new {m.Id,m.SnapshotJson}).ToListAsync(ct);
        foreach(var row in missing)
        {
            var e=JsonSerializer.Deserialize<Expedition>(row.SnapshotJson,CompatiblePlanningAi.Json)!;
            db.DocumentFingerprints.Add(new(){ModuleId=row.Id,TextHash=DocumentIdentity.Hash(e.DocumentText)});
        }
        await db.SaveChangesAsync(ct);
    }
}

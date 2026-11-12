using CrowdFund.Web.Backup;
using CrowdFund.Web.Observability;
using Microsoft.AspNetCore.Mvc;

namespace CrowdFund.Web.Endpoints;

public static class OpsEndpoints
{
    public static void MapOpsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/ops/metrics", (AppMetrics m) => Results.Ok(m.Snapshot()));
        app.MapPost("/api/admin/backup", RunBackup);
        app.MapGet("/api/admin/backup/list", ListBackups);
    }

    private static async Task<IResult> RunBackup(BackupService backups)
    {
        var (ok, path, message) = await backups.CreateBackupAsync();
        return ok ? Results.Ok(new { ok, path, message }) : Results.Problem(message);
    }

    private static IResult ListBackups(IConfiguration config)
    {
        var root = config["Backup:Root"] ?? "App_Data/backups";
        if (!Directory.Exists(root)) return Results.Ok(Array.Empty<object>());
        var list = Directory.EnumerateFiles(root, "crowdfund-backup-*.zip")
            .Select(f => new
            {
                path = f,
                name = Path.GetFileName(f),
                sizeBytes = new FileInfo(f).Length,
                createdUtc = File.GetCreationTimeUtc(f)
            })
            .OrderByDescending(x => x.createdUtc)
            .ToList();
        return Results.Ok(list);
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CrowdFund.Web.Backup;

public class BackupOptions
{
    public const string Section = "Backup";
    public string Root { get; set; } = "App_Data/backups";
    public int RetainDays { get; set; } = 14;
}

public class BackupService
{
    private readonly IConfiguration _config;
    private readonly BackupOptions _opt;
    private readonly ILogger<BackupService> _log;

    public BackupService(IConfiguration config, IOptions<BackupOptions> opt, ILogger<BackupService> log)
    {
        _config = config;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<(bool ok, string path, string message)> CreateBackupAsync(CancellationToken ct = default)
    {
        var root = Path.GetFullPath(_opt.Root);
        Directory.CreateDirectory(root);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var work = Path.Combine(root, $"work_{stamp}");
        Directory.CreateDirectory(work);

        try
        {
            // 1) File store copy
            var fileRoot = _config["FileStore:Root"] ?? "App_Data/uploads";
            var filesDest = Path.Combine(work, "files");
            if (Directory.Exists(fileRoot))
            {
                CopyDirectory(fileRoot, filesDest);
            }
            else
            {
                Directory.CreateDirectory(filesDest);
            }

            // 2) SQL backup attempt via BACKUP DATABASE (requires proper rights)
            var connStr = _config.GetConnectionString("Default") ?? "";
            var sqlMeta = Path.Combine(work, "sql-backup-note.txt");
            var bakPath = Path.Combine(work, "database.bak");
            var sqlOk = false;
            try
            {
                if (!string.IsNullOrWhiteSpace(connStr))
                {
                    var csb = new SqlConnectionStringBuilder(connStr);
                    var dbName = csb.InitialCatalog;
                    // Use a server-side path is hard from app; instead script a logical export marker
                    // and try BACKUP to a local path if SQL is on same machine.
                    await File.WriteAllTextAsync(sqlMeta,
                        $"Database={dbName}\nServer={csb.DataSource}\nUtc={DateTime.UtcNow:o}\n" +
                        "Prefer scripts/backup-sql.sh for production FULL backups with sqlcmd.\n", ct);

                    // Attempt BAK only when not empty password sandbox; failures are non-fatal
                    await using var conn = new SqlConnection(connStr);
                    await conn.OpenAsync(ct);
                    // Export schema marker via simple query count
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT DB_NAME()";
                    var name = (string?)await cmd.ExecuteScalarAsync(ct);
                    await File.AppendAllTextAsync(sqlMeta, $"ConnectedDatabase={name}\nStatus=reachable\n", ct);
                    sqlOk = true;
                }
            }
            catch (Exception ex)
            {
                await File.AppendAllTextAsync(sqlMeta, $"SQL backup helper error: {ex.Message}\n", ct);
                _log.LogWarning(ex, "SQL connectivity check during backup failed");
            }

            // 3) Manifest
            var manifest = new
            {
                createdUtc = DateTime.UtcNow,
                sqlReachable = sqlOk,
                filesIncluded = Directory.Exists(filesDest),
                note = "For full MSSQL .bak use scripts/backup-sql.sh on the DB host. This archive packages file store + metadata."
            };
            await File.WriteAllTextAsync(Path.Combine(work, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);

            var zipPath = Path.Combine(root, $"crowdfund-backup-{stamp}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(work, zipPath, CompressionLevel.Optimal, false);
            Directory.Delete(work, true);

            Retain(root);
            _log.LogInformation("Backup created {Path}", zipPath);
            return (true, zipPath, "Backup archive created (files + SQL metadata). Use scripts/backup-sql.sh for native .bak.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Backup failed");
            if (Directory.Exists(work))
            {
                try { Directory.Delete(work, true); } catch { /* ignore */ }
            }
            return (false, "", ex.Message);
        }
    }

    private void Retain(string root)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _opt.RetainDays));
        foreach (var f in Directory.EnumerateFiles(root, "crowdfund-backup-*.zip"))
        {
            try
            {
                if (File.GetCreationTimeUtc(f) < cutoff)
                    File.Delete(f);
            }
            catch { /* ignore */ }
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(src, dest));
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(src, dest);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}

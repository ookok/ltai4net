using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Maintenance;

/// <summary>
/// Background service that periodically performs health maintenance on the .livingtree directory.
/// Runs every 6 hours to:
/// - Check directory size
/// - Clean up audit logs older than 30 days
/// - Vacuum SQLite databases that exceed 100MB
/// </summary>
public sealed class LivingtreeHealthCheck : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan AuditRetention = TimeSpan.FromDays(30);
    private const long MaxDbSizeBytes = 100L * 1024 * 1024; // 100 MB

    private readonly ILogger<LivingtreeHealthCheck> _logger;
    private readonly string _livingtreeDir;
    private readonly string _auditDir;
    private static readonly string[] DbFiles = ["kg.db", "circuit_breaker.db"];

    public LivingtreeHealthCheck(ILogger<LivingtreeHealthCheck>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LivingtreeHealthCheck>.Instance;
        _livingtreeDir = Path.Combine(AppContext.BaseDirectory, ".livingtree");
        _auditDir = Path.Combine(_livingtreeDir, "audit");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LivingtreeHealthCheck started, interval={Interval}", Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
                await RunHealthCheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LivingtreeHealthCheck failed");
            }
        }
    }

    public async Task<HealthCheckResult> RunHealthCheckAsync(CancellationToken ct = default)
    {
        var result = new HealthCheckResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Check .livingtree directory size
        if (Directory.Exists(_livingtreeDir))
        {
            result.TotalSizeBytes = GetDirectorySize(_livingtreeDir);
            _logger.LogInformation("Livingtree directory size: {SizeMB:F2} MB",
                result.TotalSizeBytes / (1024.0 * 1024.0));
        }
        else
        {
            _logger.LogWarning("Livingtree directory not found: {Path}", _livingtreeDir);
            return result;
        }

        // 2. Remove old audit logs
        if (Directory.Exists(_auditDir))
        {
            var cutoff = DateTime.UtcNow - AuditRetention;
            foreach (var file in Directory.GetFiles(_auditDir, "*.*", SearchOption.AllDirectories))
            {
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (lastWrite < cutoff)
                    {
                        File.Delete(file);
                        result.DeletedAuditFiles++;
                        result.FreedBytes += new FileInfo(file).Length;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete audit file: {File}", file);
                }
            }

            // Remove empty subdirectories
            foreach (var dir in Directory.GetDirectories(_auditDir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.GetFileSystemEntries(dir).Length == 0)
                        Directory.Delete(dir);
                }
                catch { }
            }
        }

        // 3. Vacuum large SQLite databases
        foreach (var dbFile in DbFiles)
        {
            var dbPath = Path.Combine(_livingtreeDir, dbFile);
            if (!File.Exists(dbPath)) continue;

            try
            {
                var fileInfo = new FileInfo(dbPath);
                if (fileInfo.Length > MaxDbSizeBytes)
                {
                    var beforeBytes = fileInfo.Length;
                    VacuumDatabase(dbPath);
                    fileInfo.Refresh();
                    var afterBytes = fileInfo.Length;
                    result.VacuumedDatabases++;
                    result.FreedBytes += (beforeBytes - afterBytes);
                    _logger.LogInformation("Vacuumed {Db}: {BeforeMB:F2} MB → {AfterMB:F2} MB",
                        dbFile, beforeBytes / (1024.0 * 1024.0), afterBytes / (1024.0 * 1024.0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to vacuum database: {Db}", dbFile);
            }
        }

        sw.Stop();
        result.DurationMs = sw.ElapsedMilliseconds;

        _logger.LogInformation(
            "Health check complete: deleted {AuditCount} audit files, " +
            "vacuumed {DbCount} databases, freed {FreedMB:F2} MB in {DurationMs} ms",
            result.DeletedAuditFiles, result.VacuumedDatabases,
            result.FreedBytes / (1024.0 * 1024.0), result.DurationMs);

        return result;
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    private static void VacuumDatabase(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = WAL";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "VACUUM;";
        cmd.ExecuteNonQuery();
    }

    public sealed class HealthCheckResult
    {
        public long TotalSizeBytes { get; set; }
        public int DeletedAuditFiles { get; set; }
        public int VacuumedDatabases { get; set; }
        public long FreedBytes { get; set; }
        public long DurationMs { get; set; }

        public double TotalSizeMB => TotalSizeBytes / (1024.0 * 1024.0);
        public double FreedMB => FreedBytes / (1024.0 * 1024.0);
    }
}
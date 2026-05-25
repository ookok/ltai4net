using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.System;

public sealed record FileEdit
{
    public string Path { get; init; } = "";
    public string NewContent { get; init; } = "";
    public string? OriginalContent { get; init; }
    public string? OriginalSha256 { get; init; }
    public string? NewSha256 { get; init; }
    public string? BackupPath { get; init; }
    public string Status { get; init; } = "pending";
    public string? Error { get; init; }
}

public sealed record AtomicResult
{
    public List<FileEdit> Edits { get; init; } = new();
    public bool Success { get; init; }
    public int FilesModified { get; init; }
    public int FilesRolledBack { get; init; }
    public long TotalCharsChanged { get; init; }
    public DateTime AppliedAt { get; init; } = DateTime.UtcNow;
    public string Reason { get; init; } = "";
    public List<string> Errors { get; init; } = new();

    public string Summary =>
        $"Atomic: {(Success ? "SUCCESS" : "FAILED")} | " +
        $"Files: {FilesModified} modified, {FilesRolledBack} rolled back | " +
        $"Chars: {TotalCharsChanged} | " +
        $"Reason: {Reason} | " +
        $"At: {AppliedAt:O}" +
        (Errors.Count > 0 ? $" | Errors: {string.Join("; ", Errors)}" : "");
}

public sealed class AtomicModification
{
    public static AtomicModification Instance => _instance.Value;
    private static readonly Lazy<AtomicModification> _instance = new(() => new AtomicModification());

    private readonly ConcurrentQueue<AtomicResult> _history = new();
    private readonly ILogger<AtomicModification> _logger;
    private long _totalEdits;
    private AtomicResult? _lastResult;

    public AtomicModification(ILogger<AtomicModification>? logger = null)
    {
        _logger = logger ?? NullLogger<AtomicModification>.Instance;
    }

    public async Task<AtomicResult> Apply(
        Dictionary<string, string> editsDict,
        string reason,
        bool dryRun = false,
        bool verifyImports = false)
    {
        var edits = new List<FileEdit>();
        var errors = new List<string>();
        long totalCharsChanged = 0;
        var backupDir = GetBackupDir();

        var backupDirInfo = new DirectoryInfo(backupDir);
        if (!backupDirInfo.Exists && !dryRun)
            backupDirInfo.Create();

        foreach (var (path, newContent) in editsDict)
        {
            try
            {
                string? originalContent = null;
                string? originalSha256 = null;

                if (File.Exists(path))
                {
                    originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                    originalSha256 = ComputeSha256(originalContent);
                }

                var newSha256 = ComputeSha256(newContent);
                var edit = new FileEdit
                {
                    Path = path,
                    NewContent = newContent,
                    OriginalContent = originalContent,
                    OriginalSha256 = originalSha256,
                    NewSha256 = newSha256,
                    Status = "prepared"
                };

                if (!dryRun)
                {
                    edit = await BackupAndWrite(path, newContent, backupDir).ConfigureAwait(false);
                    if (edit.Status == "written")
                    {
                        edit = await Verify(edit).ConfigureAwait(false);
                        if (edit.Status == "verified")
                        {
                            edit = edit with { Status = "applied" };
                            totalCharsChanged += Math.Abs(
                                (originalContent?.Length ?? 0) - newContent.Length);
                        }
                        else
                        {
                            errors.Add($"Verification failed: {path} ({edit.Error})");
                        }
                    }
                    else
                    {
                        errors.Add($"Write failed: {path} ({edit.Error})");
                    }
                }
                else
                {
                    edit = edit with { Status = "dry_run" };
                    totalCharsChanged += Math.Abs(
                        (originalContent?.Length ?? 0) - newContent.Length);
                }

                edits.Add(edit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Apply failed for {Path}", path);
                edits.Add(new FileEdit
                {
                    Path = path,
                    NewContent = newContent,
                    Status = "error",
                    Error = ex.Message
                });
                errors.Add(ex.Message);
            }
        }

        if (verifyImports && !dryRun)
        {
            foreach (var edit in edits.Where(e => e.Status == "applied"))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(edit.Path).ConfigureAwait(false);
                    if (content != edit.NewContent)
                    {
                        errors.Add($"Import verification failed: {edit.Path} content mismatch after write");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Import verification error: {edit.Path} - {ex.Message}");
                }
            }
        }

        var result = new AtomicResult
        {
            Edits = edits,
            Success = errors.Count == 0,
            FilesModified = edits.Count(e => e.Status == "applied"),
            FilesRolledBack = 0,
            TotalCharsChanged = totalCharsChanged,
            AppliedAt = DateTime.UtcNow,
            Reason = reason,
            Errors = errors
        };

        _history.Enqueue(result);
        Interlocked.Increment(ref _totalEdits);
        _lastResult = result;

        if (!result.Success && !dryRun)
        {
            await Rollback(result).ConfigureAwait(false);
        }

        _logger.LogInformation("Atomic apply: {Reason} - {Success} ({Modified} files, {Chars} chars)",
            reason, result.Success ? "OK" : "FAIL", result.FilesModified, totalCharsChanged);

        return result;
    }

    private async Task<FileEdit> BackupAndWrite(string path, string content, string backupDir)
    {
        var edit = new FileEdit { Path = path, NewContent = content };

        if (File.Exists(path))
        {
            var relPath = path.Replace(Path.DirectorySeparatorChar, '_')
                              .Replace(Path.AltDirectorySeparatorChar, '_')
                              .TrimStart('_');
            var backupPath = global::System.IO.Path.Combine(backupDir, $"{relPath}.bak");
            var backupDirPath = Path.GetDirectoryName(backupPath);

            if (backupDirPath != null && !Directory.Exists(backupDirPath))
                Directory.CreateDirectory(backupDirPath);

            File.Copy(path, backupPath, true);
            edit = edit with { BackupPath = backupPath, OriginalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false) };
        }

        edit = await WriteNewFile(edit).ConfigureAwait(false);
        return edit;
    }

    private async Task<FileEdit> WriteNewFile(FileEdit edit)
    {
        try
        {
            var dir = Path.GetDirectoryName(edit.Path);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = edit.Path + ".tmp";
            await File.WriteAllTextAsync(tmpPath, edit.NewContent, Encoding.UTF8).ConfigureAwait(false);

            File.Move(tmpPath, edit.Path, true);

            return edit with { Status = "written" };
        }
        catch (Exception ex)
        {
            return edit with { Status = "write_failed", Error = ex.Message };
        }
    }

    private async Task<FileEdit> Verify(FileEdit edit)
    {
        try
        {
            var actual = await File.ReadAllTextAsync(edit.Path).ConfigureAwait(false);
            var actualSha256 = ComputeSha256(actual);

            if (actualSha256 == edit.NewSha256)
            {
                return edit with { Status = "verified" };
            }

            return edit with
            {
                Status = "verify_failed",
                Error = $"SHA-256 mismatch. Expected: {edit.NewSha256}, Got: {actualSha256}"
            };
        }
        catch (Exception ex)
        {
            return edit with { Status = "verify_failed", Error = ex.Message };
        }
    }

    public async Task<bool> Validate(Dictionary<string, string> editsDict)
    {
        foreach (var (path, _) in editsDict)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && !Directory.Exists(dir))
                {
                    var parent = FindWritableParent(dir);
                    if (parent == null)
                    {
                        _logger.LogWarning("Validate failed: no writable parent for {Path}", path);
                        return false;
                    }
                }
                else if (File.Exists(path))
                {
                    using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _ = fs;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Validate failed for {Path}", path);
                return false;
            }
        }
        return true;
    }

    private static string? FindWritableParent(string path)
    {
        var current = path;
        while (current != null && current.Length > 3)
        {
            if (Directory.Exists(current))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    public async Task<int> Rollback(AtomicResult result)
    {
        var rolledBack = 0;
        var appliedEdits = result.Edits
            .Where(e => e.Status == "applied" && e.BackupPath != null)
            .Reverse()
            .ToList();

        foreach (var edit in appliedEdits)
        {
            try
            {
                if (edit.BackupPath != null && File.Exists(edit.BackupPath))
                {
                    File.Copy(edit.BackupPath!, edit.Path, true);
                    rolledBack++;

                    var verifySha = ComputeSha256(await File.ReadAllTextAsync(edit.Path).ConfigureAwait(false));
                    if (edit.OriginalSha256 != null && verifySha != edit.OriginalSha256)
                    {
                        _logger.LogWarning("Rollback SHA mismatch for {Path}", edit.Path);
                    }
                }
                else if (edit.OriginalContent != null)
                {
                    await File.WriteAllTextAsync(edit.Path, edit.OriginalContent).ConfigureAwait(false);
                    rolledBack++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rollback failed for {Path}", edit.Path);
            }
        }

        var newFiles = result.Edits
            .Where(e => e.Status == "applied" && e.BackupPath == null)
            .Reverse();

        foreach (var edit in newFiles)
        {
            try
            {
                if (File.Exists(edit.Path))
                {
                    File.Delete(edit.Path);
                    rolledBack++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rollback delete failed for {Path}", edit.Path);
            }
        }

        _logger.LogInformation("Rollback complete: {Count} files rolled back", rolledBack);
        return rolledBack;
    }

    public void Commit(AtomicResult result)
    {
        var backupDir = GetBackupDir();
        try
        {
            if (Directory.Exists(backupDir))
            {
                Directory.Delete(backupDir, true);
                _logger.LogInformation("Commit: backup directory cleaned ({Dir})", backupDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Commit: failed to clean backup directory");
        }
    }

    public async Task<AtomicResult> AtomicEditSingle(string path, string content, string reason)
    {
        return await Apply(
            new Dictionary<string, string> { { path, content } },
            reason).ConfigureAwait(false);
    }

    public static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public string GetBackupDir()
    {
        var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        return global::System.IO.Path.Combine(".livingtree", "atomic", $"backups_{ts}");
    }

    public Dictionary<string, object> Stats()
    {
        return new Dictionary<string, object>
        {
            ["totalEdits"] = Interlocked.Read(ref _totalEdits),
            ["historyCount"] = _history.Count,
            ["lastResult"] = _lastResult?.Summary ?? "none",
            ["lastSuccess"] = _lastResult?.Success ?? false,
            ["lastReason"] = _lastResult?.Reason ?? ""
        };
    }
}

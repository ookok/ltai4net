using LTAI.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed class CodeRepairAci
{
    private readonly ILogger<CodeRepairAci> _logger;
    private static readonly int _maxOutputBytes = LTAI.Core.Configuration.EnvironmentConfig.ToolMaxOutputBytes;

    public CodeRepairAci(ILogger<CodeRepairAci>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CodeRepairAci>.Instance;
    }

    public async Task<string> ViewFileAsync(
        [System.ComponentModel.Description("File path to view")]
        string filePath,
        [System.ComponentModel.Description("Start line number (1-based, optional)")]
        int? startLine = null,
        [System.ComponentModel.Description("End line number (optional)")]
        int? endLine = null)
    {
        try
        {
            if (!System.IO.File.Exists(filePath))
                return $"Error: file not found '{filePath}'";

            var lines = await System.IO.File.ReadAllLinesAsync(filePath);
            var totalLines = lines.Length;

            var start = Math.Max(0, (startLine ?? 1) - 1);
            var end = endLine.HasValue ? Math.Min(endLine.Value, totalLines) : totalLines;

            if (start >= totalLines) return $"Error: start line {startLine} exceeds file length {totalLines}";

            var selected = lines[start..end];
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"File: {filePath} ({selected.Length}/{totalLines} lines)");
            sb.AppendLine("```");
            for (int i = 0; i < selected.Length; i++)
                sb.AppendLine($"{start + i + 1,6}: {selected[i]}");
            sb.AppendLine("```");
            var output = sb.ToString();
            return output.Length > _maxOutputBytes ? output[.._maxOutputBytes] + "\n... (truncated)" : output;
        }
        catch (Exception ex)
        {
            return $"Error viewing file '{filePath}': {ex.Message}";
        }
    }

    public async Task<string> EditLinesAsync(
        [System.ComponentModel.Description("File path to edit")]
        string filePath,
        [System.ComponentModel.Description("Start line number (1-based)")]
        int startLine,
        [System.ComponentModel.Description("End line number (inclusive)")]
        int endLine,
        [System.ComponentModel.Description("Replacement text (use |BR| for newlines)")]
        string replacement)
    {
        try
        {
            if (!System.IO.File.Exists(filePath))
                return $"Error: file not found '{filePath}'";

            var lines = (await System.IO.File.ReadAllLinesAsync(filePath)).ToList();
            var start = Math.Max(0, startLine - 1);
            var end = Math.Min(endLine, lines.Count);

            if (start >= lines.Count) return $"Error: start line {startLine} exceeds file length {lines.Count}";
            if (end < start) return $"Error: end line {endLine} < start line {startLine}";

            var newLines = replacement
                .Replace("|BR|", "\n")
                .Split('\n', System.StringSplitOptions.None)
                .ToList();

            lines.RemoveRange(start, end - start);
            for (int i = newLines.Count - 1; i >= 0; i--)
                lines.Insert(start, newLines[i]);

            await System.IO.File.WriteAllLinesAsync(filePath, lines);
            return $"Edited {filePath}: replaced lines {startLine}-{endLine} with {newLines.Count} new lines.";
        }
        catch (Exception ex)
        {
            return $"Error editing file '{filePath}': {ex.Message}";
        }
    }

    public async Task<string> RunTestsAsync(
        [System.ComponentModel.Description("Optional test filter (e.g. 'ClassName' or 'Category=Unit')")]
        string? filter = null)
    {
        try
        {
            var args = string.IsNullOrWhiteSpace(filter) ? "test" : $"test --filter \"{filter}\"";
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Tests exit code: {process.ExitCode}");
            if (!string.IsNullOrWhiteSpace(output))
                sb.AppendLine(output.Length > _maxOutputBytes ? output[.._maxOutputBytes] : output);
            if (!string.IsNullOrWhiteSpace(error))
                sb.AppendLine("STDERR: " + error);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error running tests: {ex.Message}";
        }
    }

    public async Task<string> SearchSymbolAsync(
        [System.ComponentModel.Description("Symbol or query to search for")]
        string query,
        [System.ComponentModel.Description("Optional root path to search (defaults to current directory)")]
        string? rootPath = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
                return "Error: search query is empty";

            var root = string.IsNullOrWhiteSpace(rootPath) ? Environment.CurrentDirectory : rootPath!;
            if (!System.IO.Directory.Exists(root))
                return $"Error: path not found '{root}'";

            var extensions = new[] { ".cs", ".fs", ".ts", ".tsx", ".js", ".py", ".go", ".java", ".cpp", ".c", ".h" };
            var sb = new System.Text.StringBuilder();
            var matches = 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories))
            {
                if (matches >= 50) break;
                var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (System.Array.IndexOf(extensions, ext) < 0) continue;
                if (file.Contains($"{System.IO.Path.DirectorySeparatorChar}.git{System.IO.Path.DirectorySeparatorChar}")) continue;

                string[] lines;
                try { lines = await System.IO.File.ReadAllLinesAsync(file); }
                catch { continue; }

                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains(query, System.StringComparison.Ordinal)) continue;
                    sb.AppendLine($"{file}:{i + 1}: {lines[i].Trim()}");
                    matches++;
                    if (matches >= 50) break;
                }
            }

            if (matches == 0) return $"No matches found for '{query}' in {root}";
            var output = sb.ToString();
            return output.Length > _maxOutputBytes ? output[.._maxOutputBytes] + "\n... (truncated)" : output;
        }
        catch (Exception ex)
        {
            return $"Error searching symbol '{query}': {ex.Message}";
        }
    }

    [System.ComponentModel.Description("Submit the current fix as complete. Records the repair trajectory.")]
    public string Submit(
        [System.ComponentModel.Description("Summary of changes made")]
        string summary)
    {
        _logger.LogInformation("CodeRepairAci: submitted fix - {Summary}", summary);
        return $"Fix submitted: {summary}";
    }
}

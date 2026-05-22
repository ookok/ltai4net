using System.ComponentModel;
using System.Reflection;
using LTAI.Core.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.MAF.Tools;

public static class ToolRegistryExtensions
{
    public static Task RegisterAllToolCategoriesAsync(
        this AIToolRegistry registry,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        int totalTools = 0;

        totalTools += RegisterFileSystemTools(registry);
        totalTools += RegisterShellTools(registry);
        totalTools += RegisterHttpTools(registry);
        totalTools += RegisterMathTools(registry);
        totalTools += RegisterTextTools(registry);
        totalTools += RegisterDataTools(registry);
        totalTools += RegisterDateTimeTools(registry);
        totalTools += RegisterCodeTools(registry);
        totalTools += RegisterEnvironmentTools(registry);
        totalTools += RegisterWebSearchTools(registry);
        totalTools += RegisterGitTools(registry);
        totalTools += RegisterDependencyTools(registry);
        totalTools += RegisterUnderstandTools(registry);

        logger?.LogInformation("ToolRegistry: Registered {Count} tools across 13 categories", totalTools);
        return Task.CompletedTask;
    }

    private static int RegisterFileSystemTools(AIToolRegistry registry)
    {
        registry.RegisterTool("filesystem_read", AIFunctionFactory.Create(
            (string path, CancellationToken ct) => FileSystemTools.ReadFile(path, ct), "filesystem_read",
            "Read the content of a file at the given path."));
        registry.RegisterTool("filesystem_write", AIFunctionFactory.Create(
            (string path, string content, CancellationToken ct) => FileSystemTools.WriteFile(path, content, ct), "filesystem_write",
            "Write text content to a file. Creates parent directories if needed."));
        registry.RegisterTool("filesystem_list", AIFunctionFactory.Create(
            (string path, string? pattern) => FileSystemTools.ListDirectory(path, pattern), "filesystem_list",
            "List files and directories in a given directory."));
        registry.RegisterTool("filesystem_delete", AIFunctionFactory.Create(
            (string path) => FileSystemTools.DeleteFile(path), "filesystem_delete",
            "Delete a file at the given path."));
        registry.RegisterTool("filesystem_exists", AIFunctionFactory.Create(
            (string path) => FileSystemTools.Exists(path), "filesystem_exists",
            "Check if a file or directory exists."));
        registry.RegisterTool("filesystem_search", AIFunctionFactory.Create(
            (string rootPath, string pattern, int maxResults) => FileSystemTools.SearchFiles(rootPath, pattern, maxResults), "filesystem_search",
            "Search for files matching a pattern recursively."));
        return 6;
    }

    private static int RegisterShellTools(AIToolRegistry registry)
    {
        registry.RegisterTool("shell_exec", AIFunctionFactory.Create(
            (string command, string? workingDirectory, CancellationToken ct) => ShellTools.ExecuteCommand(command, workingDirectory, ct), "shell_exec",
            "Execute a shell command and return stdout/stderr/exit code."));
        registry.RegisterTool("shell_env", AIFunctionFactory.Create(
            () => ShellTools.GetEnvironmentInfo(), "shell_env",
            "Get the current working directory and environment info."));
        return 2;
    }

    private static int RegisterHttpTools(AIToolRegistry registry)
    {
        registry.RegisterTool("http_get", AIFunctionFactory.Create(
            (string url, string? headers, CancellationToken ct) => HttpTools.HttpGet(url, headers, ct), "http_get",
            "HTTP GET request to a URL, returns response body."));
        registry.RegisterTool("http_post", AIFunctionFactory.Create(
            (string url, string body, string? headers, CancellationToken ct) => HttpTools.HttpPost(url, body, headers, ct), "http_post",
            "HTTP POST request with JSON body, returns response."));
        registry.RegisterTool("http_download", AIFunctionFactory.Create(
            (string url, CancellationToken ct) => HttpTools.HttpDownload(url, ct), "http_download",
            "Download a file from URL, returns base64 content."));
        registry.RegisterTool("http_check", AIFunctionFactory.Create(
            (string url, CancellationToken ct) => HttpTools.HttpCheckStatus(url, ct), "http_check",
            "Check URL status with HEAD request."));
        return 4;
    }

    private static int RegisterMathTools(AIToolRegistry registry)
    {
        registry.RegisterTool("math_eval", AIFunctionFactory.Create(
            (string expression) => MathTools.EvaluateExpression(expression), "math_eval",
            "Evaluate a mathematical expression."));
        registry.RegisterTool("math_base_convert", AIFunctionFactory.Create(
            (string value, int fromBase, int toBase) => MathTools.ConvertBase(value, fromBase, toBase), "math_base_convert",
            "Convert number between bases (2,8,10,16)."));
        registry.RegisterTool("math_convert_units", AIFunctionFactory.Create(
            (double value, string fromUnit, string toUnit) => MathTools.ConvertUnits(value, fromUnit, toUnit), "math_convert_units",
            "Convert between units (length, weight, temp, etc)."));
        registry.RegisterTool("math_random", AIFunctionFactory.Create(
            (double min, double max) => MathTools.Random(min, max), "math_random",
            "Generate a random number between min and max."));
        registry.RegisterTool("math_statistics", AIFunctionFactory.Create(
            (string numbersJson) => MathTools.CalculateStatistics(numbersJson), "math_statistics",
            "Calculate count, sum, mean, median, min, max, stddev from number array."));
        return 5;
    }

    private static int RegisterTextTools(AIToolRegistry registry)
    {
        registry.RegisterTool("text_count", AIFunctionFactory.Create(
            (string text) => TextTools.CountText(text), "text_count",
            "Count characters, words, and lines in text."));
        registry.RegisterTool("text_hash", AIFunctionFactory.Create(
            (string text, string algorithm) => TextTools.HashText(text, algorithm), "text_hash",
            "Hash text (MD5, SHA1, SHA256, SHA384, SHA512)."));
        registry.RegisterTool("text_base64", AIFunctionFactory.Create(
            (string text, string operation) => TextTools.Base64Transform(text, operation), "text_base64",
            "Encode/decode Base64."));
        registry.RegisterTool("text_format_json", AIFunctionFactory.Create(
            (string json) => TextTools.FormatJson(json), "text_format_json",
            "Format JSON string with indentation."));
        registry.RegisterTool("text_convert_case", AIFunctionFactory.Create(
            (string text, string targetCase) => TextTools.ConvertCase(text, targetCase), "text_convert_case",
            "Convert text case: upper, lower, title, camel, pascal, snake, kebab."));
        registry.RegisterTool("text_regex_replace", AIFunctionFactory.Create(
            (string text, string pattern, string replacement) => TextTools.RegexReplace(text, pattern, replacement), "text_regex_replace",
            "Search and replace using regex."));
        registry.RegisterTool("text_regex_extract", AIFunctionFactory.Create(
            (string text, string pattern) => TextTools.RegexExtract(text, pattern), "text_regex_extract",
            "Extract text matching a regex pattern."));
        registry.RegisterTool("text_trim", AIFunctionFactory.Create(
            (string text, string mode) => TextTools.Trim(text, mode), "text_trim",
            "Trim whitespace from start, end, or both sides of text."));
        registry.RegisterTool("text_concat", AIFunctionFactory.Create(
            (string partsJson, string? separator) => TextTools.Concat(partsJson, separator), "text_concat",
            "Concatenate multiple text strings with optional separator."));
        return 9;
    }

    private static int RegisterDataTools(AIToolRegistry registry)
    {
        registry.RegisterTool("data_parse_csv", AIFunctionFactory.Create(
            (string csv, string delimiter) => DataTools.ParseCsv(csv, delimiter), "data_parse_csv",
            "Parse CSV string to JSON array."));
        registry.RegisterTool("data_query_json", AIFunctionFactory.Create(
            (string json, string jsonPath) => DataTools.QueryJson(json, jsonPath), "data_query_json",
            "Query JSON using a JSONPath expression."));
        registry.RegisterTool("data_convert_format", AIFunctionFactory.Create(
            (string data, string sourceFormat, string targetFormat) => DataTools.ConvertFormat(data, sourceFormat, targetFormat), "data_convert_format",
            "Convert between JSON and CSV."));
        registry.RegisterTool("data_pretty_print", AIFunctionFactory.Create(
            (string json) => DataTools.PrettyPrint(json), "data_pretty_print",
            "Pretty-print JSON with indentation."));
        registry.RegisterTool("data_pluck", AIFunctionFactory.Create(
            (string jsonArray, string propertyName) => DataTools.Pluck(jsonArray, propertyName), "data_pluck",
            "Extract a property from each object in a JSON array."));
        return 5;
    }

    private static int RegisterDateTimeTools(AIToolRegistry registry)
    {
        registry.RegisterTool("datetime_now", AIFunctionFactory.Create(
            (string? timezoneOffset) => DateTimeTools.GetCurrentDateTime(timezoneOffset), "datetime_now",
            "Get current date and time."));
        registry.RegisterTool("datetime_from_timestamp", AIFunctionFactory.Create(
            (long timestamp, string unit) => DateTimeTools.FromTimestamp(timestamp, unit), "datetime_from_timestamp",
            "Convert Unix timestamp to human-readable datetime."));
        registry.RegisterTool("datetime_diff", AIFunctionFactory.Create(
            (string date1, string date2) => DateTimeTools.DateDifference(date1, date2), "datetime_diff",
            "Calculate the time difference between two dates."));
        registry.RegisterTool("datetime_add", AIFunctionFactory.Create(
            (string dateStr, double amount, string unit) => DateTimeTools.DateAdd(dateStr, amount, unit), "datetime_add",
            "Add or subtract time from a date."));
        registry.RegisterTool("datetime_part", AIFunctionFactory.Create(
            (string dateStr, string? timezoneOffset) => DateTimeTools.DatePart(dateStr, timezoneOffset), "datetime_part",
            "Extract individual date parts: year, month, day, hour, minute, second, dayOfWeek, quarter, etc."));
        return 5;
    }

    private static int RegisterCodeTools(AIToolRegistry registry)
    {
        registry.RegisterTool("code_stats", AIFunctionFactory.Create(
            (string code, string? language) => CodeTools.AnalyzeCode(code, language), "code_stats",
            "Quick code analysis: count lines, detect language, identify functions and classes."));
        registry.RegisterTool("code_generate_snippet", AIFunctionFactory.Create(
            (string pattern, string language) => CodeTools.GenerateSnippet(pattern, language), "code_generate_snippet",
            "Generate a code snippet for common patterns."));
        registry.RegisterTool("code_json_to_class", AIFunctionFactory.Create(
            (string json, string language, string className) => CodeTools.JsonToClass(json, language, className), "code_json_to_class",
            "Convert JSON to C#/Python/TypeScript class definitions."));
        return 3;
    }

    private static int RegisterEnvironmentTools(AIToolRegistry registry)
    {
        registry.RegisterTool("env_sysinfo", AIFunctionFactory.Create(
            () => EnvironmentTools.GetSystemInfo(), "env_sysinfo",
            "Get detailed system information: OS, memory, CPU, drives."));
        registry.RegisterTool("env_get_var", AIFunctionFactory.Create(
            (string name) => EnvironmentTools.GetEnvironmentVariable(name), "env_get_var",
            "Get an environment variable value."));
        registry.RegisterTool("env_processes", AIFunctionFactory.Create(
            (string? filter, int top) => EnvironmentTools.ListProcesses(filter, top), "env_processes",
            "List running processes."));
        registry.RegisterTool("env_network", AIFunctionFactory.Create(
            (string? pingHost, CancellationToken ct) => EnvironmentTools.GetNetworkInfo(pingHost, ct), "env_network",
            "Get network info and optionally ping a host."));
        return 4;
    }

    private static int RegisterWebSearchTools(AIToolRegistry registry)
    {
        registry.RegisterTool("web_fetch_page", AIFunctionFactory.Create(
            (string url, CancellationToken ct) => WebSearchTools.FetchPage(url, ct), "web_fetch_page",
            "Fetch and extract readable text content from a web page."));
        registry.RegisterTool("web_extract_metadata", AIFunctionFactory.Create(
            (string url, CancellationToken ct) => WebSearchTools.ExtractMetadata(url, ct), "web_extract_metadata",
            "Extract meta tags, OG tags, and RSS feeds from a page."));
        registry.RegisterTool("web_search", AIFunctionFactory.Create(
            (string query, int maxResults, CancellationToken ct) => WebSearchTools.WebSearch(query, maxResults, ct), "web_search",
            "Search the web using DuckDuckGo (no API key required)."));
        return 3;
    }

    private static int RegisterGitTools(AIToolRegistry registry)
    {
        return 0;
    }

    private static int RegisterDependencyTools(AIToolRegistry registry)
    {
        registry.RegisterTool("dep_check", AIFunctionFactory.Create(
            (string toolName, CancellationToken ct) => DependencyTools.CheckTool(toolName, ct), "dep_check",
            "Check if a CLI tool is available on system PATH. Returns path and version."));
        registry.RegisterTool("dep_install", AIFunctionFactory.Create(
            (string toolName, string manager, CancellationToken ct) => DependencyTools.InstallTool(toolName, manager, ct), "dep_install",
            "Install a CLI tool using Scoop or Chocolatey. Auto-detects best package manager."));
        registry.RegisterTool("dep_devsuite", AIFunctionFactory.Create(
            (CancellationToken ct) => DependencyTools.InstallDevSuite(ct), "dep_devsuite",
            "Batch install all common dev tools: git, nodejs, python, ffmpeg, curl."));
        registry.RegisterTool("dep_managers", AIFunctionFactory.Create(
            (CancellationToken ct) => DependencyTools.CheckPackageManagers(ct), "dep_managers",
            "Check if Chocolatey and Scoop package managers are installed and working."));
        return 4;
    }

    private static int RegisterUnderstandTools(AIToolRegistry registry)
    {
        registry.RegisterTool("understand_diff", AIFunctionFactory.Create(
            (string? repoPath) => UnderstandDiffTool.AnalyzeImpact(repoPath), "understand_diff",
            "Analyze the impact of recent code changes. Shows changed files, affected directories, risk score, and potential ripple effects."));
        registry.RegisterTool("understand_tour", AIFunctionFactory.Create(
            (string? repoPath) => TourGeneratorTool.GenerateTour(repoPath), "understand_tour",
            "Generate a guided architecture tour of the codebase, ordered by dependency. Start from solution files → config → code → docs."));
        return 2;
    }
}

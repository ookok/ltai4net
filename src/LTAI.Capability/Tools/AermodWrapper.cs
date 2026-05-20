using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace LTAI.Capability.Tools;

internal static class HttpClientExtensions
{
    public static async Task DownloadFileAsync(this HttpClient client, string url, string path)
    {
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        await using var fs = new FileStream(path, FileMode.Create);
        await response.Content.CopyToAsync(fs);
    }
}

public sealed class AermodWrapper
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly string _toolsDir;
    private readonly ILogger<AermodWrapper>? _logger;

    public AermodWrapper(string? toolsDir = null, ILogger<AermodWrapper>? logger = null)
    {
        _toolsDir = toolsDir ?? Path.Combine(Path.GetTempPath(), "ltai_tools", "aermod");
        _logger = logger;
        Directory.CreateDirectory(_toolsDir);
    }

    public bool IsInstalled => File.Exists(Path.Combine(_toolsDir, "aermod.exe"));

    public async Task<bool> EnsureInstalledAsync()
    {
        if (IsInstalled) return true;

        try
        {
            var url = "https://gaftp.epa.gov/Air/aqmg/SCRAM/models/preferred/aermod/aermod_exe.zip";
            var zipPath = Path.Combine(_toolsDir, "aermod_exe.zip");

            await _http.DownloadFileAsync(url, zipPath);
            ZipFile.ExtractToDirectory(zipPath, _toolsDir, true);

            return IsInstalled;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AERMOD download failed");
            return false;
        }
    }

    public async Task<AermodResult> RunAsync(AermodInput input, CancellationToken ct = default)
    {
        if (!await EnsureInstalledAsync())
            return new AermodResult { Error = "AERMOD executable not available. Download from EPA SCRAM: https://gaftp.epa.gov/Air/aqmg/SCRAM/models/preferred/aermod/" };

        var runDir = Path.Combine(_toolsDir, $"run_{DateTime.Now:yyyyMMddHHmmss}");
        Directory.CreateDirectory(runDir);

        try
        {
            var inpPath = Path.Combine(runDir, "aermod.inp");
            await File.WriteAllTextAsync(input.GenerateInputFile(), inpPath, ct);

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(_toolsDir, "aermod.exe"),
                WorkingDirectory = runDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return new AermodResult { Error = "Failed to start AERMOD" };

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            return AermodResultParser.ParseOutput(stdout, stderr, proc.ExitCode);
        }
        catch (Exception ex) { return new AermodResult { Error = ex.Message }; }
    }
}

public sealed class AermodInput
{
    public string Title { get; set; } = "LTAI AERMOD Run";
    public string ModelType { get; set; } = "CO"; // CO=concentration, DF=deposition
    public string AvgTime { get; set; } = "1"; // 1-hour average
    public string PollutantId { get; set; } = "SO2";
    public double EmissionRate { get; set; } = 1.0;
    public double StackHeight { get; set; } = 50;
    public double StackDiameter { get; set; } = 2;
    public double ExitVelocity { get; set; } = 15;
    public double ExitTemperature { get; set; } = 400;
    public double UrbanRural { get; set; } = 1; // 1=urban
    public double SurfaceRoughness { get; set; } = 0.5;
    public string MetDataPath { get; set; } = "aermet.sfc";
    public string ProfilePath { get; set; } = "aermet.pfl";
    public List<(double x, double y, double z, string name)> Receptors { get; set; } = new()
    {
        (0, 0, 0, "origin"), (100, 0, 0, "R1"), (200, 0, 0, "R2"),
        (500, 0, 0, "R3"), (1000, 0, 0, "R4"), (2000, 0, 0, "R5")
    };

    public string GenerateInputFile()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("CO STARTING");
        sb.AppendLine($"  TITLEONE {Title}");
        sb.AppendLine("  MODELOPT DFAULT CONC FLAT");
        sb.AppendLine($"  AVERTIME {AvgTime}");
        sb.AppendLine($"  POLLUTID {PollutantId}");
        sb.AppendLine("  RUNORNOT RUN");
        sb.AppendLine("CO FINISHED");
        sb.AppendLine("SO STARTING");
        sb.AppendLine($"  LOCATION SRC1 POINT {0} {0}");
        sb.AppendLine($"  SRCPARAM SRC1 {EmissionRate} {StackHeight} {ExitTemperature} {ExitVelocity} {StackDiameter}");
        sb.AppendLine($"  URBANSRC ALL");
        sb.AppendLine($"  SRCGROUP ALL");
        sb.AppendLine("SO FINISHED");
        sb.AppendLine("RE STARTING");
        foreach (var (x, y, z, name) in Receptors)
            sb.AppendLine($"  DISCCART {x} {y} {z}");
        sb.AppendLine("RE FINISHED");
        sb.AppendLine($"ME STARTING");
        sb.AppendLine($"  SURFFILE {MetDataPath}");
        sb.AppendLine($"  PROFFILE {ProfilePath}");
        sb.AppendLine($"  SURFDATA 99999 2024");
        sb.AppendLine($"  UAIRDATA 99999 2024");
        sb.AppendLine($"  PROFBASE 0.0 METERS");
        sb.AppendLine("ME FINISHED");
        sb.AppendLine($"OU STARTING");
        sb.AppendLine($"  RECTABLE ALLAVE FIRST");
        sb.AppendLine($"  MAXTABLE ALLAVE 50");
        sb.AppendLine($"  PLOTFILE 1 ALL FIRST {PollutantId}.plt");
        sb.AppendLine("OU FINISHED");
        return sb.ToString();
    }
}

public sealed class AermodResult
{
    public bool Success => string.IsNullOrEmpty(Error) && Results.Count > 0;
    public string? Error { get; set; }
    public List<AermodReceptorResult> Results { get; set; } = new();
    public string? RawOutput { get; set; }
    public long DurationMs { get; set; }

    public Dictionary<string, object> ToSummary() => new()
    {
        ["success"] = Success,
        ["receptors"] = Results.Count,
        ["max_concentration"] = Results.Count > 0 ? Results.Max(r => r.Concentration) : 0,
        ["max_receptor"] = Results.Count > 0 ? Results.OrderByDescending(r => r.Concentration).First().Name : "",
        ["error"] = Error
    };
}

public sealed record AermodReceptorResult(string Name, double X, double Y, double Concentration);

internal static class AermodResultParser
{
    public static AermodResult ParseOutput(string stdout, string stderr, int exitCode)
    {
        if (exitCode != 0 || (!string.IsNullOrEmpty(stderr) && stderr.Contains("ERROR")))
            return new AermodResult { Error = stderr.Length > 500 ? stderr[..500] : stderr, RawOutput = stdout };

        var results = new List<AermodReceptorResult>();
        var lines = stdout.Split('\n');

        foreach (var line in lines)
        {
            if (line.Contains("CONC") && char.IsDigit(line.TrimStart().FirstOrDefault()))
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 &&
                    double.TryParse(parts[0], out var x) &&
                    double.TryParse(parts[1], out var y) &&
                    double.TryParse(parts[3], out var conc))
                {
                    results.Add(new AermodReceptorResult($"R{results.Count}", x, y, conc));
                }
            }
        }

        return new AermodResult { Results = results, RawOutput = stdout[..Math.Min(2000, stdout.Length)] };
    }
}

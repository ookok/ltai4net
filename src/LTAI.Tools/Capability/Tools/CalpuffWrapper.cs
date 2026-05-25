using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Tools;

public sealed class CalpuffWrapper
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly string _toolsDir;
    private readonly ILogger<CalpuffWrapper>? _logger;

    public CalpuffWrapper(string? toolsDir = null, ILogger<CalpuffWrapper>? logger = null)
    {
        _toolsDir = toolsDir ?? Path.Combine(Path.GetTempPath(), "ltai_tools", "calpuff");
        _logger = logger;
        Directory.CreateDirectory(_toolsDir);
    }

    public bool IsInstalled => File.Exists(Path.Combine(_toolsDir, "calpuff.exe"));

    public async Task<bool> EnsureInstalledAsync()
    {
        if (IsInstalled) return true;
        try
        {
            await Task.WhenAll(
                DownloadAndExtractAsync("https://gaftp.epa.gov/Air/aqmg/SCRAM/models/preferred/calpuff/calpuff_v7.2.1_L150223.zip", "calpuff"),
                DownloadAndExtractAsync("https://gaftp.epa.gov/Air/aqmg/SCRAM/models/preferred/calpuff/calmet_v6.5.0_L150223.zip", "calmet"),
                DownloadAndExtractAsync("https://gaftp.epa.gov/Air/aqmg/SCRAM/models/preferred/calpuff/calpost_v7.1.0_L150223.zip", "calpost")
            );
            return IsInstalled;
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "CALPUFF download failed"); return false; }
    }

    private async Task DownloadAndExtractAsync(string url, string subDir)
    {
        var dir = Path.Combine(_toolsDir, subDir);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, Path.GetFileName(url));
        var response = await _http.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var fs = new FileStream(zipPath, FileMode.Create);
        await response.Content.CopyToAsync(fs).ConfigureAwait(false);
        ZipFile.ExtractToDirectory(zipPath, dir, true);
    }

    public async Task<CalpuffResult> RunFullAsync(CalpuffInput input, CancellationToken ct = default)
    {
        if (!await EnsureInstalledAsync())
            return new CalpuffResult { Error = "CALPUFF/CALMET/CALPOST not available. Download from EPA SCRAM: https://gaftp.epa.gov/Air/aqmg/SCRAM/models/preferred/calpuff/" };

        var runDir = Path.Combine(_toolsDir, $"run_{DateTime.Now:yyyyMMddHHmmss}");
        Directory.CreateDirectory(runDir);

        try
        {
            var calmetOk = await RunCalmetAsync(input, runDir, ct).ConfigureAwait(false);
            if (!calmetOk) return new CalpuffResult { Error = "CALMET preprocessing failed" };

            var calpuffOk = await RunCalpuffAsync(input, runDir, ct).ConfigureAwait(false);
            if (!calpuffOk) return new CalpuffResult { Error = "CALPUFF dispersion failed" };

            var results = await RunCalpostAsync(input, runDir, ct).ConfigureAwait(false);
            return results;
        }
        catch (Exception ex) { return new CalpuffResult { Error = ex.Message }; }
    }

    private async Task<bool> RunCalmetAsync(CalpuffInput input, string runDir, CancellationToken ct)
    {
        var inpPath = Path.Combine(runDir, "calmet.inp");
        await File.WriteAllTextAsync(input.GenerateCalmetInput(runDir), inpPath, ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_toolsDir, "calmet", "calmet.exe"),
            Arguments = $"calmet.inp",
            WorkingDirectory = runDir,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return false;
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return proc.ExitCode == 0;
    }

    private async Task<bool> RunCalpuffAsync(CalpuffInput input, string runDir, CancellationToken ct)
    {
        var inpPath = Path.Combine(runDir, "calpuff.inp");
        await File.WriteAllTextAsync(input.GenerateCalpuffInput(runDir), inpPath, ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_toolsDir, "calpuff", "calpuff.exe"),
            Arguments = $"calpuff.inp",
            WorkingDirectory = runDir,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return false;
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return proc.ExitCode == 0;
    }

    private async Task<CalpuffResult> RunCalpostAsync(CalpuffInput input, string runDir, CancellationToken ct)
    {
        var inpPath = Path.Combine(runDir, "calpost.inp");
        await File.WriteAllTextAsync(input.GenerateCalpostInput(runDir), inpPath, ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_toolsDir, "calpost", "calpost.exe"),
            Arguments = $"calpost.inp",
            WorkingDirectory = runDir,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return new CalpuffResult { Error = "Failed to start CALPOST" };

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        var lstPath = Path.Combine(runDir, "calpost.lst");
        if (File.Exists(lstPath))
        {
            var lstContent = await File.ReadAllTextAsync(lstPath, ct).ConfigureAwait(false);
            return ParseCalpostOutput(lstContent);
        }

        return new CalpuffResult { Error = "CALPOST output file not found", RawOutput = stdout };
    }

    private static CalpuffResult ParseCalpostOutput(string lstContent)
    {
        var results = new List<CalpuffReceptorResult>();
        var lines = lstContent.Split('\n');

        foreach (var line in lines)
        {
            if (line.Contains("CONC") || line.Contains("ug/m"))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    for (var i = 0; i < parts.Length - 1; i++)
                    {
                        if (double.TryParse(parts[i], out var conc) && conc > 0.001)
                        {
                            double.TryParse(parts.ElementAtOrDefault(i - 1), out var y);
                            double.TryParse(parts.ElementAtOrDefault(i - 2), out var x);
                            results.Add(new CalpuffReceptorResult($"R{results.Count}", x, y, Math.Round(conc, 4)));
                            break;
                        }
                    }
                }
            }
        }

        return new CalpuffResult { Results = results, RawOutput = lstContent[..Math.Min(2000, lstContent.Length)] };
    }
}

public sealed class CalpuffInput
{
    public string Title { get; set; } = "LTAI CALPUFF Run";
    public string Projection { get; set; } = "UTM";
    public string UtmZone { get; set; } = "50";
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public int MetDays { get; set; } = 30;
    public double CellSize { get; set; } = 500;
    public int Nx { get; set; } = 20;
    public int Ny { get; set; } = 20;

    public double EmissionRate { get; set; } = 1.0;
    public double StackHeight { get; set; } = 50;
    public double StackDiameter { get; set; } = 2;
    public double ExitVelocity { get; set; } = 15;
    public double ExitTemperature { get; set; } = 400;
    public double SourceLat { get; set; } = 39.9;
    public double SourceLon { get; set; } = 116.4;

    public List<(double x, double y, double z, string name)> Receptors { get; set; } = new()
    {
        (0, 0, 0, "origin"), (200, 0, 0, "R1"), (500, 0, 0, "R2"),
        (1000, 0, 0, "R3"), (2000, 0, 0, "R4"), (5000, 0, 0, "R5")
    };

    public string GenerateCalmetInput(string runDir) => $"""
CALMET CONTROL FILE
-------------------
! Basic setup: {Title}
METRUN = 1
IBCR = 0
NSSTA = 1
NUSTA = 1
NOWSTA = 1
NPSTA = 0
NZ = 8
IREG = 0
IOUT = 0
RESTART = 0
! Grid definition
DGRIDKM = {CellSize / 1000:F2}
XORIGKM = {OriginX}
YORIGKM = {OriginY}
NX = {Nx}
NY = {Ny}
NZD = 8
! Time
IYEAR = 2024
IMO = 1
IDY = 1
IBHR = 0
IRHP = {MetDays * 24}
! Surface station
SSNAME = 'SFC_STATION'
SX = 0.0
SY = 0.0
STZ = 10.0
! Upper air
USNAME = 'UA_STATION'
UX = 0.0
UY = 0.0
UTZ = 50.0
! Output
DATASAV = 0
OUTFILE = {Path.Combine(runDir, "calmet.dat")}
SURFDAT = {Path.Combine(runDir, "surf.dat")}
UPRODAT = {Path.Combine(runDir, "uprof.dat")}
""";

    public string GenerateCalpuffInput(string runDir) => $"""
CALPUFF CONTROL FILE
---------------------
! Title: {Title}
METRUN = 0
METDAT = {Path.Combine(runDir, "calmet.dat")}
! ---- General run control ----
MCHEM = 0
MWET = 1
MDRY = 1
MTIP = 0
MBDW = 0
! ---- Time ----
NSPT = 1
IRHP = {MetDays * 24}
! ---- Source ----
NSRC = 1
! Source 1
SRCX(1) = {OriginX}
SRCY(1) = {OriginY}
SRCZ(1) = {StackHeight}
SRCDM(1) = {StackDiameter}
SRCVV(1) = {ExitVelocity}
SRCTP(1) = {ExitTemperature}
SRCEM(1) = {EmissionRate}
SRCG1(1) = {SourceLat}
SRCG2(1) = {SourceLon}
! ---- Receptors ----
NREC = {Receptors.Count}
{string.Join("\n", Receptors.Select((r, i) => $"XR({i + 1}) = {r.x}  YR({i + 1}) = {r.y}  ZR({i + 1}) = {r.z}"))}
! ---- Output ----
NSPT = 1
OUTDAT = {Path.Combine(runDir, "calpuff.dat")}
""";

    public string GenerateCalpostInput(string runDir) => $"""
CALPOST CONTROL FILE
---------------------
! Title: {Title} Post-processing
MODDAT = {Path.Combine(runDir, "calpuff.dat")}
! Processing options
IPROC = 1
ITAB = 1
IPLT = 1
AVET = 1.0
! Period
IBDAT = 24001
IEDAT = 24030
! Output
OUTDAT = {Path.Combine(runDir, "calpost.dat")}
LSTFIL = {Path.Combine(runDir, "calpost.lst")}
""";
}

public sealed class CalpuffResult
{
    public bool Success => string.IsNullOrEmpty(Error) && Results.Count > 0;
    public string? Error { get; set; }
    public List<CalpuffReceptorResult> Results { get; set; } = new();
    public string? RawOutput { get; set; }

    public Dictionary<string, object> ToSummary() => new()
    {
        ["success"] = Success,
        ["receptors"] = Results.Count,
        ["max_concentration"] = Results.Count > 0 ? Results.Max(r => r.Concentration) : 0,
        ["max_distance"] = Results.Count > 0 ? Results.OrderByDescending(r => r.Concentration).First().Distance.ToString() : "0",
        ["model"] = "CALPUFF (CALMET + CALPUFF + CALPOST pipeline)",
        ["error"] = Error ?? string.Empty
    };
}

public sealed record CalpuffReceptorResult(string Name, double X, double Y, double Concentration)
{
    public double Distance => Math.Sqrt(X * X + Y * Y);
}

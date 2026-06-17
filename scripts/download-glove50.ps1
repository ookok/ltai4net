param(
    [string]$OutputDir = ""
)

$ScriptDir = Split-Path -Parent $PSCommandPath
$ProjectRoot = Split-Path -Parent $ScriptDir
if (-not $OutputDir) { $OutputDir = Join-Path $ProjectRoot "models" }
$Gv50Path = Join-Path $OutputDir "glove50d.gv50"
$ModelsDir = $OutputDir
if (-not (Test-Path $ModelsDir)) { New-Item -ItemType Directory -Path $ModelsDir -Force | Out-Null }

Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  GloVe-50d → .gv50 下载 + 转换" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# Step 1: Download glove.6B.zip (862MB, contains 50d/100d/200d/300d)
$ZipPath = Join-Path $env:TEMP "glove.6B.zip"
$ExtractDir = Join-Path $env:TEMP "glove.6B"

if (-not (Test-Path $ZipPath)) {
    $url = "https://hf-mirror.com/stanfordnlp/glove/resolve/main/glove.6B.zip"
    Write-Host "Step 1: 下载 GloVe-6B 原始数据..." -ForegroundColor Yellow
    Write-Host "  URL: $url" -ForegroundColor Gray
    Write-Host "  大小: 862 MB (ZIP, 包含 50d/100d/200d/300d)" -ForegroundColor Gray
    Write-Host "  适合首次使用，只需一次下载" -ForegroundColor Gray
    Write-Host ""
    
    try {
        $progressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $url -OutFile $ZipPath -TimeoutSec 7200 -ErrorAction Stop
        Write-Host "  ✅ 下载完成: $((Get-Item $ZipPath).Length / 1MB) MB" -ForegroundColor Green
    }
    catch {
        Write-Host "  ❌ 下载失败: $_" -ForegroundColor Red
        Write-Host ""
        Write-Host "  备用方案: 从以下地址手动下载 glove.6B.zip:" -ForegroundColor Yellow
        Write-Host "    https://nlp.stanford.edu/data/glove.6B.zip" -ForegroundColor Cyan
        Write-Host "  解压出 glove.6B.50d.txt 放到 $ModelsDir" -ForegroundColor Cyan
        Write-Host "  然后重新运行此脚本" -ForegroundColor Cyan
        exit 1
    }
    $progressPreference = 'Continue'
}
else {
    Write-Host "Step 1: 使用本地缓存 ZIP: $ZipPath" -ForegroundColor Green
}

# Step 2: Extract glove.6B.50d.txt
$RawPath = Join-Path $ModelsDir "glove.6B.50d.txt"
if (-not (Test-Path $RawPath)) {
    Write-Host "Step 2: 解压 glove.6B.50d.txt ..." -ForegroundColor Yellow
    try {
        if (Test-Path $ExtractDir) { Remove-Item -Recurse -Force $ExtractDir -ErrorAction SilentlyContinue }
        Expand-Archive -Path $ZipPath -DestinationPath $ExtractDir -Force -ErrorAction Stop
        Move-Item (Join-Path $ExtractDir "glove.6B.50d.txt") $RawPath -Force
        Remove-Item -Recurse -Force $ExtractDir -ErrorAction SilentlyContinue
        Write-Host "  ✅ 解压完成: $((Get-Item $RawPath).Length / 1MB) MB" -ForegroundColor Green
    }
    catch {
        Write-Host "  ❌ 解压失败: $_" -ForegroundColor Red
        exit 1
    }
}
else {
    Write-Host "Step 2: 使用本地缓存 TXT: $RawPath" -ForegroundColor Green
}

# Step 3: Convert to .gv50
if (Test-Path $Gv50Path) {
    $existingSize = (Get-Item $Gv50Path).Length
    if ($existingSize -gt 1MB) {
        Write-Host "Step 3: .gv50 已存在: $Gv50Path ($($existingSize / 1MB) MB)" -ForegroundColor Green
        Write-Host "  如需重新生成，先删除该文件" -ForegroundColor Gray
        exit 0
    }
}

Write-Host "Step 3: 转换 → .gv50 紧凑格式..." -ForegroundColor Yellow

$TempDir = Join-Path $env:TEMP "gv50-convert"
if (Test-Path $TempDir) { Remove-Item -Recurse -Force $TempDir }
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

$CsprojPath = Join-Path $TempDir "Convert.csproj"
$ProgPath = Join-Path $TempDir "Program.cs"

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
"@ | Set-Content -Path $CsprojPath

@"
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

string txtPath = @"$RawPath";
string gv50Path = @"$Gv50Path";

Console.WriteLine("Reading: " + txtPath);
var lines = File.ReadLines(txtPath);
var words = new List<(string word, float[] vec)>();

foreach (var line in lines)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 51) continue;

    var word = parts[0];
    if (word.Length > 255) continue;
    var vec = new float[50];
    bool ok = true;
    for (int i = 0; i < 50; i++)
    {
        if (!float.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out vec[i]))
        { ok = false; break; }
    }
    if (ok) words.Add((word, vec));
}

Console.WriteLine("Vocabulary: " + words.Count + " words");
Directory.CreateDirectory(Path.GetDirectoryName(gv50Path));

using var fs = new FileStream(gv50Path, FileMode.Create);
using var bw = new BinaryWriter(fs);
bw.Write(words.Count);
var buf = new byte[200];

foreach (var (w, v) in words)
{
    var wb = Encoding.UTF8.GetBytes(w);
    bw.Write((ushort)wb.Length);
    bw.Write(wb);
    Buffer.BlockCopy(v, 0, buf, 0, 200);
    bw.Write(buf);
}

var fi = new FileInfo(gv50Path);
Console.WriteLine("Success! " + fi.Length.ToString("N0") + " bytes for " + words.Count + " words");
"@ | Set-Content -Path $ProgPath

Write-Host "  Building converter..."
dotnet build $CsprojPath -nologo -v q 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "  ❌ Build failed" -ForegroundColor Red; exit 1 }

Write-Host "  Converting... (this may take a minute for 400K words)"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
dotnet run --project $CsprojPath --no-build 2>&1
$sw.Stop()

if (Test-Path $Gv50Path) {
    $size = (Get-Item $Gv50Path).Length
    Write-Host ""
    Write-Host "✅ 完成!" -ForegroundColor Green
    Write-Host "   文件: $Gv50Path" -ForegroundColor Green
    Write-Host "   大小: $($size / 1MB) MB" -ForegroundColor Green
    Write-Host "   耗时: $($sw.Elapsed.TotalSeconds) 秒" -ForegroundColor Green
}
else {
    Write-Host "❌ 转换失败" -ForegroundColor Red
}

Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using LTAI.Core.Governors;
using LTAI.Core.Setup;

namespace LTAI.Cli.Model;

internal static class ModelMode
{
    private static string ModelsDir
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var rootDir = FindRootDirectory(baseDir, "models");
            if (rootDir != null)
                return Path.Combine(rootDir, "models");
            return Path.Combine(baseDir, "models");
        }
    }

    public static async Task<int> RunAsync(string? command, string? layer, string? version, bool useMirror, bool force, bool rerunSetup = false)
    {
        var downloader = new ModelDownloader();

        if (string.IsNullOrEmpty(command) || command == "list")
        {
            return ListModels(downloader, layer);
        }

        if (command == "download")
        {
            return await DownloadModelAsync(downloader, layer, version, useMirror, force).ConfigureAwait(false);
        }

        if (command == "remove")
        {
            return RemoveModel(downloader, layer, version);
        }

        if (command == "reset")
        {
            return ResetAllModels(downloader, rerunSetup);
        }

        Console.WriteLine($"Unknown model command: {command}");
        Console.WriteLine("Available commands: list, download, remove, reset");
        return 1;
    }

    private static int ListModels(ModelDownloader downloader, string? layerFilter)
    {
        Console.WriteLine("=== LTAI Model Catalog ===\n");

        var layers = string.IsNullOrEmpty(layerFilter)
            ? new[] { ModelLayer.L0, ModelLayer.L1, ModelLayer.L2 }
            : new[] { ParseLayer(layerFilter) };

        var hwInfo = DetectHardwareCapabilities();
        Console.WriteLine($"Hardware: {hwInfo.CpuCores} cores | {hwInfo.MemoryMB}MB RAM | GPU: {(hwInfo.HasGpu ? hwInfo.GpuName : "None")} | NPU: {(hwInfo.HasNpu ? "Yes" : "No")}");
        Console.WriteLine($"Recommended engine: {hwInfo.RecommendedEngine.ToUpper()}\n");

        foreach (var layer in layers)
        {
            var models = LocalModelRegistry.GetByLayer(layer);
            if (models.Count == 0) continue;

            Console.WriteLine($"--- {layer} ({models.Count} models) ---");
            Console.WriteLine();
            Console.WriteLine($"| # | Version | Name | Size | Engine | Status |");
            Console.WriteLine($"|---|---------|------|------|--------|--------|");

            for (int i = 0; i < models.Count; i++)
            {
                var m = models[i];
                var installed = downloader.IsModelInstalled(m.Version, ModelsDir);
                var status = installed ? "✅ Installed" : "Not installed";
                var rec = m.EngineType == hwInfo.RecommendedEngine ? " ⭐" : "";
                Console.WriteLine($"| {i + 1} | {m.Version} | {m.Name}{rec} | {m.DiskSizeMB}MB | {m.EngineType.ToUpper()} | {status} |");
            }
            Console.WriteLine();
        }

        Console.WriteLine("Usage: ltai model download --layer L1 --version qwen2.5-1.5b-q4");
        Console.WriteLine("       ltai model remove --layer L1 --version qwen2.5-1.5b-q4");
        Console.WriteLine("       ltai model list --layer L1");

        return 0;
    }

    private static async Task<int> DownloadModelAsync(ModelDownloader downloader, string? layer, string? version, bool useMirror, bool force)
    {
        if (string.IsNullOrEmpty(version))
        {
            Console.WriteLine("Error: --version is required for download.");
            return 1;
        }

        var model = LocalModelRegistry.GetByVersion(version);
        if (model == null)
        {
            Console.WriteLine($"Error: Unknown model version '{version}'.");
            Console.WriteLine("Run 'ltai model list' to see available models.");
            return 1;
        }

        if (!string.IsNullOrEmpty(layer))
        {
            var parsedLayer = ParseLayer(layer);
            if (model.Layer != parsedLayer)
            {
                Console.WriteLine($"Warning: Model {version} is a {model.Layer} model, not {parsedLayer}.");
            }
        }

        if (downloader.IsModelInstalled(version, ModelsDir) && !force)
        {
            Console.WriteLine($"✅ Model {model.Name} is already installed.");
            Console.WriteLine($"   Path: {GetModelPath(model)}");
            Console.WriteLine("   Use --force to re-download.");
            return 0;
        }

        Console.WriteLine($"📥 Downloading {model.Name}...");
        Console.WriteLine($"   Layer: {model.Layer}");
        Console.WriteLine($"   Engine: {model.EngineType.ToUpper()}");
        Console.WriteLine($"   Size: {model.DiskSizeMB} MB");
        Console.WriteLine($"   Mirror: {(useMirror ? "Enabled (hf-mirror.com)" : "Auto-detect")}");
        Console.WriteLine();

        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            Console.Write($"\r   {p.Percent:F1}% ({p.DownloadedBytes / 1024.0 / 1024.0:F1}/{p.TotalBytes / 1024.0 / 1024.0:F1} MB, {p.SpeedMBps:F1} MB/s)");
        });

        try
        {
            var path = await downloader.DownloadAsync(model, ModelsDir, progress).ConfigureAwait(false);
            Console.WriteLine($"\n\n✅ Model downloaded successfully!");
            Console.WriteLine($"   Path: {path}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n❌ Download failed: {ex.Message}");
            return 1;
        }
    }

    private static int RemoveModel(ModelDownloader downloader, string? layer, string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            Console.WriteLine("Error: --version is required for remove.");
            return 1;
        }

        var model = LocalModelRegistry.GetByVersion(version);
        if (model == null)
        {
            Console.WriteLine($"Error: Unknown model version '{version}'.");
            return 1;
        }

        if (!downloader.IsModelInstalled(version, ModelsDir))
        {
            Console.WriteLine($"Model {model.Name} is not installed.");
            return 0;
        }

        downloader.RemoveModel(version, ModelsDir);
        Console.WriteLine($"🗑️ Removed {model.Name}");
        return 0;
    }

    private static int ResetAllModels(ModelDownloader downloader, bool rerunSetup)
    {
        var modelsDir = ModelsDir;
        
        if (!Directory.Exists(modelsDir))
        {
            Console.WriteLine("No models directory found. Nothing to reset.");
            return 0;
        }

        var layerDirs = new[] { "l0", "l1", "l2" };
        var deletedCount = 0;
        var deletedSize = 0L;

        foreach (var ld in layerDirs)
        {
            var dir = Path.Combine(modelsDir, ld);
            if (!Directory.Exists(dir)) continue;

            var files = Directory.GetFiles(dir);
            foreach (var f in files)
            {
                var fi = new FileInfo(f);
                deletedSize += fi.Length;
                File.Delete(f);
                deletedCount++;
            }

            if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                Directory.Delete(dir);
        }

        Console.WriteLine("=== LTAI Model Reset ===");
        Console.WriteLine();
        Console.WriteLine($"🗑️  Deleted {deletedCount} model file(s) ({deletedSize / 1024.0 / 1024.0:F1} MB)");
        Console.WriteLine();

        // 清除 local_llm.json 等配置
        var configFiles = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "local_llm.json"),
            Path.Combine(AppContext.BaseDirectory, "local_l0.json"),
            Path.Combine(AppContext.BaseDirectory, "local_l1.json"),
            Path.Combine(AppContext.BaseDirectory, "local_l2.json")
        };
        foreach (var cf in configFiles)
        {
            if (File.Exists(cf))
            {
                File.Delete(cf);
                Console.WriteLine($"   Cleared config: {Path.GetFileName(cf)}");
            }
        }
        Console.WriteLine();

        if (rerunSetup)
        {
            Console.WriteLine("🔄 Restarting setup wizard...");
            Console.WriteLine();

            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var wizard = new InteractiveSetupWizard(configPath);
            Task.Run(() => wizard.RunAsync()).GetAwaiter().GetResult();
        }
        else
        {
            Console.WriteLine("Tip: Run 'ltai model reset --setup' to also restart the setup wizard.");
            Console.WriteLine("     Run '.\\setup-models.ps1' to one-click re-download all models.");
        }

        return 0;
    }

    private static string GetModelPath(LocalModelInfo model)
    {
        var layerDir = model.Layer.ToString().ToLowerInvariant();
        var dir = Path.Combine(ModelsDir, layerDir);
        if (model.Layer == ModelLayer.L0 && model.EngineType == "onnx")
            return Path.Combine(dir, "model.onnx");
        return Path.Combine(dir, $"{model.Version}.{model.EngineType}");
    }

    private static ModelLayer ParseLayer(string layer)
    {
        return layer.ToUpperInvariant() switch
        {
            "L0" or "EMBEDDING" => ModelLayer.L0,
            "L1" or "FAST" => ModelLayer.L1,
            "L2" or "DEEP" => ModelLayer.L2,
            _ => throw new ArgumentException($"Unknown layer: {layer}. Use L0, L1, or L2.")
        };
    }

    private static string? FindRootDirectory(string startDir, string markerDir)
    {
        var current = startDir;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, markerDir)))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private static HardwareInfo DetectHardwareCapabilities()
    {
        var memMB = LocalModelRegistry.DetectAvailableMemoryMB();
        var cores = Environment.ProcessorCount;
        bool hasGpu = false;
        string gpuName = "None";
        bool hasNpu = false;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var p = new Process();
                p.StartInfo = new ProcessStartInfo("powershell", "-Command \"Get-PnpDevice -Class Display | Where-Object Status -eq 'OK' | Select-Object -ExpandProperty FriendlyName\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                p.Start();
                var gpuOutput = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                if (!string.IsNullOrEmpty(gpuOutput))
                {
                    hasGpu = true;
                    gpuName = gpuOutput.Split('\n').First().Trim();
                }

                using var p2 = new Process();
                p2.StartInfo = new ProcessStartInfo("powershell", "-NoProfile -Command \"Get-PnpDevice -Class ComputingAccelerator -ErrorAction SilentlyContinue | Where-Object Status -eq 'OK' | Select-Object -ExpandProperty FriendlyName\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                p2.Start();
                var npuOutput = p2.StandardOutput.ReadToEnd().Trim();
                _ = p2.StandardError.ReadToEnd();
                p2.WaitForExit(3000);
                hasNpu = !string.IsNullOrEmpty(npuOutput);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                hasGpu = File.Exists("/dev/dri/renderD128") || File.Exists("/dev/nvidia0");
                if (hasGpu)
                {
                    try
                    {
                        var lspci = Process.Start(new ProcessStartInfo("lspci", "-d ::0300") { RedirectStandardOutput = true, UseShellExecute = false });
                        gpuName = lspci?.StandardOutput.ReadLine()?.Trim() ?? "GPU";
                    }
                    catch { }
                }
            }
        }
        catch { }

        string recommendedEngine;
        if (hasNpu)
            recommendedEngine = "onnx";
        else
            recommendedEngine = "gguf";

        return new HardwareInfo(cores, memMB, hasGpu, gpuName, hasNpu, recommendedEngine);
    }

    private record HardwareInfo(int CpuCores, long MemoryMB, bool HasGpu, string GpuName, bool HasNpu, string RecommendedEngine);
}

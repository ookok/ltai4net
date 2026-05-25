namespace LTAI.Core.Governors;

public sealed class HardwareDetector
{
    public record HardwareInfo(long RamMB, long VramMB, int CpuCores, long FreeDiskMB, string? GpuName)
    {
        public double RamGB => RamMB / 1024.0;
        public double VramGB => VramMB / 1024.0;
        public double AvailableMemoryGB => VramMB > 0 ? VramGB : RamGB;
        public bool HasGpu => VramMB > 0;
    }

    public static HardwareInfo Detect()
    {
        long ramMB = 8192, vramMB = 0;
        int cpu = Environment.ProcessorCount;
        long diskMB = 10240;
        string? gpuName = null;

        try
        {
            var gcMem = GC.GetGCMemoryInfo();
            ramMB = gcMem.TotalAvailableMemoryBytes / (1024 * 1024);
        }
        catch (Exception ex) { }

        try
        {
            var baseDir = AppContext.BaseDirectory;
            var root = Path.GetPathRoot(baseDir);
            if (root != null)
                diskMB = new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
        }
        catch (Exception ex) { }

        try
        {
            using var proc = global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.total,name --format=csv,noheader,nounits",
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            });
            if (proc != null)
            {
                proc.WaitForExit(3000);
                var output = proc.StandardOutput.ReadToEnd().Trim();
                if (output.Length > 0)
                {
                    var lines = output.Split('\n');
                    if (long.TryParse(lines[0].Split(',')[0].Trim(), out var vram))
                    {
                        vramMB = vram;
                        gpuName = lines[0].Split(',').Skip(1).FirstOrDefault()?.Trim();
                    }
                }
                proc.Dispose();
            }
        }
        catch (Exception ex) { }

        return new HardwareInfo(ramMB, vramMB, cpu, diskMB, gpuName);
    }
}

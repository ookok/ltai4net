using System.ComponentModel;
using System.IO.Compression;
using System.Formats.Tar;
using LTAI.AI;
using LTAI.Core;

namespace LTAI.Agent.Tools;

[ToolDomain("file")]
public sealed class ArchiveTools
{
    private readonly string _ws;
    public ArchiveTools(string ws) => _ws = ws;

    [Description("创建压缩包。支持 zip、tar.gz 格式。\n"
        + "适用场景：打包多个文件/目录、压缩日志、归档项目文件。\n"
        + "关键参数：outputPath — 输出路径（如 archive.zip）；sourcePaths — 要打包的文件/目录列表。")]
    public string ArchiveCreate(string outputPath, string[] sourcePaths, string format = "zip")
    {
        var fp = PathUtils.SafeResolvePath(_ws, outputPath);
        if (fp == null) return "Error: path escape";
        Directory.CreateDirectory(Path.GetDirectoryName(fp)!);

        try
        {
            var resolved = sourcePaths.Select(p => PathUtils.SafeResolvePath(_ws, p)).ToArray();
            if (resolved.Any(p => p == null))
                return "Error: one or more source paths escaped workspace";

            switch (format.ToLowerInvariant())
            {
                case "zip":
                    {
                        using var stream = File.Create(fp);
                        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
                        foreach (var src in resolved!)
                        {
                            if (File.Exists(src))
                                archive.CreateEntryFromFile(src, Path.GetFileName(src));
                            else if (Directory.Exists(src))
                                AddDirToZip(archive, src, Path.GetFileName(src));
                        }
                        break;
                    }
                case "tar.gz":
                case "tgz":
                    {
                        using var fileStream = File.Create(fp);
                        using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
                        using var tarWriter = new TarWriter(gzipStream);
                        foreach (var src in resolved!)
                        {
                            if (File.Exists(src))
                            {
                                var entry = new PaxTarEntry(TarEntryType.RegularFile, Path.GetFileName(src));
                                var fs = File.OpenRead(src);
                                entry.DataStream = fs;
                                tarWriter.WriteEntry(entry);
                                fs.Dispose();
                            }
                            else if (Directory.Exists(src))
                            {
                                foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                                {
                                    var rel = Path.GetRelativePath(src, file);
                                    var entry = new PaxTarEntry(TarEntryType.RegularFile, rel);
                                    var dfs = File.OpenRead(file);
                                    entry.DataStream = dfs;
                                    tarWriter.WriteEntry(entry);
                                    dfs.Dispose();
                                }
                            }
                        }
                        break;
                    }
                default:
                    return $"Unsupported format: {format}. Supported: zip, tar.gz";
            }

            return $"Created archive: {fp} ({new FileInfo(fp).Length} bytes)";
        }
        catch (Exception ex)
        {
            return $"Error creating archive: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [Description("解压压缩包。支持 zip、tar.gz、tgz 格式。\n"
        + "适用场景：解压下载的压缩包、提取归档文件。\n"
        + "关键参数：archivePath — 压缩包路径；outputDir — 解压到目标目录。")]
    public string ArchiveExtract(string archivePath, string outputDir)
    {
        var fp = PathUtils.SafeResolvePath(_ws, archivePath);
        var outDir = PathUtils.SafeResolvePath(_ws, outputDir);
        if (fp == null) return "Error: archive path escape";
        if (outDir == null) return "Error: output directory path escape";
        if (!File.Exists(fp)) return $"Archive not found: {fp}";

        try
        {
            Directory.CreateDirectory(outDir);
            var name = Path.GetFileName(fp).ToLowerInvariant();

            if (name.EndsWith(".zip"))
            {
                ZipFile.ExtractToDirectory(fp, outDir, overwriteFiles: true);
            }
            else if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz"))
            {
                using var fileStream = File.OpenRead(fp);
                using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                using var tarReader = new TarReader(gzipStream);
                while (tarReader.GetNextEntry() is { } entry)
                {
                    var dest = Path.GetFullPath(Path.Combine(outDir, entry.Name));
                    if (!dest.StartsWith(Path.GetFullPath(outDir)))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }
            else
            {
                return $"Unsupported archive format. Supported: .zip, .tar.gz, .tgz";
            }

            var count = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Length;
            return $"Extracted {count} files to {outDir}";
        }
        catch (Exception ex)
        {
            return $"Error extracting archive: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void AddDirToZip(ZipArchive archive, string dir, string entryPrefix)
    {
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, file);
            var entryName = string.IsNullOrEmpty(entryPrefix) ? rel : $"{entryPrefix}/{rel}";
            archive.CreateEntryFromFile(file, entryName);
        }
    }
}

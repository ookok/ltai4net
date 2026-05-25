using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Infra.Sandbox;

public sealed class DockerSandbox : ISandbox
{
    private readonly ILogger<DockerSandbox> _logger;
    private readonly DockerClient _docker;
    private readonly string _imagePrefix = "ltaisb-";

    private static readonly Dictionary<SandboxLanguage, string> Images = new()
    {
        [SandboxLanguage.Python] = "python:3.12-slim",
        [SandboxLanguage.JavaScript] = "node:20-slim",
        [SandboxLanguage.Shell] = "alpine:latest",
        [SandboxLanguage.CSharp] = "mcr.microsoft.com/dotnet/sdk:10.0"
    };

    public string Name => "DockerSandbox";
    public SandboxCapability Capability => SandboxCapability.All & ~SandboxCapability.CSharp;

    public DockerSandbox(ILogger<DockerSandbox> logger)
    {
        _logger = logger;
        _docker = new DockerClientConfiguration().CreateClient();
    }

    public async Task<SandboxResult> ExecuteAsync(SandboxRequest request, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var containerId = $"{_imagePrefix}{Guid.NewGuid():N}";

        try
        {
            var image = Images.GetValueOrDefault(request.Language, "python:3.12-slim");
            var containerConfig = CreateContainerConfig(request, image);

            var createResp = await _docker.Containers.CreateContainerAsync(
                new CreateContainerParameters
                {
                    Image = image,
                    Cmd = GetCmd(request),
                    HostConfig = new HostConfig
                    {
                        Memory = (long)request.MemoryLimitMb * 1024 * 1024,
                        MemorySwap = (long)request.MemoryLimitMb * 1024 * 1024 * 2,
                        NetworkMode = request.NetworkEnabled ? "bridge" : "none",
                        ReadonlyRootfs = request.ReadOnlyFilesystem,
                        AutoRemove = true
                    },
                    AttachStdout = true,
                    AttachStderr = true,
                    AttachStdin = !string.IsNullOrEmpty(request.Stdin),
                    Name = containerId
                }, cancellationToken);

            await _docker.Containers.StartContainerAsync(createResp.ID, null, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await _docker.Containers.WaitContainerAsync(createResp.ID, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { await _docker.Containers.KillContainerAsync(createResp.ID, new ContainerKillParameters(), cancellationToken); } catch { /* non-fatal */ }
            }

            var logStream = await _docker.Containers.GetContainerLogsAsync(createResp.ID, false, new ContainerLogsParameters
            {
                ShowStdout = true, ShowStderr = true
            }, cancellationToken).ConfigureAwait(false);

            var (stdout, stderr) = await ReadMultiplexedStreamAsync(logStream, cancellationToken).ConfigureAwait(false);

            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _docker.Containers.RemoveContainerAsync(createResp.ID, new ContainerRemoveParameters { Force = true }, cleanupCts.Token); }
            catch { /* non-fatal */ }

            sw.Stop();
            return new SandboxResult
            {
                Success = string.IsNullOrEmpty(stderr),
                Stdout = stdout, Stderr = stderr,
                ExecutionTimeMs = sw.ElapsedMilliseconds,
                Error = string.IsNullOrEmpty(stderr) ? null : stderr
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Docker sandbox failed");
            return new SandboxResult { Success = false, Error = ex.Message, ExecutionTimeMs = sw.ElapsedMilliseconds };
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _docker.System.PingAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static CreateContainerParameters CreateContainerConfig(SandboxRequest request, string image) => new()
    {
        Image = image
    };

    private static string[] GetCmd(SandboxRequest request) => request.Language switch
    {
        SandboxLanguage.Python => new[] { "python", "-c", request.Code },
        SandboxLanguage.JavaScript => new[] { "node", "-e", request.Code },
        SandboxLanguage.Shell => new[] { "sh", "-c", request.Code },
        _ => new[] { "python", "-c", request.Code }
    };

    private static async Task<(string stdout, string stderr)> ReadMultiplexedStreamAsync(
        MultiplexedStream stream, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var header = new byte[8];

        while (true)
        {
            var headerBytesRead = 0;
            while (headerBytesRead < 8)
            {
                var read = await stream.ReadOutputAsync(header, headerBytesRead, 8 - headerBytesRead, ct).ConfigureAwait(false);
                if (read.EOF) return (stdout.ToString(), stderr.ToString());
                headerBytesRead += read.Count;
            }

            var streamType = header[0];
            var frameSize = (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
            if (frameSize == 0) continue;

            var buffer = new byte[Math.Min(frameSize, 65536)];
            var remaining = frameSize;
            while (remaining > 0)
            {
                var read = await stream.ReadOutputAsync(buffer, 0, Math.Min(buffer.Length, remaining), ct).ConfigureAwait(false);
                if (read.EOF) break;
                var text = Encoding.UTF8.GetString(buffer, 0, read.Count);
                if (streamType == 1)
                    stdout.Append(text);
                else if (streamType == 2)
                    stderr.Append(text);
                remaining -= read.Count;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _docker.Dispose();
        return ValueTask.CompletedTask;
    }
}

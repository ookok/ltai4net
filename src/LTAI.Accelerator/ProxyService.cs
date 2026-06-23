using LTAI.Core.Configuration;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LTAI.Accelerator;

public sealed class ProxyService : IDisposable
{
    private readonly int _port;
    private readonly DnsOverHttps _dns;
    private readonly WarpService _warp;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _running;

    public int Port => _port;
    public bool IsRunning => _running;
    public DnsOverHttps Dns => _dns;
    public WarpService Warp => _warp;
    /// <summary>Non-null when warp is available; await for connection result.</summary>
    public Task<bool>? WarpConnectTask { get; private set; }

    public ProxyService(int port)
    {
        _port = port;
        _dns = new DnsOverHttps();
        _warp = new WarpService();
    }

    public async Task StartAsync()
    {
        if (_running) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _running = true;

        // Try Warp in background (don't block proxy startup)
        if (_warp.Available)
        {
            WarpConnectTask = _warp.ConnectAsync();
            _ = WarpConnectTask;
        }

        _ = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (!_running) return;
        _running = false;
        _cts?.Cancel();
        _listener?.Stop();

        if (_warp.Connected)
            await _warp.DisconnectAsync();
    }

    private static readonly SemaphoreSlim s_handlerSemaphore = new(
        Math.Max(1, EnvironmentConfig.ProxyMaxConn));

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                var captured = client;
                _ = Task.Run(async () =>
                {
                    if (!await s_handlerSemaphore.WaitAsync(TimeSpan.FromSeconds(30)))
                    { captured.Dispose(); return; }
                    try
                    {
                        await HandleClientAsync(captured, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { System.Diagnostics.Debug.WriteLine($"[ProxyService] Client handler failed: {ex.Message}"); }
                    finally { s_handlerSemaphore.Release(); }
                });
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ProxyService] Accept loop error: {ex.Message}"); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (bytesRead == 0) return;

                var request = System.Text.Encoding.ASCII.GetString(buffer, 0, bytesRead);
                var lines = request.Split('\r', '\n');
                if (lines.Length < 1) return;

                var requestLine = lines[0];
                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;

                var method = parts[0].ToUpperInvariant();
                var target = parts[1];

                if (method == "CONNECT")
                    await HandleConnectAsync(stream, buffer, bytesRead, target, ct);
                else
                    await HandleHttpAsync(stream, buffer, bytesRead, method, target, ct);
            }
        }
        catch when (ct.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ProxyService] HandleClient error: {ex.Message}"); }
    }

    private async Task HandleConnectAsync(NetworkStream clientStream, byte[] buffer, int bytesRead, string target, CancellationToken ct)
    {
        var hostPort = target.Split(':');
        var host = hostPort[0];
        var port = hostPort.Length > 1 && int.TryParse(hostPort[1], out var p) ? p : 443;

        try
        {
            using var remote = await ConnectToTargetAsync(host, port, ct);
            remote.ReceiveTimeout = 60000;
            remote.SendTimeout = 60000;
            var remoteStream = remote.GetStream();

            var response = "HTTP/1.1 200 Connection Established\r\n\r\n";
            await clientStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response).AsMemory(), ct);

            var t1 = RelayAsync(clientStream, remoteStream, ct);
            var t2 = RelayAsync(remoteStream, clientStream, ct);
            await Task.WhenAny(t1, t2);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProxyService] Connect failed for '{target}': {ex.Message}");
            try
            {
                var error = "HTTP/1.1 502 Bad Gateway\r\n\r\nProxy error";
                await clientStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(error).AsMemory(), ct);
            }
            catch
            {
                // non-critical, best-effort
            }
        }
    }

    private async Task HandleHttpAsync(NetworkStream clientStream, byte[] buffer, int bytesRead, string method, string target, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(target);
            using var remote = await ConnectToTargetAsync(uri.Host, uri.Port, ct);
            remote.ReceiveTimeout = 30000;
            remote.SendTimeout = 30000;
            var remoteStream = remote.GetStream();

            var newRequest = System.Text.Encoding.ASCII.GetString(buffer, 0, bytesRead)
                .Replace(target, uri.PathAndQuery);

            await remoteStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(newRequest).AsMemory(), ct);

            var t1 = RelayAsync(clientStream, remoteStream, ct);
            var t2 = RelayAsync(remoteStream, clientStream, ct);
            await Task.WhenAny(t1, t2);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProxyService] HTTP proxy failed for '{target}': {ex.Message}");
            try
            {
                var error = "HTTP/1.1 502 Bad Gateway\r\n\r\nProxy error";
                await clientStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(error).AsMemory(), ct);
            }
            catch
            {
                // non-critical, best-effort
            }
        }
    }

    private static bool IsPrivateIP(System.Net.IPAddress ip)
    {
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        byte[] b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 0) return true;
        }
        else if (b.Length == 16)
        {
            if (b[10] == 0xff && b[11] == 0xff)
            {
                if (b[12] == 10 || b[12] == 127) return true;
                if (b[12] == 169 && b[13] == 254) return true;
                if (b[12] == 0) return true;
            }
            if ((b[0] & 0xfe) == 0xfc) return true;
            if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;
        }
        return false;
    }

    private async Task<TcpClient> ConnectToTargetAsync(string host, int port, CancellationToken ct)
    {
        // Block raw IP connections to private/internal addresses
        if (System.Net.IPAddress.TryParse(host, out var rawIp) && IsPrivateIP(rawIp))
            throw new InvalidOperationException("Connection to private/internal IP blocked");

        // Warp SOCKS5 upstream takes priority
        if (_warp.Connected)
            return await Socks5ConnectAsync("127.0.0.1", 40000, host, port, ct);

        // DoH resolution
        var addresses = await _dns.ResolveAsync(host, ct);
        if (addresses.Length > 0)
        {
            // SSRF protection: reject private IPs from DNS resolution
            if (addresses.Any(IsPrivateIP))
                throw new InvalidOperationException("Target resolves to private/internal IP");
            var remote = new TcpClient();
            await remote.ConnectAsync(addresses[0], port, ct);
            return remote;
        }

        // Fallback: system DNS
        var sysAddresses = await System.Net.Dns.GetHostAddressesAsync(host, ct);
        if (sysAddresses.Length == 0) throw new Exception("Cannot resolve host");
        if (sysAddresses.Any(IsPrivateIP))
            throw new InvalidOperationException("Target resolves to private/internal IP");
        var sysRemote = new TcpClient();
        await sysRemote.ConnectAsync(sysAddresses[0], port, ct);
        return sysRemote;
    }

    private static async Task<TcpClient> Socks5ConnectAsync(string proxyHost, int proxyPort, string targetHost, int targetPort, CancellationToken ct)
    {
        var socks = new TcpClient();
        await socks.ConnectAsync(proxyHost, proxyPort, ct);
        var s = socks.GetStream();

        // Handshake: no auth
        s.Write([0x05, 0x01, 0x00]);
        var resp = new byte[2];
        await s.ReadExactlyAsync(resp.AsMemory(), ct);

        // CONNECT
        var addr = System.Text.Encoding.ASCII.GetBytes(targetHost);
        using var ms = new MemoryStream();
        ms.WriteByte(0x05); ms.WriteByte(0x01); ms.WriteByte(0x00); ms.WriteByte(0x03);
        ms.WriteByte((byte)addr.Length);
        ms.Write(addr);
        ms.WriteByte((byte)(targetPort >> 8));
        ms.WriteByte((byte)targetPort);
        s.Write(ms.ToArray());

        var reply = new byte[10];
        await s.ReadExactlyAsync(reply.AsMemory(), ct);
        if (reply[1] != 0x00)
            throw new Exception($"SOCKS5 failed: code {reply[1]}");

        return socks;
    }

    private static async Task RelayAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        var buffer = new byte[65536];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await from.ReadAsync(buffer.AsMemory(), ct);
                if (read == 0) break;
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch when (ct.IsCancellationRequested) { }
        catch
        {
            // non-critical, best-effort
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _warp.Dispose();
        _dns.Dispose();
        _cts?.Dispose();
        (_listener as IDisposable)?.Dispose();
    }
}

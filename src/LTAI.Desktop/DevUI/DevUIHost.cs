// Copyright (c) LTAI. All rights reserved.

using System.Diagnostics;
using LTAI.Agent.DevUI;
using LTAI.Core.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Desktop.DevUI;

/// <summary>
/// Hosts the LTAI.Web DevUI surface (MAF DevUI HTML/JS) on a free loopback
/// port inside the Desktop process and launches the user's default browser
/// pointed at <c>/devui</c>. Avoids the WebView2 package dependency that an
/// embedded control would require on Avalonia 12; relies on the OS browser
/// for full DevUI feature parity (DevTools, copy/paste, multi-tab, etc.).
/// P9.2: lightweight Desktop DevUI surface; P9.3+ can swap in a WebView2
/// <see cref="Avalonia.Controls.NativeControlHost"/> if in-process embedding
/// is required.
/// </summary>
public sealed class DevUIHost : IAsyncDisposable
{
    private WebApplication? _app;
    private int _port;
    public int Port => _port;
    public string? BaseUrl => _app is null ? null : $"http://localhost:{_port}";

    public async Task StartAsync(IServiceProvider parentSp, CancellationToken ct = default)
    {
        if (_app is not null) return;

        var port = GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new DevUIForwardingLoggerProvider(
            parentSp.GetService<ILoggerProvider>() ?? new NullLoggerProvider()));
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(parentSp);
        builder.Services.AddRouting();
        var app = builder.Build();

        app.MapGet("/v1/entities", (IServiceProvider sp) =>
        {
            var devUi = sp.GetRequiredService<LTAIDevUIService>();
            return Results.Ok(devUi.ListAgentCards());
        });
        app.MapGet("/v1/entities/{name}/card", (IServiceProvider sp, string name) =>
        {
            var devUi = sp.GetRequiredService<LTAIDevUIService>();
            var card = devUi.GetAgentCard(name);
            return card is null ? Results.NotFound() : Results.Ok(card);
        });
        app.MapGet("/", () => Results.Redirect("/devui"));
        app.MapGet("/devui", () => Results.Content(SimpleDevUIHtml, "text/html"));

        await app.StartAsync(ct);
        _app = app;
        _port = port;
    }

    public void OpenInBrowser()
    {
        if (_app is null) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = BaseUrl + "/devui",
            UseShellExecute = true,
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            try { await _app.StopAsync(); } catch { /* shutdown best-effort */ }
            try { await _app.DisposeAsync(); } catch { /* dispose best-effort */ }
            _app = null;
        }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }

    private const string SimpleDevUIHtml = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>LTAI DevUI (Desktop-launched)</title>
  <style>
    body { font-family: -apple-system, "Segoe UI", sans-serif; margin: 24px; background: #0d1117; color: #c9d1d9; }
    h1 { color: #58a6ff; }
    a, a:visited { color: #58a6ff; text-decoration: none; }
    a:hover { text-decoration: underline; }
    .card { background: #161b22; border: 1px solid #30363d; border-radius: 8px; padding: 16px; margin: 8px 0; }
    .meta { color: #8b949e; font-size: 12px; }
    .pill { display: inline-block; background: #21262d; color: #c9d1d9; padding: 2px 8px; border-radius: 12px; margin: 2px; font-size: 11px; }
    .perm-r { background: #0e4429; color: #56d364; }
    .perm-w { background: #6c3909; color: #f0883e; }
    .perm-l { background: #0c2d6b; color: #58a6ff; }
    .perm-x { background: #6e1010; color: #ff7b72; }
  </style>
</head>
<body>
  <h1>🧠 LTAI DevUI</h1>
  <p class="meta">Hosted by LTAI.Desktop (P9.2 in-process Kestrel). Click an agent to inspect.</p>
  <div id="root">Loading…</div>
  <script>
    fetch('/v1/entities').then(r => r.json()).then(cards => {
      const root = document.getElementById('root');
      root.innerHTML = '';
      cards.sort((a, b) => a.name.localeCompare(b.name));
      for (const c of cards) {
        const el = document.createElement('div');
        el.className = 'card';
        const perms = (c.permissions || []).map(p => {
          const cls = p === 'read' ? 'perm-r' : p === 'write' ? 'perm-w' : p === 'list' ? 'perm-l' : p === 'exec' ? 'perm-x' : '';
          return `<span class="pill ${cls}">${p}</span>`;
        }).join('');
        const tools = (c.tools || []).map(t => `<span class="pill">${t}</span>`).join('');
        el.innerHTML = `
          <h3>${c.name}</h3>
          <div class="meta">model: ${c.modelId || '—'} · T=${c.temperature} · tools=${c.toolCount} · v${c.version}</div>
          <p>${c.description}</p>
          <div>${perms}</div>
          <div style="margin-top:8px">${tools}</div>
        `;
        root.appendChild(el);
      }
    });
  </script>
</body>
</html>
""";
}

internal sealed class DevUIForwardingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerProvider _inner;
    public DevUIForwardingLoggerProvider(ILoggerProvider inner) { _inner = inner; }
    public ILogger CreateLogger(string categoryName) => _inner.CreateLogger(categoryName);
    public void Dispose() { /* forward to inner */ }
}

internal sealed class NullLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new NullLogger();
    public void Dispose() { }
}

internal sealed class NullLogger : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

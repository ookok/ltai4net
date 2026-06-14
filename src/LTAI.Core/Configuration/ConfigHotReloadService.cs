// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ConfigHotReloadService — watch appsettings.json & hot-reload
//
//  Problem: Changing appsettings.json requires app restart.
//  Users must reconfigure API keys, providers, and model settings.
//
//  Solution: FileSystemWatcher on appsettings.json → re-read
//  config → notify IOptionsMonitor subscribers → refresh
//  MultiProviderChatClient registrations.
//
//  Trigger sequence:
//    1. File changed → debounce 500ms
//    2. Reload IConfigurationRoot (re-read appsettings.json)
//    3. Notify IOptionsMonitor<LTAIOptions> (triggers OnChange)
//    4. Fire ConfigReloaded event for manual consumers
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Core.Configuration;

/// <summary>
/// Background service that watches appsettings.json for changes
/// and triggers configuration hot-reload without app restart.
/// </summary>
public sealed class ConfigHotReloadService : BackgroundService
{
    private readonly IConfigurationRoot _configRoot;
    private readonly IOptionsMonitor<LTAIOptions> _optionsMonitor;
    private readonly ILogger<ConfigHotReloadService> _logger;
    private FileSystemWatcher? _watcher;
    private IDisposable? _optionsChangeToken;
    private DateTime _lastChange = DateTime.MinValue;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Fired after config is successfully reloaded.
    /// Services listening to this event can re-read configuration.
    /// </summary>
    public event Action<LTAIOptions>? ConfigReloaded;

    /// <summary>The current config file path being watched.</summary>
    public string? ConfigFilePath { get; private set; }

    public ConfigHotReloadService(
        IConfigurationRoot configRoot,
        IOptionsMonitor<LTAIOptions> optionsMonitor,
        ILogger<ConfigHotReloadService> logger)
    {
        _configRoot = configRoot ?? throw new ArgumentNullException(nameof(configRoot));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Find the config file path
        var configPath = LocateConfigFile();
        if (configPath == null)
        {
            _logger.LogWarning("ConfigHotReloadService: no config file found to watch");
            return Task.CompletedTask;
        }

        ConfigFilePath = configPath;
        var dir = Path.GetDirectoryName(configPath)!;
        var file = Path.GetFileName(configPath);

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnConfigChanged;
        _watcher.Created += OnConfigChanged;

        // Subscribe to IOptionsMonitor's built-in change notification
        _optionsChangeToken = _optionsMonitor.OnChange(OnOptionsChanged);

        _logger.LogInformation("ConfigHotReloadService: watching '{ConfigPath}'", configPath);
        return Task.CompletedTask;
    }

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastChange) < DebounceDelay)
            return; // debounce duplicate events (FileSystemWatcher fires 2× per save)
        _lastChange = now;

        // Wait briefly for file to be available (locked by editor)
        Thread.Sleep(200);

        try
        {
            // Re-read the config file into IConfigurationRoot
            _configRoot.Reload();

            // IOptionsMonitor detects the reload automatically via ChangeToken
            // and fires OnChange triggers.
            _logger.LogInformation("ConfigHotReloadService: config reloaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ConfigHotReloadService: failed to reload config");
        }
    }

    private void OnOptionsChanged(LTAIOptions newOptions)
    {
        _logger.LogInformation("ConfigHotReloadService: configuration changed — " +
            "DefaultProvider={Provider}, Model={Model}",
            newOptions.AI.DefaultProvider, newOptions.AI.Model);

        // Fire custom event for services not using IOptionsMonitor
        ConfigReloaded?.Invoke(newOptions);
    }

    private string? LocateConfigFile()
    {
        // Try common config file paths
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                try
                {
                    // Verify it's readable
                    using var fs = File.OpenRead(path);
                    return path;
                }
                catch { continue; }
            }
        }

        return null;
    }

    public override void Dispose()
    {
        _optionsChangeToken?.Dispose();
        _watcher?.Dispose();
        base.Dispose();
    }
}

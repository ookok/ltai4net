using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.Infra.Network.Bridge;

public enum ChannelType
{
    Web,
    Weixin,
    Feishu,
    Dingtalk,
    WecomBot,
    Qq,
    Terminal
}

public sealed record ChannelConfig
{
    public ChannelType Channel { get; init; }
    public bool Enabled { get; init; }
    public string? BotToken { get; init; }
    public string? AppId { get; init; }
    public string? AppSecret { get; init; }
    public string? WebhookUrl { get; init; }
    public string? ProxyUrl { get; init; }
}

public sealed record ChannelMessage
{
    public ChannelType Channel { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string? GroupId { get; init; }
    public string Text { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public sealed class ChannelBridge
{
    private static readonly Lazy<ChannelBridge> _instance = new(() => new ChannelBridge());
    public static ChannelBridge Instance => _instance.Value;

    private readonly ConcurrentDictionary<ChannelType, ChannelConfig> _channels = new();
    private readonly ConcurrentDictionary<ChannelType, Func<ChannelMessage, Task<string>>> _handlers = new();
    private readonly List<ChannelMessage> _messageLog = new();
    private readonly object _logLock = new();
    private readonly ILogger<ChannelBridge>? _logger;

    private const int MaxLogSize = 5000;
    private const string ConfigFilePath = ".livingtree/channel_bridge.json";

    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public ChannelBridge() { }

    public ChannelBridge(ILogger<ChannelBridge> logger)
    {
        _logger = logger;
    }

    public void Configure(ChannelType channel, ChannelConfig config)
    {
        _channels[channel] = config;
        _logger?.LogInformation("Channel {Channel} configured (Enabled={Enabled})", channel, config.Enabled);
        _saveConfig();
    }

    public void RegisterHandler(ChannelType channel, Func<ChannelMessage, Task<string>> handler)
    {
        _handlers[channel] = handler;
        _logger?.LogInformation("Handler registered for channel {Channel}", channel);
    }

    public async Task<string> OnMessage(ChannelMessage message)
    {
        _logMessage(message);
        _autoProfileFeed(message);
        return await Dispatch(message).ConfigureAwait(false);
    }

    public async Task<string> Dispatch(ChannelMessage message)
    {
        if (_handlers.TryGetValue(message.Channel, out var handler))
        {
            try
            {
                var reply = await handler(message).ConfigureAwait(false);
                _logger?.LogDebug("Message handled on {Channel} from {UserId}", message.Channel, message.UserId);
                return reply;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Handler error on channel {Channel}", message.Channel);
                return string.Empty;
            }
        }

        _logger?.LogWarning("No handler registered for channel {Channel}", message.Channel);
        return string.Empty;
    }

    public void Send(ChannelType channel, string userId, string text)
    {
        _logger?.LogInformation("Send to {Channel}/{UserId}: {Text}", channel, userId, text);
    }

    public void Start()
    {
        var priorityOrder = new[] { ChannelType.WecomBot, ChannelType.Feishu, ChannelType.Dingtalk, ChannelType.Terminal };

        foreach (var channelType in priorityOrder)
        {
            if (_channels.TryGetValue(channelType, out var config) && config.Enabled)
            {
                _logger?.LogInformation("Channel {Channel} started (auto-priority)", channelType);
            }
        }

        foreach (var kvp in _channels)
        {
            if (!priorityOrder.Contains(kvp.Key) && kvp.Value.Enabled)
            {
                _logger?.LogInformation("Channel {Channel} started", kvp.Key);
            }
        }

        _isRunning = true;
        _logger?.LogInformation("ChannelBridge started");
    }

    public void Stop()
    {
        foreach (var kvp in _channels)
        {
            if (kvp.Value.Enabled)
            {
                _logger?.LogInformation("Channel {Channel} stopped", kvp.Key);
            }
        }

        _isRunning = false;
        _logger?.LogInformation("ChannelBridge stopped");
    }

    public List<ChannelConfig> GetActiveChannels()
    {
        return _channels.Values
            .Where(c => c.Enabled && !string.IsNullOrEmpty(c.BotToken ?? c.AppId ?? c.WebhookUrl))
            .ToList();
    }

    public Dictionary<ChannelType, int> GetStats()
    {
        var stats = new Dictionary<ChannelType, int>();
        lock (_logLock)
        {
            foreach (var group in _messageLog.GroupBy(m => m.Channel))
                stats[group.Key] = group.Count();
        }
        return stats;
    }

    private void _autoProfileFeed(ChannelMessage message)
    {
        _logger?.LogTrace("Auto-profile feed for user {UserId} on {Channel}", message.UserId, message.Channel);
    }

    private void _logMessage(ChannelMessage message)
    {
        lock (_logLock)
        {
            _messageLog.Add(message);
            if (_messageLog.Count > MaxLogSize)
                _messageLog.RemoveRange(0, _messageLog.Count - MaxLogSize);
        }
    }

    private void _saveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var configs = _channels.Values.ToList();
            var json = JsonSerializer.Serialize(configs, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFilePath, json);
            _logger?.LogDebug("Channel config saved to {Path}", ConfigFilePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save channel config");
        }
    }

    private void _loadConfig()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return;

            var json = File.ReadAllText(ConfigFilePath);
            var configs = JsonSerializer.Deserialize<List<ChannelConfig>>(json);
            if (configs != null)
            {
                foreach (var config in configs)
                    _channels[config.Channel] = config;
                _logger?.LogInformation("Channel config loaded from {Path}", ConfigFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load channel config");
        }
    }
}

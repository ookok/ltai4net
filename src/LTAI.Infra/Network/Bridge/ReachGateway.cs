using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LTAI.Infra.Network.Bridge;

public enum DeviceType
{
    PC,
    Mobile,
    Tablet,
    Unknown
}

public enum SensorType
{
    CameraPhoto,
    CameraScan,
    QrCode,
    GpsLocation,
    Microphone,
    NfcTag,
    Accelerometer,
    Compass,
    Biometric,
    Screenshot,
    Clipboard,
    TouchSignature
}

public enum TaskPriority
{
    Critical,
    High,
    Normal,
    Low
}

public enum PairMethod
{
    Qr,
    Code8Digit,
    LanAuto,
    Audio,
    Ble,
    Manual
}

public enum TrustLevel
{
    Observer = 0,
    Sensor = 1,
    Operator = 2,
    Manager = 3
}

public sealed record DeviceInfo
{
    public string DeviceId { get; init; } = string.Empty;
    public DeviceType DeviceType { get; init; }
    public string DeviceName { get; init; } = string.Empty;
    public List<string> Capabilities { get; init; } = new();
    public DateTime? PairedAt { get; init; }
    public TrustLevel TrustLevel { get; init; } = TrustLevel.Observer;
    public int MeshHops { get; init; }
    public string? RelayedBy { get; init; }
    public bool IsConnected { get; init; }
}

public sealed record SensorRequest
{
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public SensorType SensorType { get; init; }
    public string Instruction { get; init; } = string.Empty;
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;
    public int TimeoutMs { get; init; } = 30000;
    public bool Required { get; init; }
    public string? Context { get; init; }
    public string? Hints { get; init; }
}

public sealed record TaskCard
{
    public Guid CardId { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = string.Empty;
    public string ActionType { get; init; } = string.Empty;
    public List<string> Choices { get; init; } = new();
    public string CallbackEvent { get; init; } = string.Empty;
    public int ExpiresInSeconds { get; init; } = 60;
}

public sealed record PairedDevice
{
    public string DeviceId { get; init; } = string.Empty;
    public DeviceType DeviceType { get; init; }
    public DateTime PairedAt { get; init; }
    public TrustLevel TrustLevel { get; init; }
    public List<string> Capabilities { get; init; } = new();
    public int MeshHops { get; init; }
    public string? RelayedBy { get; init; }
}

public sealed record PairingCodeEntry
{
    public string DeviceId { get; init; } = string.Empty;
    public DateTime Expires { get; init; }
    public int Attempts { get; init; }
}

public sealed class ReachGateway
{
    private static readonly Lazy<ReachGateway> _instance = new(() => new ReachGateway());
    public static ReachGateway Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, PairingCodeEntry> _pairingCodes = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _meshGraph = new();
    private readonly ILogger<ReachGateway>? _logger;

    private const int _codeLength = 6;
    private static readonly TimeSpan _codeTtl = TimeSpan.FromSeconds(300);
    private const int _maxAttempts = 5;

    public ReachGateway() { }

    public ReachGateway(ILogger<ReachGateway> logger)
    {
        _logger = logger;
    }

    public string GeneratePairingCode(string deviceId)
    {
        var code = new StringBuilder();
        var random = RandomNumberGenerator.Create();
        Span<byte> bytes = stackalloc byte[4];
        for (int i = 0; i < _codeLength; i++)
        {
            random.GetBytes(bytes);
            code.Append((BitConverter.ToUInt32(bytes) % 10).ToString());
        }

        var codeStr = code.ToString();
        _pairingCodes[codeStr] = new PairingCodeEntry
        {
            DeviceId = deviceId,
            Expires = DateTime.UtcNow.Add(_codeTtl),
            Attempts = 0
        };

        _logger?.LogDebug("Pairing code generated for {DeviceId}: {Code}", deviceId, codeStr);
        return codeStr;
    }

    public bool VerifyCode(string code, string deviceId)
    {
        if (!_pairingCodes.TryGetValue(code, out var entry))
            return false;

        if (DateTime.UtcNow > entry.Expires)
        {
            _pairingCodes.TryRemove(code, out _);
            return false;
        }

        if (entry.Attempts >= _maxAttempts)
        {
            _pairingCodes.TryRemove(code, out _);
            _logger?.LogWarning("Max attempts exceeded for code {Code}", code);
            return false;
        }

        _pairingCodes.TryUpdate(code, entry with { Attempts = entry.Attempts + 1 }, entry);

        if (entry.DeviceId == deviceId)
        {
            _pairingCodes.TryRemove(code, out _);
            return true;
        }

        return false;
    }

    public string GenerateQrUrl(string code)
    {
        return $"https://livingtree.ai/pair?code={code}";
    }

    public void RegisterDevice(DeviceInfo deviceInfo)
    {
        _detectCapabilities(deviceInfo);
        _devices.AddOrUpdate(deviceInfo.DeviceId, deviceInfo, (_, __) => deviceInfo);
        _logger?.LogInformation("Device registered: {DeviceId} ({DeviceType})", deviceInfo.DeviceId, deviceInfo.DeviceType);
    }

    public void UnregisterDevice(string deviceId)
    {
        _devices.TryRemove(deviceId, out _);
        _logger?.LogInformation("Device unregistered: {DeviceId}", deviceId);
    }

    public List<DeviceInfo> GetDevices(DeviceType? deviceType = null)
    {
        var query = _devices.Values.AsEnumerable();
        if (deviceType.HasValue)
            query = query.Where(d => d.DeviceType == deviceType.Value);
        return query.ToList();
    }

    public List<DeviceInfo> GetMobileDevices()
    {
        return _devices.Values
            .Where(d => d.DeviceType is DeviceType.Mobile or DeviceType.Tablet)
            .ToList();
    }

    public bool HasMobile()
    {
        return _devices.Values.Any(d => d.DeviceType is DeviceType.Mobile or DeviceType.Tablet);
    }

    public PairedDevice PairDevice(string deviceId, PairMethod method, TrustLevel trustLevel)
    {
        if (!_devices.TryGetValue(deviceId, out var device))
        {
            device = new DeviceInfo
            {
                DeviceId = deviceId,
                DeviceType = DeviceType.Unknown,
                DeviceName = deviceId,
                Capabilities = _guessCapabilities(deviceId, DeviceType.Unknown)
            };
            _devices[deviceId] = device;
        }

        var pairedAt = DateTime.UtcNow;
        var updatedDevice = device with { PairedAt = pairedAt, TrustLevel = trustLevel, IsConnected = true };
        _devices[deviceId] = updatedDevice;

        _logger?.LogInformation("Device paired: {DeviceId} via {Method} with trust {TrustLevel}", deviceId, method, trustLevel);

        return new PairedDevice
        {
            DeviceId = deviceId,
            DeviceType = device.DeviceType,
            PairedAt = pairedAt,
            TrustLevel = trustLevel,
            Capabilities = device.Capabilities,
            MeshHops = device.MeshHops,
            RelayedBy = device.RelayedBy
        };
    }

    public void PromoteTrust(string deviceId)
    {
        if (_devices.TryGetValue(deviceId, out var device) && device.TrustLevel < TrustLevel.Manager)
        {
            _devices[deviceId] = device with { TrustLevel = device.TrustLevel + 1 };
            _logger?.LogInformation("Trust promoted for {DeviceId}: {TrustLevel}", deviceId, device.TrustLevel + 1);
        }
    }

    public void DemoteTrust(string deviceId)
    {
        if (_devices.TryGetValue(deviceId, out var device) && device.TrustLevel > TrustLevel.Observer)
        {
            _devices[deviceId] = device with { TrustLevel = device.TrustLevel - 1 };
            _logger?.LogInformation("Trust demoted for {DeviceId}: {TrustLevel}", deviceId, device.TrustLevel - 1);
        }
    }

    public bool CanDo(string deviceId, string capability)
    {
        return _devices.TryGetValue(deviceId, out var device)
            && device.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<string?> RequestSensor(string deviceId, SensorRequest request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = request.RequestId.ToString("N");
        _pendingRequests[requestId] = tcs;

        _logger?.LogInformation("Sensor request {RequestId} to {DeviceId}: {SensorType}", requestId, deviceId, request.SensorType);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(request.TimeoutMs);

        try
        {
            cts.Token.Register(() => tcs.TrySetResult(null));
            var result = await tcs.Task;
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    public TaskCard PushTaskCard(string deviceId, TaskCard taskCard)
    {
        _logger?.LogInformation(
            "Task card {CardId} pushed to {DeviceId}: {Title} ({ActionType})",
            taskCard.CardId, deviceId, taskCard.Title, taskCard.ActionType);
        return taskCard;
    }

    public void ReceiveResponse(string requestId, string? response)
    {
        if (_pendingRequests.TryGetValue(requestId, out var tcs))
        {
            tcs.TrySetResult(response);
            _logger?.LogDebug("Response received for request {RequestId}", requestId);
        }
        else
        {
            _logger?.LogWarning("Response received for unknown request {RequestId}", requestId);
        }
    }

    public bool RelayMessage(string fromDevice, string toDevice, string message)
    {
        if (!_meshGraph.TryGetValue(fromDevice, out var _))
            _meshGraph[fromDevice] = new ConcurrentDictionary<string, int>();

        if (!_meshGraph[fromDevice].ContainsKey(toDevice))
            _meshGraph[fromDevice][toDevice] = 1;

        var path = _findPath(fromDevice, toDevice);
        if (path.Count == 0)
        {
            _logger?.LogWarning("No mesh path from {FromDevice} to {ToDevice}", fromDevice, toDevice);
            return false;
        }

        _logger?.LogDebug("Relaying message from {FromDevice} to {ToDevice} via {Hops} hops", fromDevice, toDevice, path.Count);
        return true;
    }

    public List<string> GetReachableDevices(string deviceId)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        var reachable = new List<string>();

        queue.Enqueue(deviceId);
        visited.Add(deviceId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (_meshGraph.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors.Keys)
                {
                    if (visited.Add(neighbor))
                    {
                        reachable.Add(neighbor);
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }

        return reachable;
    }

    public PairedDevice CreateEphemeralPairing(string deviceId, string capability)
    {
        _logger?.LogInformation("Ephemeral pairing for {DeviceId} with capability {Capability}", deviceId, capability);
        return new PairedDevice
        {
            DeviceId = deviceId,
            DeviceType = _devices.TryGetValue(deviceId, out var d) ? d.DeviceType : DeviceType.Unknown,
            PairedAt = DateTime.UtcNow,
            TrustLevel = TrustLevel.Sensor,
            Capabilities = new List<string> { capability },
            MeshHops = 0,
            RelayedBy = null
        };
    }

    private void _detectCapabilities(DeviceInfo deviceInfo)
    {
        var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cap in deviceInfo.Capabilities)
            caps.Add(cap);

        foreach (var guessed in _guessCapabilities(deviceInfo.DeviceId, deviceInfo.DeviceType))
            caps.Add(guessed);

        var name = deviceInfo.DeviceName.ToLowerInvariant();

        if (name.Contains("pixel") || name.Contains("iphone") || name.Contains("samsung"))
        {
            caps.Add("camera_photo");
            caps.Add("camera_scan");
            caps.Add("qr_code");
        }

        if (name.Contains("watch") || name.Contains("band"))
            caps.Add("biometric");

        if (name.Contains("glass") || name.Contains("ar"))
        {
            caps.Add("camera_photo");
            caps.Add("camera_scan");
        }

        var updated = deviceInfo with { Capabilities = caps.ToList() };
        _devices[deviceInfo.DeviceId] = updated;
    }

    private static List<string> _guessCapabilities(string deviceId, DeviceType deviceType)
    {
        return deviceType switch
        {
            DeviceType.Mobile => new List<string>
            {
                "camera_photo", "camera_scan", "qr_code", "gps_location",
                "microphone", "nfc_tag", "accelerometer", "compass",
                "biometric", "touch_signature", "clipboard"
            },
            DeviceType.Tablet => new List<string>
            {
                "camera_photo", "camera_scan", "qr_code", "gps_location",
                "microphone", "accelerometer", "compass",
                "touch_signature", "clipboard"
            },
            DeviceType.PC => new List<string>
            {
                "screenshot", "clipboard", "microphone"
            },
            _ => new List<string>()
        };
    }

    public List<PairedDevice> GetPairedDevices()
    {
        return _devices.Values
            .Where(d => d.PairedAt.HasValue)
            .Select(d => new PairedDevice
            {
                DeviceId = d.DeviceId,
                DeviceType = d.DeviceType,
                PairedAt = d.PairedAt!.Value,
                TrustLevel = d.TrustLevel,
                Capabilities = d.Capabilities,
                MeshHops = d.MeshHops,
                RelayedBy = d.RelayedBy
            })
            .ToList();
    }

    public (int deviceCount, int mobileCount, int pairedCount, int pendingRequests) Stats()
    {
        return (
            _devices.Count,
            _devices.Values.Count(d => d.DeviceType is DeviceType.Mobile or DeviceType.Tablet),
            _devices.Values.Count(d => d.PairedAt.HasValue),
            _pendingRequests.Count
        );
    }

    private List<string> _findPath(string from, string to)
    {
        if (from == to)
            return new List<string>();

        var visited = new HashSet<string>();
        var prev = new Dictionary<string, string>();
        var queue = new Queue<string>();

        queue.Enqueue(from);
        visited.Add(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (_meshGraph.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors.Keys)
                {
                    if (visited.Add(neighbor))
                    {
                        prev[neighbor] = current;
                        if (neighbor == to)
                        {
                            var path = new List<string>();
                            var node = to;
                            while (node != from)
                            {
                                path.Add(node);
                                node = prev[node];
                            }
                            path.Reverse();
                            return path;
                        }
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }

        return new List<string>();
    }

    private static string ComputeSha256(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

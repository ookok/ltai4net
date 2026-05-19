using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LTAI.Network.Links;

public sealed record EncryptedMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SenderId { get; init; } = string.Empty;
    public string ReceiverId { get; init; } = string.Empty;
    public byte[] Payload { get; init; } = [];
    public string Signature { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string MessageType { get; init; } = string.Empty;
    public Dictionary<string, string> Metadata { get; init; } = new();

    public static EncryptedMessage Create(
        string senderId,
        string receiverId,
        byte[] payload,
        string sharedSecret,
        string messageType)
    {
        var signature = ComputeHmac(payload, sharedSecret);
        return new EncryptedMessage
        {
            SenderId = senderId,
            ReceiverId = receiverId,
            Payload = payload,
            Signature = signature,
            MessageType = messageType
        };
    }

    public bool Verify(string sharedSecret)
    {
        var expected = ComputeHmac(Payload, sharedSecret);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(Signature));
    }

    private static string ComputeHmac(byte[] data, string secret)
    {
        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class EncryptedChannel
{
    private readonly string _nodeId;
    private readonly string _sharedSecret;
    private readonly int _maxMessageSize;
    private readonly List<EncryptedMessage> _history;
    private readonly object _historyLock = new();
    private readonly ILogger<EncryptedChannel> _logger;
    private const int MaxHistory = 10000;

    public EncryptedChannel(
        string nodeId,
        string sharedSecret,
        ILogger<EncryptedChannel> logger,
        int maxMessageSize = 10 * 1024 * 1024)
    {
        _nodeId = nodeId;
        _sharedSecret = sharedSecret;
        _logger = logger;
        _maxMessageSize = maxMessageSize;
        _history = new List<EncryptedMessage>(MaxHistory);
    }

    public EncryptedMessage? Encrypt(byte[] payload, string receiverId, string messageType)
    {
        if (payload.Length > _maxMessageSize)
        {
            _logger.LogWarning("Payload exceeds max message size: {Size} > {Max}", payload.Length, _maxMessageSize);
            return null;
        }

        var xorKey = DeriveKey(_sharedSecret);
        var encryptedPayload = XorWithKey(payload, xorKey);

        var message = EncryptedMessage.Create(_nodeId, receiverId, encryptedPayload, _sharedSecret, messageType);
        _logger.LogDebug("Message encrypted: {Id} -> {Receiver}", message.Id, receiverId);
        return message;
    }

    public byte[]? Decrypt(EncryptedMessage message, string senderId)
    {
        if (!message.Verify(_sharedSecret))
        {
            _logger.LogWarning("HMAC verification failed for message {Id} from {Sender}", message.Id, senderId);
            return null;
        }

        var xorKey = DeriveKey(_sharedSecret);
        var plaintext = XorWithKey(message.Payload, xorKey);

        _logger.LogDebug("Message decrypted: {Id} from {Sender}", message.Id, senderId);
        return plaintext;
    }

    public EncryptedMessage? Send(string receiverId, byte[] payload, string messageType)
    {
        var message = Encrypt(payload, receiverId, messageType);
        if (message is null)
            return null;

        Store(message);
        _logger.LogInformation("Message sent: {Id} -> {Receiver}", message.Id, receiverId);
        return message;
    }

    public byte[]? Receive(EncryptedMessage message)
    {
        Store(message);
        return Decrypt(message, message.SenderId);
    }

    public List<EncryptedMessage> Broadcast(List<string> receiverIds, byte[] payload, string messageType)
    {
        var messages = new List<EncryptedMessage>(receiverIds.Count);

        for (var i = 0; i < receiverIds.Count; i++)
        {
            var message = Encrypt(payload, receiverIds[i], messageType);
            if (message is not null)
            {
                Store(message);
                messages.Add(message);
            }
        }

        _logger.LogInformation("Broadcast sent to {Count} receivers", messages.Count);
        return messages;
    }

    public IReadOnlyList<EncryptedMessage> GetHistory(string? senderId = null, string? receiverId = null)
    {
        lock (_historyLock)
        {
            return _history
                .Where(m =>
                    (senderId is null || m.SenderId == senderId) &&
                    (receiverId is null || m.ReceiverId == receiverId))
                .ToList();
        }
    }

    public IReadOnlyList<EncryptedMessage> GetMessagesByType(string messageType)
    {
        lock (_historyLock)
        {
            return _history
                .Where(m => m.MessageType == messageType)
                .ToList();
        }
    }

    private static byte[] XorWithKey(byte[] data, byte[] key)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ key[i % key.Length]);
        }

        return result;
    }

    private static byte[] DeriveKey(string sharedSecret)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret));
    }

    private void Store(EncryptedMessage message)
    {
        lock (_historyLock)
        {
            if (_history.Count >= MaxHistory)
                _history.RemoveAt(0);

            _history.Add(message);
        }
    }
}

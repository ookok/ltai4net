using System.Text;

namespace LTAI.Network.Links;

public enum FrameMessageType : byte
{
    DiscoveryAnnounce = 0x01,
    CellShare = 0x02,
    KnowledgeSync = 0x03,
    TaskDistribute = 0x04,
    TaskResponse = 0x05,
    HealthReport = 0x06
}

public sealed record BinaryFrame
{
    public FrameMessageType FrameType { get; init; }
    public byte[] Payload { get; init; } = [];
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public static class MessageBusBinary
{
    public static readonly byte[] FrameMagic = [0x4C, 0x54, 0x01];

    public static byte[] Encode(BinaryFrame frame)
    {
        var payloadLen = BitConverter.GetBytes(frame.Payload.Length);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(payloadLen);

        var result = new byte[3 + 1 + 4 + frame.Payload.Length];
        Array.Copy(FrameMagic, 0, result, 0, 3);
        result[3] = (byte)frame.FrameType;
        Array.Copy(payloadLen, 0, result, 4, 4);
        Array.Copy(frame.Payload, 0, result, 8, frame.Payload.Length);

        return result;
    }

    public static BinaryFrame? Decode(byte[] bytes)
    {
        if (bytes.Length < 8)
            return null;

        if (bytes[0] != FrameMagic[0] || bytes[1] != FrameMagic[1] || bytes[2] != FrameMagic[2])
            return null;

        var frameType = (FrameMessageType)bytes[3];

        var payloadLenBytes = new byte[4];
        Array.Copy(bytes, 4, payloadLenBytes, 0, 4);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(payloadLenBytes);

        var payloadLen = BitConverter.ToInt32(payloadLenBytes, 0);

        if (bytes.Length < 8 + payloadLen)
            return null;

        var payload = new byte[payloadLen];
        Array.Copy(bytes, 8, payload, 0, payloadLen);

        return new BinaryFrame
        {
            FrameType = frameType,
            Payload = payload
        };
    }

    public static FrameMessageType? IdentifyFrame(byte[] bytes)
    {
        if (bytes.Length < 4)
            return null;

        return (FrameMessageType)bytes[3];
    }

    public static byte[] EncodeDiscoveryAnnounce(string peerId, string address, int port)
    {
        var payload = Encoding.UTF8.GetBytes($"{peerId}|{address}|{port}");
        var frame = new BinaryFrame
        {
            FrameType = FrameMessageType.DiscoveryAnnounce,
            Payload = payload
        };
        return Encode(frame);
    }

    public static (string peerId, string address, int port) DecodeDiscoveryAnnounce(byte[] data)
    {
        var frame = Decode(data);
        if (frame is null)
            return (string.Empty, string.Empty, 0);

        var text = Encoding.UTF8.GetString(frame.Payload);
        var parts = text.Split('|');
        if (parts.Length != 3)
            return (string.Empty, string.Empty, 0);

        return (parts[0], parts[1], int.TryParse(parts[2], out var port) ? port : 0);
    }

    public static byte[] EncodeCellShare(string cellId, byte[] genomeData)
    {
        var idBytes = Encoding.UTF8.GetBytes(cellId + "|");
        var payload = new byte[idBytes.Length + genomeData.Length];
        Array.Copy(idBytes, 0, payload, 0, idBytes.Length);
        Array.Copy(genomeData, 0, payload, idBytes.Length, genomeData.Length);

        var frame = new BinaryFrame
        {
            FrameType = FrameMessageType.CellShare,
            Payload = payload
        };
        return Encode(frame);
    }

    public static byte[] EncodeKnowledgeSync(string knowledgeJson)
    {
        var frame = new BinaryFrame
        {
            FrameType = FrameMessageType.KnowledgeSync,
            Payload = Encoding.UTF8.GetBytes(knowledgeJson)
        };
        return Encode(frame);
    }

    public static byte[] EncodeTaskDistribute(string taskId, string taskJson)
    {
        var combined = $"{taskId}|{taskJson}";
        var frame = new BinaryFrame
        {
            FrameType = FrameMessageType.TaskDistribute,
            Payload = Encoding.UTF8.GetBytes(combined)
        };
        return Encode(frame);
    }

    public static byte[] EncodeTaskResponse(string taskId, string resultJson)
    {
        var combined = $"{taskId}|{resultJson}";
        var frame = new BinaryFrame
        {
            FrameType = FrameMessageType.TaskResponse,
            Payload = Encoding.UTF8.GetBytes(combined)
        };
        return Encode(frame);
    }

    public static byte[] EncodeHealthReport(string nodeId, byte[] data)
    {
        var idBytes = Encoding.UTF8.GetBytes(nodeId + "|");
        var payload = new byte[idBytes.Length + data.Length];
        Array.Copy(idBytes, 0, payload, 0, idBytes.Length);
        Array.Copy(data, 0, payload, idBytes.Length, data.Length);

        var frame = new BinaryFrame
        {
            FrameType = FrameMessageType.HealthReport,
            Payload = payload
        };
        return Encode(frame);
    }

    public static byte[] WrapPayload(FrameMessageType type, byte[] payload)
    {
        var frame = new BinaryFrame
        {
            FrameType = type,
            Payload = payload
        };
        return Encode(frame);
    }

    public static int GetFrameStats(FrameMessageType type)
    {
        return 3 + 1 + 4;
    }
}

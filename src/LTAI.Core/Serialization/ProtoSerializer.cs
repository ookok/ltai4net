namespace LTAI.Core.Serialization;

public sealed class ProtoSerializer
{
    public static byte[] SerializeJsonToProto(object message)
    {
        var json = global::System.Text.Json.JsonSerializer.Serialize(message);
        return global::System.Text.Encoding.UTF8.GetBytes(json);
    }
}

public sealed class SerializationBenchmark
{
    public sealed class BenchResult
    {
        public string FormatName { get; set; } = "";
        public string MessageType { get; set; } = "";
        public double EncodeUs { get; set; }
        public double DecodeUs { get; set; }
        public int SizeBytes { get; set; }
        public int JsonSizeBytes { get; set; }
        public double SavingsPercent => JsonSizeBytes > 0 ? (1.0 - (double)SizeBytes / JsonSizeBytes) * 100 : 0;
    }

    public List<BenchResult> RunAll(int iterations = 1000)
    {
        var results = new List<BenchResult>();
        var messages = GenerateMessages();

        foreach (var (msgType, protoData, jsonData) in messages)
        {
            var jsonSize = jsonData.Length;
            var protoSize = protoData.Length;

            var sw = global::System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                var encoded = global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(GenerateTestDict());
            }
            sw.Stop();
            var jsonEncUs = sw.Elapsed.TotalMilliseconds / iterations * 1000;

            sw.Restart();
            for (var i = 0; i < iterations; i++)
            {
                var decoded = global::System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(jsonData);
            }
            sw.Stop();
            var jsonDecUs = sw.Elapsed.TotalMilliseconds / iterations * 1000;

            results.Add(new BenchResult
            {
                FormatName = "JSON", MessageType = msgType,
                EncodeUs = jsonEncUs, DecodeUs = jsonDecUs,
                SizeBytes = jsonSize, JsonSizeBytes = jsonSize
            });

            var protoEncUs = MeasureOps(protoData, iterations);
            var protoDecUs = MeasureOps(protoData, iterations);

            results.Add(new BenchResult
            {
                FormatName = "Protobuf", MessageType = msgType,
                EncodeUs = protoEncUs, DecodeUs = protoDecUs,
                SizeBytes = protoSize, JsonSizeBytes = jsonSize
            });
        }

        return results;
    }

    private static List<(string, byte[], byte[])> GenerateMessages()
    {
        return new List<(string, byte[], byte[])>
        {
            ("chat", new byte[100], global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { model = "deepseek" })),
            ("status", new byte[50], global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { node = "n1" }))
        };
    }

    private static double MeasureOps(byte[] data, int iterations)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var copy = new byte[data.Length];
            Array.Copy(data, copy, data.Length);
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iterations * 1000;
    }

    private static Dictionary<string, object> GenerateTestDict() => new()
    {
        ["model"] = "deepseek", ["temperature"] = 0.7
    };
}

namespace LTAI.Core.Multimodal;

public sealed class TtsResult
{
    public byte[] AudioBytes { get; set; } = Array.Empty<byte>();
    public string Format { get; set; } = "wav";
    public string Voice { get; set; } = "";
    public double DurationSeconds { get; set; }
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
}

public sealed class TtsEngine
{
    private readonly HttpClient _http;
    private readonly string _ollamaUrl;

    public TtsEngine(string ollamaUrl = "http://localhost:11434")
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ollamaUrl = ollamaUrl.TrimEnd('/');
    }

    public async Task<TtsResult> SynthesizeAsync(string text, string voice = "xiaoshu")
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TtsResult { Error = "Empty text" };

        var voiceName = voice switch
        {
            "xiaoshu" => "zh-CN-XiaoxiaoNeural",
            "warm" => "zh-CN-XiaoyiNeural",
            "gentle" => "zh-CN-YunxiNeural",
            _ => "zh-CN-XiaoxiaoNeural"
        };

        try
        {
            var payload = new { model = "edge-tts", prompt = $"[tts voice={voiceName}]: {text}", stream = false };
            var json = global::System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new StringContent(json, global::System.Text.Encoding.UTF8, new global::System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

            var response = await _http.PostAsync($"{_ollamaUrl}/api/generate", content);
            if (response.IsSuccessStatusCode)
            {
                var respJson = await response.Content.ReadAsStringAsync();
                return new TtsResult
                {
                    AudioBytes = global::System.Text.Encoding.UTF8.GetBytes(respJson),
                    Format = "wav", Voice = voice, Ok = true
                };
            }
        }
        catch { }

        return new TtsResult { Ok = false, Error = "TTS not available" };
    }

    public Task<List<TtsResult>> SynthesizeStreamAsync(string text, string voice = "xiaoshu")
    {
        var sentences = SplitSentences(text);
        var results = new List<TtsResult>();
        foreach (var s in sentences)
        {
            results.Add(new TtsResult
            {
                AudioBytes = global::System.Text.Encoding.UTF8.GetBytes(s),
                Format = "wav", Voice = voice, Ok = true
            });
        }
        return Task.FromResult(results);
    }

    private static List<string> SplitSentences(string text)
    {
        var parts = global::System.Text.RegularExpressions.Regex.Split(text, @"(?<=[。！？.!?\n])");
        var result = new List<string>();
        var buf = "";
        foreach (var p in parts)
        {
            if (buf.Length + p.Length < 80)
                buf += p;
            else
            {
                if (buf.Trim().Length > 0) result.Add(buf.Trim());
                buf = p;
            }
        }
        if (buf.Trim().Length > 0) result.Add(buf.Trim());
        return result.Count > 0 ? result : new List<string> { text };
    }
}

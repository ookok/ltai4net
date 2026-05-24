using System.Text;
using System.Text.Json;

namespace LTAI.Core.Multimodal;

public sealed class TtsResult
{
    public byte[] AudioBytes { get; set; } = Array.Empty<byte>();
    public string Format { get; set; } = "wav";
    public string Voice { get; set; } = "";
    public double DurationSeconds { get; set; }
    public int SampleRate { get; set; } = 44100;
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
}

public sealed class TtsSynthesisOptions
{
    public string? Voice { get; init; }
    public string? Lang { get; init; }
    public int TotalSteps { get; init; } = 8;
    public float Speed { get; init; } = 1.05f;
    public string Format { get; init; } = "wav";
    public static TtsSynthesisOptions Default => new();
}

public sealed class VoiceInfo
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? Language { get; init; }
}

public interface ITtsEngine
{
    Task<TtsResult> SynthesizeAsync(string text, TtsSynthesisOptions? options = null, CancellationToken ct = default);
    Task<VoiceInfo[]> GetVoicesAsync(CancellationToken ct = default);
    string EngineName { get; }
    bool IsAvailable { get; }
}

public sealed class TtsEngine : ITtsEngine
{
    private readonly HttpClient _http;
    private readonly string _ollamaUrl;

    public string EngineName => "Ollama Edge-TTS";
    public bool IsAvailable => true;

    public TtsEngine(string ollamaUrl = "http://localhost:11434")
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ollamaUrl = ollamaUrl.TrimEnd('/');
    }

    public Task<TtsResult> SynthesizeAsync(string text, TtsSynthesisOptions? options = null, CancellationToken ct = default)
    {
        var voice = options?.Voice ?? "xiaoshu";
        return SynthesizeInternalAsync(text, voice);
    }

    private async Task<TtsResult> SynthesizeInternalAsync(string text, string voice)
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
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, new global::System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

            var response = await _http.PostAsync($"{_ollamaUrl}/api/generate", content);
            if (response.IsSuccessStatusCode)
            {
                var respJson = await response.Content.ReadAsStringAsync();
                return new TtsResult
                {
                    AudioBytes = Encoding.UTF8.GetBytes(respJson),
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
                AudioBytes = Encoding.UTF8.GetBytes(s),
                Format = "wav", Voice = voice, Ok = true
            });
        }
        return Task.FromResult(results);
    }

    public async Task<VoiceInfo[]> GetVoicesAsync(CancellationToken ct = default)
    {
        return await Task.FromResult(new[]
        {
            new VoiceInfo { Name = "xiaoshu", Description = "晓晓 - 中文女声 (Neural)" },
            new VoiceInfo { Name = "warm", Description = "晓依 - 温暖女声 (Neural)" },
            new VoiceInfo { Name = "gentle", Description = "云希 - 温和男声 (Neural)" }
        });
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

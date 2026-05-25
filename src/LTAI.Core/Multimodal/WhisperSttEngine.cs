using System.Text.Json;

namespace LTAI.Core.Multimodal;

public sealed class WhisperTranscribeResult
{
    public bool Ok { get; set; }
    public string Text { get; set; } = "";
    public string Language { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string Error { get; set; } = "";
}

public sealed class WhisperSttEngine : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _ollamaUrl;
    private readonly string _sttModel;

    public WhisperSttEngine(string ollamaUrl = "http://localhost:11434", string sttModel = "whisper:latest")
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _ollamaUrl = ollamaUrl.TrimEnd('/');
        _sttModel = sttModel;
    }

    public void Dispose() { _http?.Dispose(); }

    public string OllamaUrl => _ollamaUrl;
    public string SttModel => _sttModel;

    public async Task<WhisperTranscribeResult> TranscribeAsync(byte[] audioBytes, string format = "wav")
    {
        if (audioBytes == null || audioBytes.Length < 500)
            return new WhisperTranscribeResult { Error = "Audio too short" };

        try
        {
            var audioB64 = Convert.ToBase64String(audioBytes);
            var payload = new
            {
                model = _sttModel,
                prompt = $"Transcribe this audio to text: [base64:{audioB64}]",
                stream = false,
                options = new { temperature = 0.0 }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, global::System.Text.Encoding.UTF8, new global::System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

            var response = await _http.PostAsync($"{_ollamaUrl}/api/generate", content);
            if (response.IsSuccessStatusCode)
            {
                var respJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(respJson);
                var text = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString()?.Trim() ?? "" : "";

                if (!string.IsNullOrEmpty(text))
                    return new WhisperTranscribeResult { Ok = true, Text = text, Language = DetectLanguage(text) };
            }
        }
        catch { /* non-fatal */ }

        return new WhisperTranscribeResult { Error = "Whisper model not available. Run: ollama pull whisper" };
    }

    public async Task<bool> HealthCheckAsync()
    {
        try
        {
            var response = await _http.GetAsync($"{_ollamaUrl}/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static string DetectLanguage(string text)
    {
        var chineseCount = text.Count(c => c >= 0x4e00 && c <= 0x9fff);
        return chineseCount > text.Length * 0.3 ? "zh" : "en";
    }
}

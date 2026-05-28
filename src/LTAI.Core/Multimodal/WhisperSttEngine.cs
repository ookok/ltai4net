using System.Net.Http.Headers;
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

    private static readonly Dictionary<string, string> FormatMimeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wav"] = "audio/wav",
        ["mp3"] = "audio/mpeg",
        ["m4a"] = "audio/mp4",
        ["ogg"] = "audio/ogg",
        ["flac"] = "audio/flac",
        ["webm"] = "audio/webm",
    };

    public WhisperSttEngine(string ollamaUrl = "http://localhost:11434", string sttModel = "whisper:latest")
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
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
            var mimeType = FormatMimeMap.GetValueOrDefault(format, "audio/wav");
            var fileName = $"audio.{format.TrimStart('.')}";

            // Attempt 1: Ollama /api/transcribe (multipart — preferred for v0.3+)
            using var formContent = new MultipartFormDataContent();
            var audioContent = new ByteArrayContent(audioBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            formContent.Add(audioContent, "file", fileName);
            formContent.Add(new StringContent(_sttModel), "model");

            var response = await _http.PostAsync($"{_ollamaUrl}/api/transcribe", formContent);
            if (response.IsSuccessStatusCode)
            {
                var respJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(respJson);
                var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";
                var language = doc.RootElement.TryGetProperty("language", out var l) ? l.GetString() ?? "" : "";

                if (!string.IsNullOrEmpty(text))
                {
                    return new WhisperTranscribeResult
                    {
                        Ok = true,
                        Text = text,
                        Language = language,
                        DurationSeconds = doc.RootElement.TryGetProperty("duration", out var d) ? d.GetDouble() : 0
                    };
                }
            }

            // Attempt 2: OpenAI-compatible /v1/audio/transcriptions
            using var openAiForm = new MultipartFormDataContent();
            var openAiAudio = new ByteArrayContent(audioBytes);
            openAiAudio.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            openAiForm.Add(openAiAudio, "file", fileName);
            openAiForm.Add(new StringContent("whisper-1"), "model");

            var openAiResponse = await _http.PostAsync($"{_ollamaUrl}/v1/audio/transcriptions", openAiForm);
            if (openAiResponse.IsSuccessStatusCode)
            {
                var respJson = await openAiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(respJson);
                var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";

                if (!string.IsNullOrEmpty(text))
                    return new WhisperTranscribeResult { Ok = true, Text = text, Language = DetectLanguage(text) };
            }
        }
        catch { /* non-fatal — fall through to error result */ }

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

using System.Text;
using System.Text.Json;
using LTAI.Core.Multimodal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Infra.Multimodal;

public record SupertonicTtsConfig
{
    public string ServerUrl { get; init; } = "http://127.0.0.1:7788";
    public string DefaultVoice { get; init; } = "M1";
    public string Model { get; init; } = "supertonic-3";
    public int TotalSteps { get; init; } = 8;
    public float Speed { get; init; } = 1.05f;
    public string OnnxDir { get; init; } = "assets/onnx";
    public string VoiceStyleDir { get; init; } = "assets/voice_styles";
    public int TimeoutSeconds { get; init; } = 30;
}

public sealed class SupertonicTtsEngine : ITtsEngine, IDisposable
{
    private readonly SupertonicTtsConfig _config;
    private readonly ILogger<SupertonicTtsEngine> _logger;
    private readonly HttpClient _http;
    private bool _checkedAvailability;
    private bool _isAvailable;

    public string EngineName => "Supertonic";
    public bool IsAvailable
    {
        get
        {
            if (!_checkedAvailability)
            {
                _checkedAvailability = true;
                try
                {
                    _isAvailable = CheckAvailabilityAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    _isAvailable = false;
                }
            }
            return _isAvailable;
        }
    }

    public SupertonicTtsEngine(SupertonicTtsConfig config, ILogger<SupertonicTtsEngine>? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger<SupertonicTtsEngine>.Instance;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };
    }

    public async Task<bool> CheckAvailabilityAsync()
    {
        try
        {
            var response = await _http.GetAsync($"{_config.ServerUrl}/docs", HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Supertonic server not available at {Url}: {Message}", _config.ServerUrl, ex.Message);
            return false;
        }
    }

    public async Task<TtsResult> SynthesizeAsync(string text, TtsSynthesisOptions? options = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TtsResult { Error = "Empty text" };

        options ??= TtsSynthesisOptions.Default;

        try
        {
            var requestBody = new Dictionary<string, object?>
            {
                ["model"] = _config.Model,
                ["input"] = text,
                ["voice"] = options.Voice ?? _config.DefaultVoice,
                ["response_format"] = options.Format,
                ["speed"] = options.Speed
            };

            if (options.TotalSteps > 0)
                requestBody["total_steps"] = options.TotalSteps;
            else
                requestBody["total_steps"] = _config.TotalSteps;

            if (!string.IsNullOrEmpty(options.Lang))
                requestBody["lang"] = options.Lang;

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

            var response = await _http.PostAsync(
                $"{_config.ServerUrl}/v1/audio/speech",
                content,
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogError("Supertonic TTS failed: HTTP {Status} - {Error}", (int)response.StatusCode, errorBody);
                return new TtsResult
                {
                    Ok = false,
                    Error = $"Supertonic server error: {response.StatusCode}",
                    Voice = options.Voice ?? _config.DefaultVoice
                };
            }

            var audioBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var duration = EstimateDurationSeconds(audioBytes);

            _logger.LogInformation(
                "Supertonic TTS synthesized {Bytes} bytes, ~{Duration:F1}s, voice={Voice}, lang={Lang}",
                audioBytes.Length, duration, options.Voice, options.Lang);

            return new TtsResult
            {
                AudioBytes = audioBytes,
                Format = options.Format,
                Voice = options.Voice ?? _config.DefaultVoice,
                DurationSeconds = duration,
                SampleRate = 44100,
                Ok = true
            };
        }
        catch (TaskCanceledException)
        {
            return new TtsResult { Ok = false, Error = "TTS synthesis timed out" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Supertonic TTS synthesis failed");
            return new TtsResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<VoiceInfo[]> GetVoicesAsync(CancellationToken ct = default)
    {
        var styleDir = _config.VoiceStyleDir;
        if (!Directory.Exists(styleDir))
        {
            var altDir = Path.Combine(AppContext.BaseDirectory, "assets", "voice_styles");
            if (Directory.Exists(altDir))
                styleDir = altDir;
            else
                return Array.Empty<VoiceInfo>();
        }

        try
        {
            var files = Directory.GetFiles(styleDir, "*.json");
            var voices = new List<VoiceInfo>();
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                string? description = null;

                try
                {
                    var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("description", out var desc))
                        description = desc.GetString();
                }
                catch { }

                voices.Add(new VoiceInfo { Name = name, Description = description });
            }
            return voices.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list Supertonic voices from {Dir}", styleDir);
            return Array.Empty<VoiceInfo>();
        }
    }

    private static double EstimateDurationSeconds(byte[] wavBytes)
    {
        if (wavBytes.Length < 44) return 0;

        var dataSize = BitConverter.ToInt32(wavBytes, 40);
        var sampleRate = BitConverter.ToInt32(wavBytes, 24);
        var bytesPerSample = BitConverter.ToInt16(wavBytes, 34);
        var numChannels = BitConverter.ToInt16(wavBytes, 22);

        if (sampleRate <= 0 || bytesPerSample <= 0 || numChannels <= 0)
            return 0;

        var actualData = Math.Min(dataSize, wavBytes.Length - 44);
        return (double)actualData / (sampleRate * numChannels * bytesPerSample / 8);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

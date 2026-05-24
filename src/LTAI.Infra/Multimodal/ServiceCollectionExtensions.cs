using LTAI.Core.Multimodal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Infra.Multimodal;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIMultimodal(this IServiceCollection services)
    {
        services.AddSingleton<OCREngine>();
        services.AddSingleton<RapidOCREngine>(sp =>
        {
            var logger = sp.GetService<ILogger<RapidOCREngine>>();
            var modelDir = global::System.IO.Path.Combine(AppContext.BaseDirectory, "models", "rapidocr");
            return new RapidOCREngine(modelDir, logger);
        });
        services.AddSingleton<VisionAnalyzer>();
        services.AddSingleton<SpeechEngine>();
        services.AddSingleton<MultimodalOrchestrator>();
        services.AddHostedService<OCREngineCleanupService>();

        services.AddSingleton<ITtsEngine>(sp =>
        {
            var options = sp.GetService<Microsoft.Extensions.Options.IOptions<LTAI.Core.Configuration.LTAIOptions>>()?.Value;
            var sc = options?.Supertonic;

            var onnxDir = sc?.OnnxDir ?? "assets/onnx";
            var voiceStyleDir = sc?.VoiceStyleDir ?? "assets/voice_styles";

            if (HasOnnxModels(onnxDir))
            {
                var config = new SupertonicOnnxTtsConfig
                {
                    OnnxDir = onnxDir,
                    VoiceStyleDir = voiceStyleDir,
                    DefaultVoice = sc?.DefaultVoice ?? "M1",
                    TotalSteps = sc?.TotalSteps ?? 8,
                    Speed = sc?.Speed ?? 1.05f
                };
                var logger = sp.GetService<ILogger<SupertonicOnnxTtsEngine>>();
                return (ITtsEngine)new SupertonicOnnxTtsEngine(config, logger);
            }

            if (sc is { Enabled: true, ServerUrl: not null })
            {
                var config = new SupertonicTtsConfig
                {
                    ServerUrl = sc.ServerUrl,
                    DefaultVoice = sc.DefaultVoice,
                    Model = sc.Model,
                    TotalSteps = sc.TotalSteps,
                    Speed = sc.Speed,
                    OnnxDir = onnxDir,
                    VoiceStyleDir = voiceStyleDir
                };
                var logger = sp.GetService<ILogger<SupertonicTtsEngine>>();
                return (ITtsEngine)new SupertonicTtsEngine(config, logger);
            }

            return new LTAI.Core.Multimodal.TtsEngine();
        });

        return services;
    }

    private static bool HasOnnxModels(string onnxDir)
    {
        if (!Directory.Exists(onnxDir))
        {
            var alt = Path.Combine(AppContext.BaseDirectory, onnxDir);
            if (Directory.Exists(alt))
                onnxDir = alt;
            else
                return false;
        }

        return File.Exists(Path.Combine(onnxDir, "duration_predictor.onnx"))
            && File.Exists(Path.Combine(onnxDir, "text_encoder.onnx"))
            && File.Exists(Path.Combine(onnxDir, "vector_estimator.onnx"))
            && File.Exists(Path.Combine(onnxDir, "vocoder.onnx"));
    }

    public static IServiceCollection AddLTAISupertonicOnnxTts(
        this IServiceCollection services,
        string? onnxDir = null,
        string? voiceStyleDir = null,
        string? defaultVoice = null)
    {
        var config = new SupertonicOnnxTtsConfig();
        if (!string.IsNullOrEmpty(onnxDir))
            config = config with { OnnxDir = onnxDir };
        if (!string.IsNullOrEmpty(voiceStyleDir))
            config = config with { VoiceStyleDir = voiceStyleDir };
        if (!string.IsNullOrEmpty(defaultVoice))
            config = config with { DefaultVoice = defaultVoice };

        services.AddSingleton(config);
        services.AddSingleton<ITtsEngine>(sp =>
        {
            var cfg = sp.GetRequiredService<SupertonicOnnxTtsConfig>();
            var logger = sp.GetService<ILogger<SupertonicOnnxTtsEngine>>();
            return (ITtsEngine)new SupertonicOnnxTtsEngine(cfg, logger);
        });

        return services;
    }

    internal sealed class OCREngineCleanupService(OCREngine ocr) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) { ocr.Dispose(); return Task.CompletedTask; }
    }
}

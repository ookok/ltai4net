using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Core.Configuration;
using LTAI.Core.Governors;
using LTAI.Core.Setup;
using Microsoft.Extensions.Options;

namespace LTAI.Desktop;

public sealed class LLMConfigView : UserControl
{
    private readonly LTAIOptions _opts;
    private readonly IProviderRegistry _providerReg;
    private readonly ComboBox _l0ProviderBox;
    private readonly TextBox _l0ModelBox;
    private readonly TextBox _l0ApiKeyBox;
    private readonly Button _l0TestBtn;
    private readonly TextBlock _l0TestResult;
    private readonly Button _l0SrvBtn;
    private readonly ComboBox _l1ProviderBox;
    private readonly TextBox _l1ModelBox;
    private readonly TextBox _l1TempBox;
    private readonly TextBox _l1ApiKeyBox;
    private readonly Button _l1TestBtn;
    private readonly TextBlock _l1TestResult;
    private readonly Button _l1SrvBtn;
    private readonly ComboBox _l2ProviderBox;
    private readonly TextBox _l2ModelBox;
    private readonly TextBox _l2TempBox;
    private readonly TextBox _l2ApiKeyBox;
    private readonly Button _l2TestBtn;
    private readonly TextBlock _l2TestResult;
    private readonly Button _l2SrvBtn;
    private readonly TextBox _maxTokensBox;
    private readonly TextBlock _statusText;

    private static readonly string[] LocalOnlyProviders = ["ollama", "lmstudio", "vllm", "llamacpp", "open_webui"];

    private static readonly Dictionary<string, string> EnvVarFor = new()
    {
        ["deepseek"] = "DEEPSEEK_API_KEY", ["openai"] = "OPENAI_API_KEY", ["anthropic"] = "ANTHROPIC_API_KEY",
        ["gemini"] = "GEMINI_API_KEY", ["siliconflow"] = "SILICONFLOW_API_KEY", ["aliyun"] = "DASHSCOPE_API_KEY",
        ["zhipu"] = "ZHIPU_API_KEY", ["hunyuan"] = "HUNYUAN_API_KEY", ["baidu"] = "BAIDU_API_KEY",
        ["spark"] = "SPARK_API_KEY", ["mofang"] = "MOFANG_API_KEY", ["nvidia"] = "NVIDIA_API_KEY",
        ["bailing"] = "BAILING_API_KEY", ["stepfun"] = "STEPFUN_API_KEY", ["internlm"] = "INTERNLM_API_KEY",
        ["sensetime"] = "SENSETIME_API_KEY", ["modelscope"] = "MODELSCOPE_API_KEY", ["openrouter"] = "OPENROUTER_API_KEY",
        ["xiaomi"] = "XIAOMI_API_KEY", ["longcat"] = "LONGCAT_API_KEY", ["dmxapi"] = "DMXAPI_API_KEY",
        ["volcengine"] = "VOLCENGINE_API_KEY", ["moonshot"] = "MOONSHOT_API_KEY", ["minimax"] = "MINIMAX_API_KEY",
        ["groq"] = "GROQ_API_KEY", ["kiro"] = "KIRO_API_KEY", ["opencode"] = "OPENCODE_API_KEY",
    };

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public LLMConfigView(LTAIService svc)
    {
        _opts = ServiceLocator.Get<IOptions<LTAIOptions>>().Value;
        _providerReg = ServiceLocator.Get<IProviderRegistry>();
        var ai = _opts.AI;

        Background = LtaiTheme.Sbb(LtaiTheme.Bg);
        var allProviders = new List<string> { "local" };
        allProviders.AddRange(_providerReg.AllProviders.OrderBy(p => p));

        var root = new StackPanel { Spacing = 8, Margin = new(16) };

        root.Children.Add(new TextBlock
        {
            Text = "LLM Configuration",
            FontSize = 20, FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });
        root.Children.Add(Separator());

        (_l0ProviderBox, _l0TestBtn, _l0TestResult, _l0SrvBtn) = LayerSectionFull(root, "L0 Embedding", allProviders, ai.L0.Provider, ai.L0.Model);
        _l0ProviderBox.SelectionChanged += (_, _) => OnProviderChanged(Layer.L0);
        _l0ModelBox = ModelInput(root, ai.L0.Model, "e.g. bge-large-zh-v1.5");
        _l0ApiKeyBox = ApiKeyBox(root, "API Key (L0):", ai.L0.Provider, "L0_API_KEY");
        _l0TestBtn.Click += async (_, _) => await TestConnection(Layer.L0);
        _l0SrvBtn.Click += async (_, _) => await ToggleLocalServer(Layer.L0);
        root.Children.Add(Separator());

        (_l1ProviderBox, _l1TestBtn, _l1TestResult, _l1SrvBtn) = LayerSectionFull(root, "L1 Fast", allProviders, ai.L1.Provider, ai.L1.Model);
        _l1ProviderBox.SelectionChanged += (_, _) => OnProviderChanged(Layer.L1);
        _l1ModelBox = ModelInput(root, ai.L1.Model, "e.g. deepseek-v4-flash");
        _l1TempBox = TempInput(root, ai.L1.Temperature, "0.0-2.0");
        _l1ApiKeyBox = ApiKeyBox(root, "API Key (L1):", ai.L1.Provider, "L1_API_KEY");
        _l1TestBtn.Click += async (_, _) => await TestConnection(Layer.L1);
        _l1SrvBtn.Click += async (_, _) => await ToggleLocalServer(Layer.L1);
        root.Children.Add(Separator());

        (_l2ProviderBox, _l2TestBtn, _l2TestResult, _l2SrvBtn) = LayerSectionFull(root, "L2 Deep", allProviders, ai.L2.Provider, ai.L2.Model);
        _l2ProviderBox.SelectionChanged += (_, _) => OnProviderChanged(Layer.L2);
        _l2ModelBox = ModelInput(root, ai.L2.Model, "e.g. deepseek-v4-pro");
        _l2TempBox = TempInput(root, ai.L2.Temperature, "0.0-2.0");
        _l2ApiKeyBox = ApiKeyBox(root, "API Key (L2):", ai.L2.Provider, "L2_API_KEY");
        _l2TestBtn.Click += async (_, _) => await TestConnection(Layer.L2);
        _l2SrvBtn.Click += async (_, _) => await ToggleLocalServer(Layer.L2);
        root.Children.Add(Separator());

        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = "Max Tokens:", Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
                FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Width = 100
            });
            _maxTokensBox = new TextBox
            {
                Text = (ai.MaxTokens > 0 ? ai.MaxTokens : 4096).ToString(),
                FontFamily = new("Consolas"), FontSize = 13, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                Background = LtaiTheme.Sbb(LtaiTheme.BgInput), Width = 120
            };
            row.Children.Add(_maxTokensBox);
            root.Children.Add(row);
        }
        root.Children.Add(Separator());

        BuildLocalModelsSection(root);
        var _ = RefreshModelsAsync();

        root.Children.Add(Separator());

        BuildHarnessProfileSection(root);

        root.Children.Add(Separator());

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var saveBtn = new Button
        {
            Content = "Save & Apply", Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            Foreground = LtaiTheme.Sbb("#ffffff"), FontWeight = FontWeight.Bold, Width = 120
        };
        saveBtn.Click += (_, _) => Save();
        btnRow.Children.Add(saveBtn);
        _statusText = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        btnRow.Children.Add(_statusText);
        root.Children.Add(btnRow);

        root.Children.Add(new TextBlock
        {
            Text = "API keys saved to environment variables. Restart after saving.",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontSize = 11, Margin = new(0, 4, 0, 0)
        });

        UpdateAllUi();
        Content = new ScrollViewer { Content = root };
    }

    private enum Layer { L0, L1, L2 }

    private void UpdateAllUi()
    {
        UpdateApiKeyVisibility();
        UpdateServerButtonForLayer(_l0SrvBtn, _l0ProviderBox, _l0ModelBox);
        UpdateServerButtonForLayer(_l1SrvBtn, _l1ProviderBox, _l1ModelBox);
        UpdateServerButtonForLayer(_l2SrvBtn, _l2ProviderBox, _l2ModelBox);
    }

    private void OnProviderChanged(Layer layer)
    {
        UpdateAllUi();
        ClearTestResult(layer);
    }

    private static void UpdateServerButtonForLayer(Button btn, ComboBox box, TextBox modelBox)
    {
        var provider = box.SelectedItem?.ToString() ?? "";
        var model = modelBox.Text?.Trim();
        var isLocal = provider == "local" || provider == "ollama";
        btn.IsVisible = isLocal && !string.IsNullOrEmpty(model);

        if (btn.IsVisible && btn.Content?.ToString() == "Start")
        {
            Task.Run(async () =>
            {
                if (!string.IsNullOrEmpty(model) && await IsModelLoadedAsync(model))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        btn.Content = "Stop";
                        btn.Background = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
                    });
                }
            });
        }
    }

    private void ClearTestResult(Layer layer)
    {
        var (_, _, _, r, _) = GetLayer(layer);
        r.Text = "";
    }

    private static TextBox ApiKeyBox(StackPanel root, string label, string provider, string fallbackEnv)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = label, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Width = 100
        });
        var existing = GetExistingApiKey(provider);
        var box = new TextBox
        {
            Text = existing, Watermark = GetEnvVarName(provider) ?? fallbackEnv,
            PasswordChar = '*', FontFamily = new("Consolas"), FontSize = 12,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Background = LtaiTheme.Sbb(LtaiTheme.BgInput), Width = 400
        };
        row.Children.Add(box);
        root.Children.Add(row);
        return box;
    }

    private void UpdateApiKeyVisibility()
    {
        SetApiKeyRowVisible(_l0ApiKeyBox, _l0ProviderBox.SelectedItem?.ToString());
        SetApiKeyRowVisible(_l1ApiKeyBox, _l1ProviderBox.SelectedItem?.ToString());
        SetApiKeyRowVisible(_l2ApiKeyBox, _l2ProviderBox.SelectedItem?.ToString());
    }

    private static void SetApiKeyRowVisible(TextBox box, string? provider)
    {
        var parent = box.Parent as StackPanel;
        if (parent == null) return;
        var isLocal = provider == "local" || (provider != null && Array.IndexOf(LocalOnlyProviders, provider) >= 0);
        parent.IsVisible = !string.IsNullOrEmpty(provider) && !isLocal;
    }

    private async Task TestConnection(Layer layer)
    {
        var (providerBox, modelBox, _, result, _) = GetLayer(layer);
        var provider = providerBox.SelectedItem?.ToString() ?? "";
        var model = modelBox.Text?.Trim() ?? "";

        result.Text = "Testing...";
        result.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentWarning);

        try
        {
            if (provider == "local")
            {
                TestLocalModelFiles(result, model);
                return;
            }

            if (Array.IndexOf(LocalOnlyProviders, provider) >= 0)
            {
                await TestLocalProviderEndpoint(result, provider, model);
                return;
            }

            await TestCloudProvider(result, provider, model);
        }
        catch (Exception ex)
        {
            result.Text = $"Error: {ex.Message}";
            result.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
        }
    }

    private static void TestLocalModelFiles(TextBlock result, string modelName)
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "assets", "models");
        if (!Directory.Exists(baseDir))
        {
            result.Text = "No models directory";
            result.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
            return;
        }

        var found = new List<string>();
        foreach (var layer in new[] { "l0", "l1", "l2" })
        {
            var dir = Path.Combine(baseDir, layer);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(modelName) || name.Contains(modelName, StringComparison.OrdinalIgnoreCase))
                    found.Add($"{layer}/{Path.GetFileName(file)}");
            }
        }

        if (found.Count > 0)
        {
            result.Text = $"Found: {string.Join(", ", found.Take(3))}{(found.Count > 3 ? "..." : "")}";
            result.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
        }
        else
        {
            result.Text = modelName.Length > 0 ? $"Model '{modelName}' not found. Download below." : "No models found. Download below.";
            result.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
        }
    }

    private async Task TestLocalProviderEndpoint(TextBlock result, string provider, string model)
    {
        var (url, port) = provider switch
        {
            "ollama" => ("http://localhost:11434", 11434),
            "lmstudio" => ("http://localhost:1234", 1234),
            "vllm" => ("http://localhost:8000", 8000),
            "llamacpp" => ("http://localhost:8080", 8080),
            "open_webui" => ("http://localhost:3000", 3000),
            _ => ("http://localhost:11434", 11434)
        };

        try
        {
            var resp = await _http.GetAsync($"{url}/api/tags");
            var ok = resp.IsSuccessStatusCode;
            result.Text = ok
                ? $"{provider} running on port {port}"
                : $"{provider} port {port} responded {(int)resp.StatusCode}";
            result.Foreground = LtaiTheme.Sbb(ok ? LtaiTheme.AccentSystem : LtaiTheme.AccentWarning);
        }
        catch
        {
            result.Text = $"{provider} not running (port {port}). Click Start button.";
            result.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
        }
    }

    private async Task TestCloudProvider(TextBlock result, string provider, string model)
    {
        var baseUrl = _providerReg.GetBaseUrl(provider) ?? $"https://api.{provider}.com/v1";
        var apiKey = GetExistingApiKey(provider);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            result.Text = $"Enter API key for {provider} first";
            result.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentWarning);
            return;
        }

        var endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages = new[] { new { role = "user", content = "ping" } },
            max_tokens = 1,
            temperature = 0f
        });

        var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {apiKey}");

        var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        var ok = resp.IsSuccessStatusCode;

        if (ok)
        {
            result.Text = $"{provider}/{model} OK";
            result.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
        }
        else
        {
            var msg = body.Length > 100 ? body[..100] : body;
            result.Text = $"{provider}: {(int)resp.StatusCode} — {msg}";
            result.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
        }
    }

    private async Task ToggleLocalServer(Layer layer)
    {
        var (providerBox, modelBox, _, _, srvBtn) = GetLayer(layer);
        var provider = providerBox.SelectedItem?.ToString() ?? "";
        var modelName = modelBox.Text?.Trim() ?? "";
        if ((provider != "local" && provider != "ollama") || string.IsNullOrEmpty(modelName)) return;

        var isLoaded = await IsModelLoadedAsync(modelName);
        if (isLoaded)
        {
            await StopModelAsync(modelName);
            srvBtn.Content = "Start";
            srvBtn.Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
            _statusText.Text = $"{modelName} stopped.";
            _statusText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
        }
        else
        {
            srvBtn.Content = "Loading...";
            srvBtn.IsEnabled = false;

            var modelPath = FindModelFile(modelName);
            if (modelPath == null)
            {
                srvBtn.Content = "Start";
                srvBtn.IsEnabled = true;
                _statusText.Text = $"Model file not found for '{modelName}'. Download it below.";
                _statusText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
                return;
            }

            var ok = await LoadModelIntoOllamaAsync(modelName, modelPath);
            if (ok)
            {
                srvBtn.Content = "Stop";
                srvBtn.Background = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
                _statusText.Text = $"{modelName} loaded and ready.";
                _statusText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
            }
            else
            {
                srvBtn.Content = "Start";
                _statusText.Text = $"Failed to load {modelName}. Is ollama installed?";
                _statusText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
            }
            srvBtn.IsEnabled = true;
        }
    }

    private static string? FindModelFile(string modelName)
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "assets", "models");
        if (!Directory.Exists(baseDir)) return null;

        var search = modelName.ToLowerInvariant();
        foreach (var layer in new[] { "l0", "l1", "l2" })
        {
            var dir = Path.Combine(baseDir, layer);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (name.Contains(search) || search.Contains(name))
                    return file;
            }
        }
        return null;
    }

    private static async Task<bool> IsModelLoadedAsync(string modelName)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "ollama", Arguments = "list",
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            });
            if (proc == null) return false;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output.Split('\n').Any(line =>
                line.TrimStart().StartsWith(modelName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static async Task<bool> LoadModelIntoOllamaAsync(string modelName, string modelPath)
    {
        try
        {
            var modelfile = Path.GetTempFileName();
            var ext = Path.GetExtension(modelPath).ToLowerInvariant();
            if (ext is ".gguf" or ".bin")
            {
                await File.WriteAllTextAsync(modelfile, $"FROM \"{modelPath}\"\n");
            }
            else if (ext is ".onnx")
            {
                await File.WriteAllTextAsync(modelfile,
                    $"FROM \"{modelPath}\"\n" +
                    "PARAMETER num_gpu 0\n");
            }
            else
            {
                await File.WriteAllTextAsync(modelfile, $"FROM \"{modelPath}\"\n");
            }

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "ollama",
                Arguments = $"create {EscapeArg(modelName)} -f \"{modelfile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc == null) return false;
            await proc.WaitForExitAsync();
            try { File.Delete(modelfile); } catch { }
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task StopModelAsync(string modelName)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "ollama",
                Arguments = $"stop {EscapeArg(modelName)}",
                UseShellExecute = false, CreateNoWindow = true
            });
            if (proc != null) await proc.WaitForExitAsync();
        }
        catch { }
    }

    private static string EscapeArg(string arg)
    {
        if (!arg.Contains(' ') && !arg.Contains('"')) return arg;
        return $"\"{arg.Replace("\"", "\\\"")}\"";
    }

    private StackPanel? _localModelsPanel;
    private readonly StackPanel _localModelsContent = new() { Spacing = 2 };

    private void BuildHarnessProfileSection(StackPanel root)
    {
        root.Children.Add(new TextBlock
        {
            Text = "Harness Profile — Workflow Mode",
            FontSize = 16, FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });
        root.Children.Add(new TextBlock
        {
            Text = "Controls how the AI orchestrates tasks: Controlled (auditable, single-agent), Evolutionary (multi-agent, self-learning), Hybrid (both)",
            FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            TextWrapping = TextWrapping.Wrap
        });

        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 4) };
        modeRow.Children.Add(new TextBlock
        {
            Text = "Mode:", Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Width = 100
        });
        var harnessBox = new ComboBox
        {
            ItemsSource = new[] { "Hybrid", "Controlled", "Evolutionary" },
            SelectedIndex = _opts.Harness.Mode switch
            {
                HarnessMode.Controlled => 1,
                HarnessMode.Evolutionary => 2,
                _ => 0
            },
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 13, Width = 160
        };
        harnessBox.SelectionChanged += (_, _) =>
        {
            _opts.Harness = HarnessProfile.For(harnessBox.SelectedIndex switch
            {
                1 => HarnessMode.Controlled,
                2 => HarnessMode.Evolutionary,
                _ => HarnessMode.Hybrid
            });
        };
        modeRow.Children.Add(harnessBox);
        modeRow.Children.Add(new TextBlock
        {
            Text = "Ctrl+T Theme | Changes take effect after Save & Restart",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(modeRow);
    }

    private void BuildLocalModelsSection(StackPanel root)
    {
        root.Children.Add(new TextBlock
        {
            Text = "Local Models",
            FontSize = 15, FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentWarning), Margin = new(0, 4, 0, 2)
        });

        var hw = HardwareDetector.Detect();
        root.Children.Add(new TextBlock
        {
            Text = $"RAM: {hw.RamGB:F1} GB | VRAM: {(hw.HasGpu ? $"{hw.VramGB:F1} GB ({hw.GpuName})" : "none")} | CPU: {hw.CpuCores} cores | Disk: {hw.FreeDiskMB / 1024.0:F0} GB free",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontSize = 11
        });

        var refreshBtn = new Button
        {
            Content = "Refresh from HF",
            FontSize = 10, Width = 100, Height = 22,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentWarning)
        };
        refreshBtn.Click += async (_, _) => await RefreshModelsAsync();
        root.Children.Add(refreshBtn);

        _localModelsPanel = new StackPanel { Spacing = 2, Margin = new(0, 4, 0, 0) };
        _localModelsPanel.Children.Add(_localModelsContent);
        root.Children.Add(_localModelsPanel);

        root.Children.Add(new TextBlock
        {
            Text = "Fit levels (llmfit-inspired): Perfect (VRAM <40%) > Good (<60%) > Marginal (<90%) > Tight (<140%). HF mirror preferred for downloads.",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontSize = 10, FontStyle = FontStyle.Italic,
            Margin = new(0, 4, 0, 0)
        });
    }

    private async Task RefreshModelsAsync()
    {
        _statusText.Text = "Fetching model list from HF...";
        _statusText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentWarning);
        try
        {
            var all = await ModelFitter.FetchAndScoreAsync();
            _localModelsContent.Children.Clear();

            foreach (var model in all)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(0, 1) };

                var fitColor = model.FitLabel switch
                {
                    "Perfect" => LtaiTheme.AccentSystem,
                    "Good" => LtaiTheme.AccentDNA,
                    "Marginal" => LtaiTheme.AccentWarning,
                    _ => LtaiTheme.AccentDanger
                };

                row.Children.Add(new TextBlock
                {
                    Text = model.IsInstalled ? "[Installed]" : $"[{model.FitLabel}]",
                    FontSize = 11,
                    Foreground = model.IsInstalled
                        ? LtaiTheme.Sbb(LtaiTheme.AccentSystem)
                        : LtaiTheme.Sbb(fitColor),
                    VerticalAlignment = VerticalAlignment.Center, Width = 70
                });

                var desc = $"[{model.Layer}] {model.Name} ({model.ParamsB:F1}B {model.BestQuant}) — {model.MemoryMB}MB";
                row.Children.Add(new TextBlock
                {
                    Text = desc, FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
                    VerticalAlignment = VerticalAlignment.Center
                });

                if (!model.IsInstalled)
                {
                    var dlBtn = new Button
                    {
                        Content = "Download",
                        FontSize = 10, Width = 64, Height = 20,
                        Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                        Foreground = LtaiTheme.Sbb(LtaiTheme.AccentWarning)
                    };
                    var captured = model;
                    dlBtn.Click += async (_, _) => await DownloadFitResultAsync(captured, dlBtn);
                    row.Children.Add(dlBtn);
                }
                else
                {
                    var delBtn = new Button
                    {
                        Content = "Delete",
                        FontSize = 10, Width = 48, Height = 20,
                        Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                        Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger)
                    };
                    var captured = model;
                    delBtn.Click += async (_, _) => await DeleteFitResultAsync(captured, delBtn);
                    row.Children.Add(delBtn);
                }

                _localModelsContent.Children.Add(row);
            }
            _statusText.Text = $"Loaded {all.Count} models. {all.Count(m => m.IsInstalled)} installed.";
            _statusText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
        }
        catch (Exception ex)
        {
            _statusText.Text = $"HF fetch failed: {ex.Message}";
            _statusText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
        }
    }

    private async Task DownloadFitResultAsync(ModelFitter.FitResult model, Button btn)
    {
        try
        {
            btn.Content = "..."; btn.IsEnabled = false;
            _statusText.Text = $"Downloading {model.Name}...";
            _statusText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentWarning);

            var localInfo = new LocalModelInfo(
                Version: model.ModelId.ToLowerInvariant().Replace("/", "-"),
                Name: model.Name,
                Url: model.DownloadUrl,
                MirrorUrl: model.MirrorUrl,
                Sha256: "auto",
                RecommendedMemoryMB: model.MemoryMB,
                DiskSizeMB: model.DiskMB,
                Description: $"{model.ParamsB:F1}B {model.Architecture} {model.BestQuant}",
                Layer: model.Layer,
                EngineType: "gguf");

            var downloader = new ModelDownloader();
            var modelsDir = Path.Combine(AppContext.BaseDirectory, "assets", "models");
            var lastPct = 0;

            await downloader.DownloadAsync(localInfo, modelsDir, new Progress<ModelDownloadProgress>(p =>
            {
                if ((int)p.Percent != lastPct)
                {
                    lastPct = (int)p.Percent;
                    Dispatcher.UIThread.Post(() => { btn.Content = $"{lastPct}%"; });
                }
            }), CancellationToken.None);

            btn.Content = "Done"; btn.IsEnabled = true;
            _statusText.Text = $"{model.Name} downloaded. Use provider='local' in the layer above.";
            _statusText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);

            SelectLocalInLayer(model.Layer, model.Name);
        }
        catch (Exception ex)
        {
            btn.Content = "Retry"; btn.IsEnabled = true;
            _statusText.Text = $"Download failed: {ex.Message}";
            _statusText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
        }
    }

    private static async Task DeleteFitResultAsync(ModelFitter.FitResult model, Button btn)
    {
        try
        {
            btn.Content = "..."; btn.IsEnabled = false;

            var fileName = model.DownloadUrl.Split('/').Last();
            var dir = Path.Combine(AppContext.BaseDirectory, "assets", "models", model.Layer.ToString().ToLowerInvariant());
            var path = Path.Combine(dir, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            btn.Content = "Deleted";
            btn.Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim);
        }
        catch (Exception ex)
        {
            btn.Content = "Err"; btn.IsEnabled = true;
            System.Diagnostics.Debug.WriteLine($"Delete failed: {ex.Message}");
        }
    }

    private static void SelectProvider(ComboBox box, string provider)
    {
        for (var i = 0; i < box.Items.Count; i++)
            if (box.Items[i]?.ToString() == provider) { box.SelectedIndex = i; return; }
    }

    private void SelectLocalInLayer(ModelLayer layer, string modelName)
    {
        switch (layer)
        {
            case ModelLayer.L0:
                SelectProvider(_l0ProviderBox, "local");
                _l0ModelBox.Text = modelName; break;
            case ModelLayer.L1:
                SelectProvider(_l1ProviderBox, "local");
                _l1ModelBox.Text = modelName; break;
            case ModelLayer.L2:
                SelectProvider(_l2ProviderBox, "local");
                _l2ModelBox.Text = modelName; break;
        }
    }

    private static bool IsModelInstalled(LocalModelInfo model)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "models", model.Layer.ToString().ToLowerInvariant());
        if (!Directory.Exists(dir)) return false;
        var name = model.Url.Split('/').Last();
        return File.Exists(Path.Combine(dir, name));
    }

    private record HardwareInfo(long RamMB, long VramMB, int CpuCores, long FreeDiskMB)
    {
        public double RamGB => RamMB / 1024.0;
        public double VramGB => VramMB / 1024.0;
        public double FreeDiskGB => FreeDiskMB / 1024.0;
    }

    private static HardwareInfo DetectHardware()
    {
        long ram = 0, vram = 0, disk = 0;
        int cpu = Environment.ProcessorCount;
        try { ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024); }
        catch { ram = 8192; }
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\");
            disk = drive.AvailableFreeSpace / (1024 * 1024);
        }
        catch { }
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                proc.WaitForExit(3000);
                var txt = proc.StandardOutput.ReadToEnd();
                if (long.TryParse(txt.Trim().Split('\n')[0].Trim(), out var nv)) vram = nv;
                proc.Dispose();
            }
        }
        catch { }
        return new HardwareInfo(ram, vram, cpu, disk);
    }

    private static (string Label, Color Color) RateFit(LocalModelInfo model, HardwareInfo hw)
    {
        var availMB = hw.VramMB > 0 ? hw.VramMB : hw.RamMB;
        var needMB = model.RecommendedMemoryMB;
        if (needMB <= 0) needMB = (long)(model.DiskSizeMB * 2.5);
        var ratio = availMB > 0 ? needMB / (double)availMB : 2.0;

        if (ratio <= 0.4) return ("Perfect", LtaiTheme.AccentSystem);
        if (ratio <= 0.7) return ("Good", LtaiTheme.AccentDNA);
        if (ratio <= 1.0) return ("Marginal", LtaiTheme.AccentWarning);
        if (ratio <= 1.5) return ("Tight", LtaiTheme.AccentDanger);
        return ("Too Big", Color.Parse("#888888"));
    }

    private static string GetFitAdvice(HardwareInfo hw)
    {
        var parts = new List<string>();
        if (hw.VramMB > 0)
        {
            var vramGB = hw.VramMB / 1024.0;
            if (vramGB >= 12) parts.Add($"GPU VRAM {vramGB:F0}GB — Q4_K_M quants up to ~{vramGB * 0.8:F0}GB models");
            else if (vramGB >= 6) parts.Add($"VRAM {vramGB:F0}GB — suitable for 3-7B models at Q4");
            else parts.Add($"VRAM {vramGB:F0}GB — try 1-3B models or CPU fallback");
        }
        else parts.Add("No GPU detected. Models run on CPU via RAM only.");

        if (hw.FreeDiskGB < 10) parts.Add("Low disk space — free up before downloading.");
        return string.Join(" | ", parts);
    }

    private void Save()
    {
        try
        {
            SetLayerConfig(_opts.AI.L0, _l0ProviderBox.SelectedItem?.ToString(), _l0ModelBox.Text, null);
            SetLayerConfig(_opts.AI.L1, _l1ProviderBox.SelectedItem?.ToString(), _l1ModelBox.Text, TryParseFloat(_l1TempBox.Text!));
            SetLayerConfig(_opts.AI.L2, _l2ProviderBox.SelectedItem?.ToString(), _l2ModelBox.Text, TryParseFloat(_l2TempBox.Text!));

            if (int.TryParse(_maxTokensBox.Text, out var mt) && mt > 0)
                _opts.AI.MaxTokens = mt;

            SaveApiKeyPerLayer(_opts.AI.L0.Provider, _l0ApiKeyBox.Text);
            SaveApiKeyPerLayer(_opts.AI.L1.Provider, _l1ApiKeyBox.Text);
            SaveApiKeyPerLayer(_opts.AI.L2.Provider, _l2ApiKeyBox.Text);

            SaveToFile();
            _statusText.Text = "Saved! Restart to apply changes.";
            _statusText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Save failed: {ex.Message}";
            _statusText.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
        }
    }

    private static void SaveApiKeyPerLayer(string? provider, string? key)
    {
        if (string.IsNullOrEmpty(provider) || string.IsNullOrWhiteSpace(key)) return;
        if (provider == "local" || Array.IndexOf(LocalOnlyProviders, provider) >= 0) return;

        var envVar = GetEnvVarName(provider);
        if (envVar == null) return;

        Environment.SetEnvironmentVariable(envVar, key.Trim(), EnvironmentVariableTarget.Process);
        try { Environment.SetEnvironmentVariable(envVar, key.Trim(), EnvironmentVariableTarget.User); } catch { }
    }

    private static void SetLayerConfig(LayerConfig cfg, string? provider, string? model, float? temperature)
    {
        if (provider != null)
            typeof(LayerConfig).GetProperty("Provider")!.SetValue(cfg, provider);
        if (!string.IsNullOrWhiteSpace(model))
            typeof(LayerConfig).GetProperty("Model")!.SetValue(cfg, model.Trim());
        if (temperature.HasValue)
            typeof(LayerConfig).GetProperty("Temperature")!.SetValue(cfg, temperature.Value);
    }

    private static float? TryParseFloat(string s)
        => float.TryParse(s, out var v) && v >= 0 && v <= 2 ? v : null;

    private void SaveToFile()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var json = JsonSerializer.Serialize(
            new Dictionary<string, LTAIOptions> { ["LTAI"] = _opts },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
    }

    private static string? GetEnvVarName(string provider)
    {
        var key = provider.ToLowerInvariant();
        return EnvVarFor.TryGetValue(key, out var n) ? n : null;
    }

    private static string GetExistingApiKey(string provider)
    {
        var envVar = GetEnvVarName(provider);
        if (envVar == null) return "";
        return Environment.GetEnvironmentVariable(envVar, EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable(envVar, EnvironmentVariableTarget.User) ?? "";
    }

    private static (ComboBox box, Button testBtn, TextBlock result, Button srvBtn) LayerSectionFull(StackPanel root, string label, List<string> providers, string currentProvider, string currentModel)
    {
        var header = new TextBlock
        {
            Text = label, FontSize = 15, FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA), Margin = new(0, 4, 0, 2)
        };
        root.Children.Add(header);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = "Provider:", Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Width = 100
        });

        var box = new ComboBox
        {
            FontFamily = new("Consolas"), FontSize = 13,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Background = LtaiTheme.Sbb(LtaiTheme.BgInput), Width = 180
        };
        foreach (var p in providers) box.Items.Add(p);

        var idx = providers.IndexOf(currentProvider);
        if (idx >= 0) box.SelectedIndex = idx;
        else if (providers.Count > 0) box.SelectedIndex = 0;

        var testBtn = new Button
        {
            Content = "Test", FontSize = 11, Width = 52, Height = 24,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA)
        };
        var testResult = new TextBlock
        {
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim)
        };

        row.Children.Add(box);
        row.Children.Add(testBtn);
        row.Children.Add(testResult);
        root.Children.Add(row);

        var srvBtn = new Button
        {
            Content = "Start", FontSize = 11, Width = 52, Height = 24,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            IsVisible = currentProvider == "local" || currentProvider == "ollama"
        };
        root.Children.Add(srvBtn);

        return (box, testBtn, testResult, srvBtn);
    }

    private static TextBox ModelInput(StackPanel root, string current, string watermark)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = "Model:", Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Width = 100
        });
        var box = new TextBox
        {
            Text = current, Watermark = watermark, FontFamily = new("Consolas"), FontSize = 13,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Background = LtaiTheme.Sbb(LtaiTheme.BgInput), Width = 360
        };
        row.Children.Add(box);
        root.Children.Add(row);
        return box;
    }

    private static TextBox TempInput(StackPanel root, float? current, string watermark)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = "Temperature:", Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Width = 100
        });
        var box = new TextBox
        {
            Text = (current ?? 0.3f).ToString("F2"), Watermark = watermark, FontFamily = new("Consolas"), FontSize = 13,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Background = LtaiTheme.Sbb(LtaiTheme.BgInput), Width = 80
        };
        row.Children.Add(box);
        root.Children.Add(row);
        return box;
    }

    private (ComboBox box, TextBox modelBox, TextBox apiKeyBox, TextBlock testResult, Button srvBtn) GetLayer(Layer layer) => layer switch
    {
        Layer.L0 => (_l0ProviderBox, _l0ModelBox, _l0ApiKeyBox, _l0TestResult, _l0SrvBtn),
        Layer.L1 => (_l1ProviderBox, _l1ModelBox, _l1ApiKeyBox, _l1TestResult, _l1SrvBtn),
        _          => (_l2ProviderBox, _l2ModelBox, _l2ApiKeyBox, _l2TestResult, _l2SrvBtn),
    };

    private static Border Separator() => new()
    {
        Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border), Margin = new(0, 2)
    };
}

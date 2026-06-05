using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LTAI.AI;
using LTAI.Core.Configuration;
using LTAI.Core.Commands;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace LTAI.TUI.Services;

public sealed class ModelCommandService : ICommandService
{
    private readonly MultiProviderChatClient? _router;
    private readonly LocalEmbedder? _embedder;
    private readonly ModelMetadataProvider? _modelsProvider;
    private readonly string _defaultProvider;
    private readonly string _l1Model;
    private readonly string _l2Model;
    private List<string>? _availableLayerModels;

    public ModelCommandService(
        MultiProviderChatClient? router,
        LocalEmbedder? embedder,
        ModelMetadataProvider? modelsProvider,
        IOptions<LTAIOptions> options)
    {
        _router = router;
        _embedder = embedder;
        _modelsProvider = modelsProvider;
        _defaultProvider = options.Value.AI.DefaultProvider ?? "DeepSeek";
        _l1Model = options.Value.AI.GetLayerConfig("fast").Model;
        _l2Model = options.Value.AI.GetLayerConfig("deep").Model;
    }

    public CommandResult Execute(Command command) => command switch
    {
        ModelCommand mc => HandleModelCommand(mc.Args),
        ModelsCommand => HandleModelsCommand(),
        _ => new SuccessResult("ok"),
    };

    // ── /models ──

    private CommandResult HandleModelsCommand()
    {
        var lines = new List<string> { "[bold yellow]当前模型配置[/]\n" };

        var embedder = _embedder;
        if (embedder != null && embedder.Available)
            lines.Add($"  [cyan]L0 嵌入[/]  {embedder.CurrentModelName}  [dim]({embedder.Dim}d)[/]");
        else if (embedder != null)
            lines.Add("  [cyan]L0 嵌入[/]  [yellow]未加载 (运行 /model l0 download)[/]");
        else
            lines.Add("  [cyan]L0 嵌入[/]  [grey]不可用[/]");

        // Reads from options (appsettings.json — single config file)
        lines.Add(_l1Model != null
            ? $"  [cyan]L1 标准 (必需)[/]  {_defaultProvider} / [white]{_l1Model}[/]"
            : "  [cyan]L1 标准 (必需)[/]  [yellow]未配置 (/model l1)[/]");
        lines.Add(_l2Model != null
            ? $"  [cyan]L2 深度 (可选项)[/]  {_defaultProvider} / [white]{_l2Model}[/]"
            : "  [cyan]L2 深度 (可选项)[/]  [yellow]未配置 (/model l2)[/]");

        var mp = _modelsProvider;
        if (mp == null) { lines.Add("\n[grey]ModelMetadataProvider 未注册[/]"); return new SuccessResult(string.Join("\n", lines)); }
        var all = mp.AllModels;
        if (all.Count == 0) { lines.Add("\n[grey]在线模型信息尚未获取（后台刷新进行中）[/]"); return new SuccessResult(string.Join("\n", lines)); }

        lines.Add($"\n[bold yellow]在线模型元数据[/]\n");
        foreach (var group in all.GroupBy(m => m.Provider).OrderBy(g => g.Key))
        {
            lines.Add($"  [cyan]{group.Key}[/][green] ({group.Count()})[/]");
            var sample = group.OrderByDescending(m => m.ContextWindow ?? 0).Take(5);
            foreach (var m in sample)
            {
                var ctx = m.ContextWindow != null ? CommandHelpers.FormatNum(m.ContextWindow.Value) : "";
                var caps = m.Capabilities != 0 ? $" [dim]{CommandHelpers.AbbrevCaps(m.Capabilities)}[/]" : "";
                var priceIn = m.PriceInPerM > 0 ? $" [yellow]¥{m.PriceInPerM:F2} 输入[/]" : "";
                var priceOut = m.PriceOutPerM > 0 ? $" [yellow]¥{m.PriceOutPerM:F2} 输出[/]" : "";
                var priceCache = m.PriceInCachePerM > 0 ? $" [yellow]¥{m.PriceInCachePerM:F2} 缓存[/]" : "";
                var priceLine = (priceIn + priceOut + priceCache).TrimStart() != ""
                    ? $"\n      {priceIn}{priceOut}{priceCache}"
                    : "";
                lines.Add($"    · [white]{m.Id}[/] {ctx}{caps}{priceLine}");
            }
            var remaining = group.Count() - sample.Count();
            if (remaining > 0)
                lines.Add($"    [dim]...还有 {remaining} 个模型[/]");
        }

        lines.Add($"\n[grey]总计 {all.Count} 个模型，来自 {all.Select(m => m.Provider).Distinct().Count()} 个 Provider[/]");
        lines.Add("[dim]能力缩写: Chat=聊天, Str=流式, Tool=工具, Func=函数, Struct=结构化输出, Vis=视觉, Emb=嵌入, Img=图像[/]");
        return new SuccessResult(string.Join("\n", lines));
    }

    // ── /model ──

    private CommandResult HandleModelCommand(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs = parts.Length > 1 ? parts[1] : "";

        var embedder = _embedder;
        if (embedder == null && subCmd is not "l1" and not "l2" and not "ls")
            return new SuccessResult("ONNX embedder not available");

        if (subCmd is "l1" or "l2")
        {
            var layerArgs = subArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (layerArgs.Length == 0)
                return HandleLayerSelect(subCmd);
            if (layerArgs.Length == 1)
                return HandleLayerSelectComplete(subCmd, layerArgs[0]);
            var p = layerArgs[0]; var m = layerArgs[1];
            SaveLayerSelection(subCmd, p, m);
            if (_router != null && SlashCommands.KnownProviders.TryGetValue(p, out var info))
            {
                var key = SecretManager.Get(info.EnvVar) ?? "";
                _router.Register(subCmd, OpenAIChatClientFactory.Create(info.Endpoint ?? "", m, key));
            }
            return new SuccessResult($"[green]✓ {subCmd.ToUpperInvariant()}={p}/{m}[/]");
        }

        if (subCmd == "l0" && subArgs.StartsWith("api", StringComparison.OrdinalIgnoreCase))
        {
            var apiArgs = subArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return apiArgs.Length == 1 ? ShowL0ApiProviders() : ShowL0ApiModels(apiArgs[1]);
        }

        if (subCmd == "l0")
            subCmd = subArgs.Split(' ', 2)[0].ToLowerInvariant();
        subArgs = subArgs.Trim();

        return (subCmd, embedder) switch
        {
            ("" or "list", not null) => HandleModelList(embedder),
            ("switch", not null) => HandleModelSwitch(embedder, subArgs),
            ("download", not null) => HandleModelDownload(embedder, subArgs),
            ("delete" or "remove", not null) => HandleModelDelete(embedder, subArgs),
            ("cleanup", not null) => HandleModelCleanup(embedder, subArgs),
            ("info", not null) => HandleModelInfo(embedder),
            ("quant", not null) => HandleModelQuant(embedder, subArgs),
            _ => new SuccessResult("用法: /model [l0 [list|download|switch|delete|info|cleanup|quant]] | l1 | l2"),
        };
    }

    private CommandResult HandleLayerSelect(string layer)
    {
        var providers = SlashCommands.KnownProviders
            .Where(kv => !string.IsNullOrEmpty(kv.Value.EnvVar))
            .Select(kv => kv.Key)
            .ToList();
        if (providers.Count == 0)
            return new SuccessResult("没有可用 provider。请先设置 API Key。");

        var picker = new SelectionPrompt<string>()
            .Title($"[yellow]选择 {layer.ToUpperInvariant()} provider:[/]")
            .PageSize(15)
            .AddChoices(providers);
        var chosen = AnsiConsole.Prompt(picker);
        if (!SlashCommands.KnownProviders.TryGetValue(chosen, out var info)) return new SuccessResult("");

        string? existingKey = null;
        if (!string.IsNullOrEmpty(info.EnvVar))
        {
            existingKey = SecretManager.Get(info.EnvVar);
            if (string.IsNullOrEmpty(existingKey) && layer == "l2")
            {
                var l1Sel = ReadLayerSelection("l1");
                if (l1Sel is { provider: not null } && string.Equals(l1Sel.Value.provider, chosen, StringComparison.OrdinalIgnoreCase))
                    existingKey = SecretManager.Get(info.EnvVar);
            }
            if (string.IsNullOrEmpty(existingKey))
            {
                var key = AnsiConsole.Prompt(new TextPrompt<string>($"[yellow]输入 {chosen} API Key ({info.EnvVar}):[/]").Secret());
                if (string.IsNullOrWhiteSpace(key)) return new SuccessResult("已取消");
                existingKey = key;
                SecretManager.Set(info.EnvVar, key, persistent: true);
            }
        }

        var authHeader = existingKey != null ? new AuthenticationHeaderValue("Bearer", existingKey) : null;
        AnsiConsole.Status().Start("获取模型列表...", ctx =>
        {
            try
            {
                var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var req = new HttpRequestMessage(HttpMethod.Get, $"{info.Endpoint.TrimEnd('/')}/models");
                if (authHeader != null) req.Headers.Authorization = authHeader;
                var resp = http.Send(req);
                if (!resp.IsSuccessStatusCode) { _availableLayerModels = null; return; }
                using var json = JsonDocument.Parse(resp.Content.ReadAsStream());
                _availableLayerModels = json.RootElement.GetProperty("data")
                    .EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "")
                    .Where(id => !string.IsNullOrEmpty(id)).OrderBy(id => id).ToList();
            }
            catch { _availableLayerModels = null; }
        });
        if (_availableLayerModels is not { Count: > 0 })
            return new SuccessResult("获取模型列表失败，请检查 API Key 和网络连接");

        var modelPicker = new SelectionPrompt<string>()
            .Title($"[yellow]为 {layer.ToUpperInvariant()} 选择模型 ({chosen}):[/]")
            .PageSize(15)
            .AddChoices(_availableLayerModels);
        var model = AnsiConsole.Prompt(modelPicker);
        _availableLayerModels = null;

        SaveLayerSelection(layer, chosen, model);

        if (_router != null)
        {
            try
            {
                var client = OpenAIChatClientFactory.Create(info.Endpoint, model, existingKey);
                _router.Register(layer, client);
                AnsiConsole.MarkupLine($"[green]✓ 已注册 {layer}: {chosen}/{model}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]注册失败: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        return new SuccessResult($"已配置 [green]{layer.ToUpperInvariant()}[/] = [cyan]{chosen}[/] / [yellow]{model}[/]");
    }

    private CommandResult HandleLayerSelectComplete(string layer, string provider)
    {
        if (!SlashCommands.KnownProviders.TryGetValue(provider, out var info))
            return new SuccessResult($"未知 provider: {provider}");
        var sb = new StringBuilder();
        sb.AppendLine($"[bold]{layer.ToUpperInvariant()} Provider: {provider}[/]");
        string? key = !string.IsNullOrEmpty(info.EnvVar) ? SecretManager.Get(info.EnvVar) : null;
        if (!string.IsNullOrEmpty(info.EnvVar) && string.IsNullOrEmpty(key))
            return new SuccessResult($"未设置 {info.EnvVar}，/config apikey {provider}");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var req = new HttpRequestMessage(HttpMethod.Get, $"{info.Endpoint!.TrimEnd('/')}/models");
            if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var resp = http.Send(req);
            if (!resp.IsSuccessStatusCode) return new SuccessResult($"获取模型失败 ({(int)resp.StatusCode})");
            using var json = JsonDocument.Parse(resp.Content.ReadAsStream());
            var models = json.RootElement.GetProperty("data")
                .EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id)).OrderBy(id => id).Take(30).ToList();
            sb.AppendLine($"模型 ({models.Count}):");
            foreach (var m in models) sb.AppendLine($"  · [cyan]{m}[/]");
            sb.AppendLine($"[dim]/model {layer} {provider} <模型名>[/]");
        }
        catch (Exception ex) { sb.AppendLine($"[red]{ex.Message.EscapeMarkup()}[/]"); }
        return new SuccessResult(sb.ToString());
    }

    private CommandResult ShowL0ApiProviders()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[bold yellow]/model l0 api[/]");
        sb.AppendLine("[grey]可用 Provider:[/]");
        foreach (var (name, info) in SlashCommands.KnownProviders)
        {
            if (string.IsNullOrEmpty(info.Endpoint)) continue;
            var hasKey = !string.IsNullOrEmpty(info.EnvVar) && SecretManager.Has(info.EnvVar);
            sb.AppendLine($"  · [cyan]{name}[/] {(hasKey ? "[green]Key✓[/]" : "[yellow]需设置 Key[/]")}");
        }
        sb.AppendLine($"[dim]输入 /model l0 api <provider> 继续[/]");
        return new SuccessResult(sb.ToString());
    }

    private CommandResult ShowL0ApiModels(string provider)
    {
        if (!SlashCommands.KnownProviders.TryGetValue(provider, out var info))
            return new SuccessResult($"未知 provider: {provider}");

        var sb = new StringBuilder();
        sb.AppendLine($"[bold yellow]L0 Embedding: {provider}[/]");

        string? key = !string.IsNullOrEmpty(info.EnvVar) ? SecretManager.Get(info.EnvVar) : null;
        if (!string.IsNullOrEmpty(info.EnvVar) && string.IsNullOrEmpty(key))
        {
            sb.AppendLine($"[red]未设置 {info.EnvVar}，请先 /config apikey {provider}[/]");
            return new SuccessResult(sb.ToString());
        }
        sb.AppendLine(key != null ? "[green]✓ Key 已设置[/]" : "[green]✓ 无需 Key[/]");

        sb.Append("[grey]获取 embedding 模型...[/]");
        List<string> models;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var req = new HttpRequestMessage(HttpMethod.Get, $"{info.Endpoint.TrimEnd('/')}/models");
            if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var resp = http.Send(req);
            if (!resp.IsSuccessStatusCode) return new SuccessResult($"获取失败 ({(int)resp.StatusCode})");
            using var json = JsonDocument.Parse(resp.Content.ReadAsStream());
            var allModels = json.RootElement.GetProperty("data")
                .EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id)).OrderBy(id => id).ToList();
            models = allModels.Where(id => id.Contains("embed", StringComparison.OrdinalIgnoreCase)).ToList();
            if (models.Count == 0) models = allModels.Take(20).ToList();
        }
        catch (Exception ex) { return new SuccessResult($"获取失败: {ex.Message.EscapeMarkup()}"); }

        sb.AppendLine($"[grey]{models.Count} 个模型:[/]");
        foreach (var m in models) sb.AppendLine($"  · [cyan]{m}[/]");
        sb.AppendLine($"[dim]输入 /model l0 api {provider} <模型名> 完成设置[/]");
        return new SuccessResult(sb.ToString());
    }

    private static void SaveLayerSelection(string layer, string provider, string model)
    {
        LTAIOptions.SaveLayerToAppSettings(layer, provider, model);
    }

    private static (string? provider, string? model)? ReadLayerSelection(string layer)
    {
        return null; // Replaced by IOptions<LTAIOptions> read at call sites
    }

    private static CommandResult HandleModelList(LocalEmbedder embedder)
    {
        var models = LocalEmbedder.ListAvailableModels();
        if (models.Count == 0) return new SuccessResult("没有可用的 ONNX 模型");

        var lines = new List<string> { "[bold yellow]可用的 ONNX Embedding 模型[/]\n" };
        foreach (var m in models)
        {
            var status = m.Downloaded
                ? (string.Equals(m.Id, embedder.CurrentModelName, StringComparison.OrdinalIgnoreCase)
                    ? "[green]● 当前使用[/]"
                    : "[grey]已下载[/]")
                : "[yellow]未下载[/]";
            lines.Add($"  [cyan]{m.Id,-16}[/] {status}");
            lines.Add($"    {m.DisplayName,-20} [grey]{m.Description}[/]");
            lines.Add("");
        }
        return new SuccessResult(string.Join("\n", lines));
    }

    private static CommandResult HandleModelSwitch(LocalEmbedder embedder, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new SuccessResult("用法: /model switch <模型ID>  例如: /model switch bge-small-zh");

        if (!LocalEmbedder.KnownModels.ContainsKey(name))
            return new SuccessResult($"未知模型 '{name}'。可用模型: {string.Join(", ", LocalEmbedder.KnownModels.Keys)}");

        if (!embedder.SwitchModel(name))
        {
            var baseDir = LocalEmbedder.BaseModelsDirectory;
            return new SuccessResult($"模型 '{name}' 未下载。请先运行 /model download {name}。模型目录: {baseDir}");
        }

        return new SuccessResult($"已切换到 ONNX 模型: [green]{name}[/]（{LocalEmbedder.KnownModels[name].DisplayName}）\n" +
            "已自动清空 tool/agent embedding 缓存，下次路由会重新计算向量。");
    }

    private static CommandResult HandleModelDownload(LocalEmbedder embedder, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var models = LocalEmbedder.ListAvailableModels();
            var pending = models.Where(m => !m.Downloaded).ToList();
            if (pending.Count == 0) return new SuccessResult("所有模型均已下载");
            name = pending[0].Id;
        }

        if (!LocalEmbedder.KnownModels.ContainsKey(name))
            return new SuccessResult($"未知模型 '{name}'。可用: {string.Join(", ", LocalEmbedder.KnownModels.Keys)}");

        var info = LocalEmbedder.KnownModels[name];
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var success = Task.Run(() => embedder.DownloadModelAsync(name, http)).GetAwaiter().GetResult();
        return success
            ? new SuccessResult($"✅ 模型 [green]{name}[/]（{info.DisplayName}）下载完成")
            : new SuccessResult($"❌ 模型 '{name}' 下载失败。请检查网络连接后重试");
    }

    private static CommandResult HandleModelDelete(LocalEmbedder embedder, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new SuccessResult("用法: /model delete <模型ID>");

        if (!LocalEmbedder.KnownModels.ContainsKey(name))
            return new SuccessResult($"未知模型 '{name}'");

        if (string.Equals(name, embedder.CurrentModelName, StringComparison.OrdinalIgnoreCase))
            return new SuccessResult($"不能删除当前正在使用的模型 '{name}'。请先切换到其他模型");

        if (embedder.DeleteModel(name))
            return new SuccessResult($"已删除模型 '{name}'");

        return new SuccessResult($"模型 '{name}' 不存在或已删除");
    }

    private static CommandResult HandleModelCleanup(LocalEmbedder embedder, string arg)
    {
        var baseDir = LocalEmbedder.BaseModelsDirectory;
        if (baseDir == null) return new SuccessResult("Models 目录未初始化");

        var names = string.IsNullOrWhiteSpace(arg)
            ? LocalEmbedder.ListAvailableModels()
                .Where(m => m.Downloaded || m.QuantizedDownloaded)
                .Select(m => m.Id).ToList()
            : new List<string> { arg.Trim() };

        if (names.Count == 0) return new SuccessResult("没有已下载的模型可清理");

        int totalFiles = 0;
        long totalBytes = 0;
        var details = new List<string>();
        var currentPref = (LocalEmbedder.Options.Quantization ?? "auto").ToLowerInvariant();

        foreach (var name in names)
        {
            if (!LocalEmbedder.KnownModels.ContainsKey(name))
            {
                details.Add($"  [red]✗[/] {name}: 未知模型");
                continue;
            }
            var info = LocalEmbedder.KnownModels[name];
            var modelDir = Path.Combine(baseDir, name);
            if (!Directory.Exists(modelDir))
            {
                details.Add($"  [grey]–[/] {name}: 未下载，跳过");
                continue;
            }

            var targetQuant = currentPref switch
            {
                "fp32" => false,
                "int8" => true,
                _ => string.Equals(embedder.CurrentModelName, name, StringComparison.OrdinalIgnoreCase)
                    ? embedder.UsingQuantizedModel
                    : true,
            };

            long bytesRemoved = 0;
            int filesRemoved = 0;
            if (targetQuant)
            {
                var fp32 = Path.Combine(modelDir, "model.onnx");
                if (File.Exists(fp32) && new FileInfo(fp32).Length > 1024)
                {
                    bytesRemoved += new FileInfo(fp32).Length;
                    try { File.Delete(fp32); filesRemoved++; } catch { }
                }
            }
            else
            {
                if (info.QuantizedFileName != null)
                {
                    var q = Path.Combine(modelDir, info.QuantizedFileName);
                    if (File.Exists(q))
                    {
                        bytesRemoved += new FileInfo(q).Length;
                        try { File.Delete(q); filesRemoved++; } catch { }
                    }
                }
            }
            totalFiles += filesRemoved;
            totalBytes += bytesRemoved;
            var kept = targetQuant ? "INT8" : "FP32";
            if (filesRemoved > 0)
                details.Add($"  [green]✓[/] {name}: 释放 {CommandHelpers.FormatBytes(bytesRemoved)}, 保留 {kept}");
            else
                details.Add($"  [grey]–[/] {name}: 已单变种（保留 {kept}）");
        }

        var header = "[bold yellow]模型清理[/]\n";
        if (totalFiles > 0)
            header += $"  释放 [green]{CommandHelpers.FormatBytes(totalBytes)}[/]（{totalFiles} 文件）\n\n";
        else
            header += "  没有可清理的旧变种\n\n";
        return new SuccessResult(header + string.Join("\n", details));
    }

    private static CommandResult HandleModelInfo(LocalEmbedder embedder)
    {
        var baseDir = LocalEmbedder.BaseModelsDirectory;
        var models = LocalEmbedder.ListAvailableModels();
        var lines = new List<string> { "[bold yellow]ONNX Embedder 详情[/]\n" };

        lines.Add($"  偏好 quant: [cyan]{LocalEmbedder.Options.Quantization}[/]  " +
                  $"GPU: [cyan]{LocalEmbedder.Options.Gpu}[/]  " +
                  $"DeviceId: [cyan]{LocalEmbedder.Options.DeviceId}[/]");
        if (LocalEmbedder.Options.Models is { Count: > 0 } perModel)
        {
            var entries = string.Join(", ",
                perModel.Select(kv => $"[cyan]{kv.Key}[/]=[yellow]{kv.Value}[/]"));
            lines.Add($"  per-model: {entries}");
        }
        if (LocalEmbedder.DefaultDisabled)
            lines.Add("  状态: [grey]已禁用（远程 API 接管）[/]");
        else if (embedder.Available)
        {
            var ep = embedder.ActiveExecutionProvider ?? "?";
            var epColor = ep == "CPU" ? "grey" : "green";
            var quant = embedder.UsingQuantizedModel ? "INT8" : "FP32";
            var quantColor = quant == "INT8" ? "green" : "yellow";
            lines.Add($"  当前: [cyan]{embedder.CurrentModelName}[/]  " +
                      $"EP: [{epColor}]{ep}[/]  " +
                      $"quant: [{quantColor}]{quant}[/]  " +
                      $"Dim: [cyan]{embedder.Dim}[/]");
        }
        else
            lines.Add("  状态: [yellow]未加载（运行 /model list|download）[/]");
        lines.Add($"  目录: [grey]{baseDir ?? "(not set)"}[/]\n");

        foreach (var m in models)
        {
            var isCurrent = string.Equals(m.Id, embedder.CurrentModelName, StringComparison.OrdinalIgnoreCase);
            var marker = isCurrent ? "[green]●[/]" : " ";
            lines.Add($"  {marker} [cyan]{m.Id,-16}[/]  {m.DisplayName}");
            lines.Add($"    [grey]{m.Description}[/]");

            var modelDir = baseDir != null ? Path.Combine(baseDir, m.Id) : null;
            if (modelDir != null && Directory.Exists(modelDir))
            {
                var fp32File = Path.Combine(modelDir, "model.onnx");
                long fp32Size = 0;
                var fp32Valid = false;
                if (File.Exists(fp32File))
                {
                    fp32Size = new FileInfo(fp32File).Length;
                    fp32Valid = fp32Size > 1024;
                }
                var fp32Mark = fp32Valid ? "[green]●[/]" : "[grey]○[/]";
                lines.Add($"    FP32: {fp32Mark} {(fp32Valid ? CommandHelpers.FormatBytes(fp32Size) : "—")}");

                var qInfo = LocalEmbedder.KnownModels[m.Id];
                if (qInfo.QuantizedFileName != null)
                {
                    var qFile = Path.Combine(modelDir, qInfo.QuantizedFileName);
                    var qValid = File.Exists(qFile) && new FileInfo(qFile).Length > 1024;
                    var qMark = qValid ? "[green]●[/]" : "[grey]○[/]";
                    lines.Add($"    INT8: {qMark} {(qValid ? CommandHelpers.FormatBytes(new FileInfo(qFile).Length) : "—")}");
                }
                else
                    lines.Add("    INT8: [grey](无上游量化版)[/]");

                var vocab = Path.Combine(modelDir, "vocab.txt");
                if (File.Exists(vocab))
                    lines.Add($"    Vocab: [green]●[/] {CommandHelpers.FormatBytes(new FileInfo(vocab).Length)}");
                else
                    lines.Add("    Vocab: [red]○[/] —");

                var effQuant = LocalEmbedder.Options.GetQuantizationFor(m.Id);
                var hasOverride = LocalEmbedder.Options.Models.ContainsKey(m.Id);
                var effColor = effQuant == "int8" || effQuant == "auto" ? "green" : "yellow";
                var suffix = hasOverride ? " (override)" : "";
                lines.Add($"    Eff. quant: [{effColor}]{effQuant}[/]{suffix}");
            }
            else
                lines.Add("    [yellow](未下载)[/]");
            lines.Add("");
        }
        return new SuccessResult(string.Join("\n", lines));
    }

    private static CommandResult HandleModelQuant(LocalEmbedder embedder, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return new SuccessResult($"当前 quant 偏好: [cyan]{LocalEmbedder.Options.Quantization}[/]\n" +
                                    $"用法: /model quant fp32|int8|auto");

        var val = arg.Trim().ToLowerInvariant();
        if (val != "fp32" && val != "int8" && val != "auto")
            return new SuccessResult($"未知 quant 偏好: '{arg}'。可用: fp32|int8|auto");

        var oldVal = LocalEmbedder.Options.Quantization;
        LocalEmbedder.Options.Quantization = val;

        var msg = $"Quant 偏好: [grey]{oldVal}[/] → [green]{val}[/]\n";

        if (LocalEmbedder.DefaultDisabled)
        {
            msg += "（embedder 已禁用，下次启动生效）";
            return new SuccessResult(msg);
        }

        if (embedder.CurrentModelName != null)
        {
            try
            {
                if (embedder.SwitchModel(embedder.CurrentModelName))
                {
                    var newQuant = embedder.UsingQuantizedModel ? "INT8" : "FP32";
                    var qColor = newQuant == "INT8" ? "green" : "yellow";
                    msg += $"已重新加载 [cyan]{embedder.CurrentModelName}[/] (使用 [{qColor}]{newQuant}[/])";
                }
                else
                {
                    msg += $"[yellow]⚠[/] 重新加载失败 — 目标 {val} 的变种不存在。\n" +
                           $"    提示: /model info 看磁盘状态，/model download {embedder.CurrentModelName} 重下";
                }
            }
            catch (Exception ex)
            {
                msg += $"[yellow]⚠[/] 重新加载异常: {ex.Message}";
            }
        }
        else
        {
            msg += "（无活动模型，下次启动生效）";
        }
        return new SuccessResult(msg);
    }
}

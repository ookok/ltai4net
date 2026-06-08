using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LTAI.AI;
using LTAI.Core.Configuration;
using LTAI.Core.Commands;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace LTAI.TUI.Services;

public sealed class ConfigCommandService : ICommandService
{
    private readonly MultiProviderChatClient? _router;
    private readonly string _defaultProvider;
    private readonly string _l1Model;
    private readonly string _l2Model;

    public ConfigCommandService(
        MultiProviderChatClient? router,
        IOptions<LTAIOptions> options)
    {
        _router = router;
        _defaultProvider = options.Value.AI.DefaultProvider ?? "DeepSeek";
        _l1Model = options.Value.AI.GetLayerConfig("fast").Model;
        _l2Model = options.Value.AI.GetLayerConfig("deep").Model;
    }

    public Task<CommandResult> ExecuteAsync(Command command) => command switch
    {
        ConfigCommand cc => Task.FromResult(HandleConfigCommand(cc.Args)),
        _ => Task.FromResult<CommandResult>(new SuccessResult("ok")),
    };

    private CommandResult HandleConfigCommand(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs = parts.Length > 1 ? parts[1] : "";

        return subCmd switch
        {
            "status" => ConfigStatus(),
            "provider" => ConfigSelectProvider(),
            "apikey" or "key" => ConfigSetApiKey(subArgs),
            "l1" => ConfigSelectModel("l1"),
            "l2" => ConfigSelectModel("l2"),
            "model" => ConfigSelectModel(subArgs),
            "export" => ConfigExport(subArgs),
            "import" => ConfigImport(subArgs),
            "clear" => ConfigClear(subArgs),
            "clear-all" => ConfigClearAll(),
            _ => new SuccessResult("用法: /config status|provider|apikey|l1|l2|clear [name]|clear-all|export [file]|import [file]"),
        };
    }

    private CommandResult ConfigStatus()
    {
        var prov = _defaultProvider;
        var l1 = _l1Model;
        var l2 = _l2Model;

        var sb = new StringBuilder();
        sb.AppendLine("[bold yellow]LLM 配置状态[/]\n");
        sb.AppendLine($"[bold]当前 Provider:[/] [cyan]{prov}[/]");
        sb.AppendLine($"[bold]L1 模型 (Fast):[/] {l1} [green](必需)[/]");
        sb.AppendLine($"[bold]L2 模型 (Pro):[/]  {l2} [dim](可选项)[/]");
        sb.AppendLine();

        sb.AppendLine("[bold]可用 Provider:[/]");
        foreach (var (name, info) in ProviderHelpers.KnownProviders)
        {
            var keyStatus = string.IsNullOrEmpty(info.EnvVar)
                ? "[dim]Local[/]"
                : SecretManager.Has(info.EnvVar)
                    ? (_router?.RegisteredProviders.Contains(name) == true ? "[green]✅ 就绪[/]" : "[yellow]🔑 已设(需重启)[/]")
                    : "[dim]未设置[/]";
            var isActive = string.Equals(name, prov, StringComparison.OrdinalIgnoreCase);
            sb.AppendLine($"  {(isActive ? "[cyan]> [/]" : "  ")}{name,-20} {keyStatus}");
        }
        return new SuccessResult(sb.ToString());
    }

    private CommandResult ConfigSelectProvider()
    {
        var prompt = new SelectionPrompt<string>()
            .Title("[yellow]选择 LLM Provider:[/]")
            .PageSize(15)
            .MoreChoicesText("[grey](滚动查看更多)[/]")
            .AddChoices(ProviderHelpers.KnownProviders.Keys.OrderBy(k => k));
        var choice = AnsiConsole.Prompt(prompt);

        if (_router != null) _router.ActiveProvider = choice;

        if (ProviderHelpers.KnownProviders.TryGetValue(choice, out var info) && !string.IsNullOrEmpty(info.EnvVar))
        {
            var key = SecretManager.Get(info.EnvVar);
            if (!string.IsNullOrEmpty(key) && _router != null && !_router.RegisteredProviders.Contains(choice))
            {
                var client = OpenAIChatClientFactory.Create(info.Endpoint, info.Model, key);
                _router.Register(choice, client);
            }
        }

        if (ProviderHelpers.KnownProviders.TryGetValue(choice, out var pInfo) && !string.IsNullOrEmpty(pInfo.EnvVar) && !SecretManager.Has(pInfo.EnvVar))
            return new SuccessResult($"已切换到 [cyan]{choice}[/]。使用 /config apikey 设置 API Key");

        return new SuccessResult($"已切换到 [cyan]{choice}[/]");
    }

    private CommandResult ConfigSetApiKey(string providerArg)
    {
        var providerName = !string.IsNullOrEmpty(providerArg) ? providerArg : _defaultProvider;
        if (string.IsNullOrEmpty(providerName) || !ProviderHelpers.KnownProviders.TryGetValue(providerName, out var info))
        {
            if (string.IsNullOrEmpty(providerArg))
                return new SuccessResult("用法: /config apikey <provider名称>  或先通过 /config provider 选择");
            return new SuccessResult($"未知 Provider '{providerArg}'。可用: {string.Join(", ", ProviderHelpers.KnownProviders.Keys)}");
        }

        if (string.IsNullOrEmpty(info.EnvVar))
            return new SuccessResult($"{providerName} 为本地 Provider，无需 API Key");

        var key = AnsiConsole.Prompt(
            new TextPrompt<string>($"[yellow]输入 {providerName} API Key ({info.EnvVar}):[/]").Secret());
        if (string.IsNullOrWhiteSpace(key)) return new SuccessResult("已取消");

        SecretManager.Set(info.EnvVar, key);

        var client = OpenAIChatClientFactory.Create(info.Endpoint, info.Model, key);
        _router?.Register(providerName, client);

        return new SuccessResult($"✅ [green]{info.EnvVar}[/] 已设置并注册");
    }

    private CommandResult ConfigSelectModel(string layer)
    {
        var info = ProviderHelpers.KnownProviders.TryGetValue(_defaultProvider, out var p) ? p : null;
        if (info == null) return new SuccessResult("请先通过 /config provider 选择 Provider");

        if (!string.IsNullOrEmpty(info.EnvVar) && !SecretManager.Has(info.EnvVar))
            return new SuccessResult("请先通过 /config apikey 设置 API Key");

        List<string> models;
        if (!string.IsNullOrEmpty(info.EnvVar))
        {
            var apiKey = SecretManager.Get(info.EnvVar);
            models = !string.IsNullOrEmpty(apiKey) ? FetchModelsFromApi(info.Endpoint, apiKey) : [];
        }
        else
        {
            models = [info.Model];
        }

        if (models.Count == 0)
        {
            models = [info.Model];
            AnsiConsole.MarkupLine("[yellow]无法从 API 获取模型列表，使用常用模型作为参考[/]");
        }

        return new SuccessResult($"L1: [cyan]{_l1Model}[/]  L2: [cyan]{_l2Model}[/]");
    }

    private static List<string> FetchModelsFromApi(string endpoint, string apiKey)
    {
        try
        {
            var http = CommandHelpers.SharedHttp;
            var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = http.Send(req);
            if (!resp.IsSuccessStatusCode) return [];
            using var json = JsonDocument.Parse(resp.Content.ReadAsStream());
            return json.RootElement.GetProperty("data")
                .EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id)).OrderBy(id => id).ToList();
        }
        catch { return []; }
    }

    private CommandResult ConfigExport(string fileArg)
    {
        var knownEnvVars = KnownKeys.All
            .Select(k => k.EnvVar)
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct().ToList();

        var config = new Dictionary<string, string>();
        foreach (var envVar in knownEnvVars)
        {
            var val = SecretManager.Get(envVar);
            if (!string.IsNullOrEmpty(val)) config[envVar] = val;
        }

        if (config.Count == 0)
            return new SuccessResult("没有已配置的环境变量可导出");

        var filePath = fileArg;
        if (string.IsNullOrEmpty(filePath))
        {
            var defaultName = $"ltai-config-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            filePath = Path.Combine(Directory.GetCurrentDirectory(), defaultName);
        }

        File.WriteAllText(filePath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return new SuccessResult($"✅ 已导出 {config.Count} 个环境变量到 [cyan]{Path.GetFullPath(filePath)}[/]\n" +
                                "[yellow]⚠ 此文件包含 API Key，请妥善保管，不要提交到版本控制[/]");
    }

    private CommandResult ConfigImport(string fileArg)
    {
        if (string.IsNullOrWhiteSpace(fileArg))
            return new SuccessResult("用法: /config import <文件路径>");

        var filePath = fileArg;
        if (!File.Exists(filePath))
        {
            filePath = Path.Combine(Directory.GetCurrentDirectory(), fileArg);
            if (!File.Exists(filePath)) return new SuccessResult($"文件不存在: {fileArg}");
        }

        Dictionary<string, string>? config;
        try { config = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath)); }
        catch (Exception ex) { return new SuccessResult($"解析配置文件失败: {ex.Message}"); }

        if (config == null || config.Count == 0)
            return new SuccessResult("配置文件中没有有效的环境变量");

        var imported = 0;
        foreach (var (envVar, value) in config)
        {
            if (!string.IsNullOrEmpty(envVar) && !string.IsNullOrEmpty(value))
            {
                SecretManager.Set(envVar, value);
                var providerName = ProviderHelpers.KnownProviders
                    .Where(kv => string.Equals(kv.Value.EnvVar, envVar, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key).FirstOrDefault();
                if (providerName != null && _router != null)
                {
                    var info = ProviderHelpers.KnownProviders[providerName];
                    var client = OpenAIChatClientFactory.Create(info.Endpoint, info.Model, value);
                    _router.Register(providerName, client);
                }
                imported++;
            }
        }

        return new SuccessResult($"✅ 已导入 {imported}/{config.Count} 个环境变量\n" +
                                "[yellow]部分 Provider 已自动注册，使用 /config status 查看状态[/]");
    }

    private CommandResult ConfigClear(string providerArg)
    {
        if (string.IsNullOrWhiteSpace(providerArg))
            return new SuccessResult("用法: /config clear <provider名称>  清除指定 Provider 的 API Key");

        if (!ProviderHelpers.KnownProviders.TryGetValue(providerArg, out var info))
            return new SuccessResult($"未知 Provider '{providerArg}'。可用: {string.Join(", ", ProviderHelpers.KnownProviders.Keys)}");

        if (string.IsNullOrEmpty(info.EnvVar))
            return new SuccessResult($"{providerArg} 为本地 Provider，无 API Key 可清除");

        if (!AnsiConsole.Confirm($"[yellow]确认清除 {providerArg} 的 API Key ({info.EnvVar})?[/]", false))
            return new SuccessResult("已取消");

        SecretManager.Set(info.EnvVar, null, persistent: true);
        SecretManager.Invalidate(info.EnvVar);
        return new SuccessResult($"✅ 已清除 [cyan]{providerArg}[/] 的 API Key ({info.EnvVar})");
    }

    private CommandResult ConfigClearAll()
    {
        var known = KnownKeys.All.Select(k => k.EnvVar).Distinct().ToList();
        var setKeys = known.Where(e => !string.IsNullOrEmpty(e) && SecretManager.Has(e)).ToList();

        if (setKeys.Count == 0)
            return new SuccessResult("当前没有任何已设置的 API Key");

        AnsiConsole.MarkupLine($"[yellow]将清除以下 {setKeys.Count} 个 API Key:[/]");
        foreach (var envVar in setKeys)
            AnsiConsole.MarkupLine($"  - [red]{envVar}[/]");

        if (!AnsiConsole.Confirm("[red]确认清除全部? (此操作不可撤销)[/]", false))
            return new SuccessResult("已取消");

        var cleared = 0;
        foreach (var envVar in setKeys)
        {
            SecretManager.Set(envVar, null, persistent: true);
            SecretManager.Invalidate(envVar);
            cleared++;
        }

        return new SuccessResult($"✅ 已清除 {cleared} 个 API Key。重启后所有 Provider 将恢复未配置状态。");
    }
}

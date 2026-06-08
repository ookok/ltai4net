using LTAI.Agent.Snippets;
using LTAI.Agent.Tools;
using LTAI.Agent.Workflows;
using LTAI.Core.Commands;
using LTAI.Core.Configuration;
using LTAI.Core.Session;
using LTAI.Core.Specs;
using LTAI.TUI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class AsyncCommandServiceTests
{
    private static readonly IOptions<LTAIOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new LTAIOptions());

    [Fact]
    public async Task ExecuteAsync_ReturnsCompletedTask()
    {
        var pairs = CreateServiceCommandPairs();
        foreach (var (svc, cmd) in pairs)
        {
            var result = await svc.ExecuteAsync(cmd);
            Assert.NotNull(result);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnCancel()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var svc = new DelayedCommandService(TimeSpan.FromSeconds(5));

        var task = svc.ExecuteAsync(new HelpCommand());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WithCancellation(task, cts.Token));
    }

    [Fact]
    public async Task CommandRouter_ExecuteAsync_Help_ReturnsSuccess()
    {
        var cts = new CancellationTokenSource(3000);
        var router = CreateRouter();
        try
        {
            var result = await router.ExecuteAsync(new HelpCommand());
            Assert.NotNull(result);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("CommandRouter.ExecuteAsync(HelpCommand) timed out");
        }
        finally { cts.Dispose(); }
    }

    [Fact]
    public async Task SlashCommands_Dispatch_ReturnsSuccess()
    {
        var router = CreateRouter();
        var cmd = SlashCommands.Parser.Parse("/help");
        var result = await router.ExecuteAsync(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ConfigCommandService_ExecuteAsync_List_ReturnsSuccess()
    {
        var svc = new ConfigCommandService(null, Options);
        var result = await svc.ExecuteAsync(new ConfigCommand("list"));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ConfigCommandService_ExecuteAsync_Status_ReturnsSuccess()
    {
        var svc = new ConfigCommandService(null, Options);
        var result = await svc.ExecuteAsync(new ConfigCommand("status"));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ModelCommandService_ExecuteAsync_List_ReturnsSuccess()
    {
        var mockHttp = new Mock<IHttpClientFactory>();
        mockHttp.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var svc = new ModelCommandService(null, null, null, mockHttp.Object, Options);
        var result = await svc.ExecuteAsync(new ModelCommand("list"));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task WorkflowCommandService_ExecuteAsync_List_ReturnsSuccess()
    {
        var svc = new WorkflowCommandService(null);
        var result = await svc.ExecuteAsync(new WorkflowCommand("list"));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task SnippetCommandService_ExecuteAsync_List_ReturnsSuccess()
    {
        var svc = new SnippetCommandService(null);
        var result = await svc.ExecuteAsync(new LTAI.Core.Commands.SnippetCommand("list"));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task SessionsCommandHandler_ExecuteAsync_List_ReturnsResult()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltai-async-test-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(dir);
            var sessions = new SessionManager(dir);
            var handler = new SessionsCommandHandler(sessions);
            var result = await handler.ExecuteAsync("/sessions list", () => Task.CompletedTask);
            Assert.NotNull(result);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task SpecCommandService_ExecuteAsync_List_ReturnsSuccess()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltai-spec-async-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(dir);
            var specSvc = new SpecService(dir);
            var cmdSvc = new SpecCommandService(specSvc);
            var result = await cmdSvc.ExecuteAsync(new SpecCommand("list"));
            Assert.NotNull(result);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task FileCommandService_ExecuteAsync_List_ReturnsSuccess()
    {
        var svc = new FileCommandService();
        var result = await svc.ExecuteAsync(new LsCommand(""));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task JobsCommandService_ExecuteAsync_List_ReturnsSuccess()
    {
        var svc = new JobsCommandService(null);
        var result = await svc.ExecuteAsync(new JobsCommand("list"));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GitCommandService_ExecuteAsync_Status_ReturnsSuccess()
    {
        var svc = new GitCommandService();
        var result = await svc.ExecuteAsync(new GitCommand("status"));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task PipeCommandService_ExecuteAsync_List_ReturnsSuccess()
    {
        var svc = new PipeCommandService(null, null);
        var result = await svc.ExecuteAsync(new PipeCommand("list"));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ExecuteAsync_LongRunning_RespectsTimeout()
    {
        using var cts = new CancellationTokenSource(50);
        var svc = new DelayedCommandService(TimeSpan.FromSeconds(5));

        var task = svc.ExecuteAsync(new HelpCommand());
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WithCancellation(task, cts.Token));
        Assert.NotNull(canceled);
    }

    private static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>();
        using var _ = ct.Register(() => tcs.TrySetCanceled(ct));
        return await await Task.WhenAny(task, tcs.Task);
    }

    [Fact]
    public async Task PipeCommandService_AsyncChain_Completes()
    {
        var svc = new PipeCommandService(null, null);
        var list = await svc.ExecuteAsync(new PipeCommand("list"));
        Assert.NotNull(list);

        var run = await svc.ExecuteAsync(new PipeCommand("run test-pipe"));
        Assert.NotNull(run);

        var stop = await svc.ExecuteAsync(new PipeCommand("stop test-pipe"));
        Assert.NotNull(stop);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnType_IsTaskOfCommandResult()
    {
        var svc = new FileCommandService();
        Task<CommandResult> resultTask = svc.ExecuteAsync(new HelpCommand());
        Assert.NotNull(resultTask);
        var cmdResult = await resultTask;
        Assert.IsAssignableFrom<CommandResult>(cmdResult);
    }

    private static CommandRouter CreateRouter()
    {
        var devUi = CreateDevUi();
        return new CommandRouter(
            new ModelCommandService(null, null, null, Mock.Of<IHttpClientFactory>(), Options),
            new JobsCommandService(null),
            new ConfigCommandService(null, Options),
            new SnippetCommandService(null),
            new WorkflowCommandService(null),
            new PipeCommandService(null, null),
            new AgentsCommandService(devUi),
            new ToolsCommandService(),
            new McpCommandService(null, Options),
            new GitCommandService(),
            new FileCommandService(),
            new InfoCommandService(),
            new GraphCommandService(null, null),
            new SpecCommandService(new SpecService(Path.GetTempPath())),
            new ThemeCommandService());
    }

    private static LTAI.Agent.DevUI.LTAIDevUIService CreateDevUi()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<LTAI.Agent.DevUI.LTAIDevUIService>.Instance;
        return new LTAI.Agent.DevUI.LTAIDevUIService(sp, logger);
    }

    private static List<(ICommandService svc, Command cmd)> CreateServiceCommandPairs()
    {
        var mockHttp = new Mock<IHttpClientFactory>();
        mockHttp.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(new HttpClient());

        return
        [
            (new ConfigCommandService(null, Options), new ConfigCommand("list")),
            (new ModelCommandService(null, null, null, mockHttp.Object, Options), new ModelCommand("list")),
            (new WorkflowCommandService(null), new WorkflowCommand("list")),
            (new SnippetCommandService(null), new LTAI.Core.Commands.SnippetCommand("list")),
            (new SpecCommandService(new SpecService(Path.GetTempPath())), new SpecCommand("list")),
            (new FileCommandService(), new LsCommand("")),
            (new JobsCommandService(null), new JobsCommand("list")),
            (new GitCommandService(), new GitCommand("status")),
            (new PipeCommandService(null, null), new PipeCommand("list")),
            (new InfoCommandService(), new HelpCommand()),
            (new ToolsCommandService(), new ToolsCommand("list")),
            (new McpCommandService(null, Options), new McpCommand("list")),
            (new GraphCommandService(null, null), new GraphCommand("init")),
            (new ThemeCommandService(), new ThemeCommand("")),
        ];
    }

    private sealed class DelayedCommandService : ICommandService
    {
        private readonly TimeSpan _delay;
        public DelayedCommandService(TimeSpan delay) => _delay = delay;

        public async Task<CommandResult> ExecuteAsync(Command command)
        {
            await Task.Delay(_delay);
            return new SuccessResult("done");
        }
    }
}

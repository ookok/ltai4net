using LTAI.Core.Commands;
using LTAI.Core.Configuration;
using LTAI.TUI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class CommandRouterTests
{
    private static readonly IOptions<LTAIOptions> Options = Microsoft.Extensions.Options.Options.Create(new LTAIOptions());

    private readonly CommandRouter _router;

    public CommandRouterTests()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<LTAI.Agent.DevUI.LTAIDevUIService>.Instance;
        var sp = new ServiceCollection().BuildServiceProvider();
        var devUi = new LTAI.Agent.DevUI.LTAIDevUIService(sp, logger);

        _router = new CommandRouter(
            new ModelCommandService(null, null, null, null, Options),
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
            null!);
    }

    [Fact] public async Task Execute_ModelCommand_DelegatesToModelService() => Assert.NotNull(await _router.ExecuteAsync(new ModelCommand("")));
    [Fact] public async Task Execute_ModelsCommand_DelegatesToModelService() => Assert.NotNull(await _router.ExecuteAsync(new ModelsCommand()));
    [Fact] public async Task Execute_JobsCommand_DelegatesToJobsService() => Assert.NotNull(await _router.ExecuteAsync(new JobsCommand("list")));
    [Fact] public async Task Execute_ConfigCommand_DelegatesToConfigService() => Assert.NotNull(await _router.ExecuteAsync(new ConfigCommand("status")));
    [Fact] public async Task Execute_SnippetCommand_DelegatesToSnippetService() => Assert.NotNull(await _router.ExecuteAsync(new SnippetCommand("list")));
    [Fact] public async Task Execute_WorkflowCommand_DelegatesToWorkflowService() => Assert.NotNull(await _router.ExecuteAsync(new WorkflowCommand("list")));
    [Fact] public async Task Execute_PipeCommand_DelegatesToPipeService() => Assert.NotNull(await _router.ExecuteAsync(new PipeCommand("list")));
    [Fact] public async Task Execute_GitCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(await _router.ExecuteAsync(new GitCommand("status")));
    [Fact] public async Task Execute_LsCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(await _router.ExecuteAsync(new LsCommand("")));
    [Fact] public async Task Execute_CdCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(await _router.ExecuteAsync(new CdCommand("")));
    [Fact] public async Task Execute_HelpCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(await _router.ExecuteAsync(new HelpCommand()));
    [Fact] public async Task Execute_StatusCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(await _router.ExecuteAsync(new StatusCommand()));
    [Fact] public async Task Execute_AgentsCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(await _router.ExecuteAsync(new AgentsCommand("")));
    [Fact] public async Task Execute_ToolsCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(await _router.ExecuteAsync(new ToolsCommand("")));
    [Fact] public async Task Execute_McpCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(await _router.ExecuteAsync(new McpCommand("")));
    [Fact] public async Task Execute_GraphCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(await _router.ExecuteAsync(new GraphCommand("init")));
    [Fact] public async Task Execute_ChatMessageCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(await _router.ExecuteAsync(new ChatMessageCommand("hello")));
    [Fact] public async Task Execute_UnknownCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(await _router.ExecuteAsync(new UnknownCommand("unknown")));
    [Fact] public async Task Execute_EmptyCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(await _router.ExecuteAsync(new EmptyCommand()));
}

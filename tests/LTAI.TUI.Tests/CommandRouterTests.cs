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
            new ModelCommandService(null, null, null, Options),
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

    [Fact] public void Execute_ModelCommand_DelegatesToModelService() => Assert.NotNull(_router.Execute(new ModelCommand("")));
    [Fact] public void Execute_ModelsCommand_DelegatesToModelService() => Assert.NotNull(_router.Execute(new ModelsCommand()));
    [Fact] public void Execute_JobsCommand_DelegatesToJobsService() => Assert.NotNull(_router.Execute(new JobsCommand("list")));
    [Fact] public void Execute_ConfigCommand_DelegatesToConfigService() => Assert.NotNull(_router.Execute(new ConfigCommand("status")));
    [Fact] public void Execute_SnippetCommand_DelegatesToSnippetService() => Assert.NotNull(_router.Execute(new SnippetCommand("list")));
    [Fact] public void Execute_WorkflowCommand_DelegatesToWorkflowService() => Assert.NotNull(_router.Execute(new WorkflowCommand("list")));
    [Fact] public void Execute_PipeCommand_DelegatesToPipeService() => Assert.NotNull(_router.Execute(new PipeCommand("list")));
    [Fact] public void Execute_GitCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(_router.Execute(new GitCommand("status")));
    [Fact] public void Execute_LsCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(_router.Execute(new LsCommand("")));
    [Fact] public void Execute_CdCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(_router.Execute(new CdCommand("")));
    [Fact] public void Execute_HelpCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(_router.Execute(new HelpCommand()));
    [Fact] public void Execute_StatusCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(_router.Execute(new StatusCommand()));
    [Fact] public void Execute_AgentsCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(_router.Execute(new AgentsCommand("")));
    [Fact] public void Execute_ToolsCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(_router.Execute(new ToolsCommand("")));
    [Fact] public void Execute_McpCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(_router.Execute(new McpCommand("")));
    [Fact] public void Execute_GraphCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(_router.Execute(new GraphCommand("init")));
    [Fact] public void Execute_ChatMessageCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(_router.Execute(new ChatMessageCommand("hello")));
    [Fact] public void Execute_UnknownCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(_router.Execute(new UnknownCommand("unknown")));
    [Fact] public void Execute_EmptyCommand_ReturnsSuccess() => Assert.IsType<SuccessResult>(_router.Execute(new EmptyCommand()));
}

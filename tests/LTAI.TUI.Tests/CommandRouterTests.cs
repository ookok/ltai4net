using LTAI.Core.Commands;
using LTAI.Core.Configuration;
using LTAI.TUI.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace LTAI.TUI.Tests;

public sealed class CommandRouterTests
{
    private static readonly IOptions<LTAIOptions> Options = Microsoft.Extensions.Options.Options.Create(new LTAIOptions());

    private readonly ModelCommandService _modelService;
    private readonly JobsCommandService _jobsService;
    private readonly ConfigCommandService _configService;
    private readonly SnippetCommandService _snippetService;
    private readonly WorkflowCommandService _workflowService;
    private readonly PipeCommandService _pipeService;
    private readonly CommandRouter _router;

    public CommandRouterTests()
    {
        _modelService = new ModelCommandService(null, null, null, Options);
        _jobsService = new JobsCommandService(null);
        _configService = new ConfigCommandService(null, Options);
        _snippetService = new SnippetCommandService(null);
        _workflowService = new WorkflowCommandService(null);
        _pipeService = new PipeCommandService(null, null);

        _router = new CommandRouter(
            _modelService,
            _jobsService,
            _configService,
            _snippetService,
            _workflowService,
            _pipeService
        );
    }

    [Fact]
    public void Execute_ModelCommand_DelegatesToModelService()
    {
        var cmd = new ModelCommand("");
        var result = _router.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_ModelsCommand_DelegatesToModelService()
    {
        var cmd = new ModelsCommand();
        var result = _router.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_JobsCommand_DelegatesToJobsService()
    {
        var cmd = new JobsCommand("list");
        var result = _router.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_ConfigCommand_DelegatesToConfigService()
    {
        var cmd = new ConfigCommand("status");
        var result = _router.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_SnippetCommand_DelegatesToSnippetService()
    {
        var cmd = new SnippetCommand("list");
        var result = _router.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_WorkflowCommand_DelegatesToWorkflowService()
    {
        var cmd = new WorkflowCommand("list");
        var result = _router.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_PipeCommand_DelegatesToPipeService()
    {
        var cmd = new PipeCommand("list");
        var result = _router.Execute(cmd);
        Assert.NotNull(result);
    }

    [Fact]
    public void Execute_NonRoutedCommand_ReturnsSuccess()
    {
        var cmd = new HelpCommand();
        var result = _router.Execute(cmd);
        Assert.IsType<SuccessResult>(result);
    }

    [Fact]
    public void Execute_ChatMessageCommand_ReturnsSuccess()
    {
        var cmd = new ChatMessageCommand("hello");
        var result = _router.Execute(cmd);
        Assert.IsType<SuccessResult>(result);
    }

    [Fact]
    public void Execute_UnknownCommand_ReturnsSuccess()
    {
        var cmd = new UnknownCommand("unknown");
        var result = _router.Execute(cmd);
        Assert.IsType<SuccessResult>(result);
    }

    [Fact]
    public void Execute_EmptyCommand_ReturnsSuccess()
    {
        var cmd = new EmptyCommand();
        var result = _router.Execute(cmd);
        Assert.IsType<SuccessResult>(result);
    }

    [Fact]
    public void Execute_GitCommand_ReturnsSuccess()
    {
        var cmd = new GitCommand("status");
        var result = _router.Execute(cmd);
        Assert.IsType<SuccessResult>(result);
    }
}

using LTAI.Core.Commands;

namespace LTAI.TUI.Services;

/// <summary>
/// Thin dispatcher that routes each Command to its ICommandService.
/// Created in Phase 2 extraction from the monolithic original.
/// </summary>
public sealed class CommandRouter
{
    private readonly ModelCommandService _modelService;
    private readonly JobsCommandService _jobsService;
    private readonly ConfigCommandService _configService;
    private readonly SnippetCommandService _snippetService;
    private readonly WorkflowCommandService _workflowService;
    private readonly PipeCommandService _pipeService;

    public CommandRouter(
        ModelCommandService modelService,
        JobsCommandService jobsService,
        ConfigCommandService configService,
        SnippetCommandService snippetService,
        WorkflowCommandService workflowService,
        PipeCommandService pipeService)
    {
        _modelService = modelService;
        _jobsService = jobsService;
        _configService = configService;
        _snippetService = snippetService;
        _workflowService = workflowService;
        _pipeService = pipeService;
    }

    public CommandResult Execute(Command cmd) => cmd switch
    {
        ModelCommand or ModelsCommand => _modelService.Execute(cmd),
        JobsCommand => _jobsService.Execute(cmd),
        ConfigCommand => _configService.Execute(cmd),
        SnippetCommand => _snippetService.Execute(cmd),
        WorkflowCommand => _workflowService.Execute(cmd),
        PipeCommand => _pipeService.Execute(cmd),
        _ => new SuccessResult("ok"),
    };
}

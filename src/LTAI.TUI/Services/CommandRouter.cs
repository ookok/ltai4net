using LTAI.Core.Commands;

namespace LTAI.TUI.Services;

public sealed class CommandRouter
{
    private readonly ModelCommandService _modelService;
    private readonly JobsCommandService _jobsService;
    private readonly ConfigCommandService _configService;
    private readonly SnippetCommandService _snippetService;
    private readonly WorkflowCommandService _workflowService;
    private readonly PipeCommandService _pipeService;
    private readonly AgentsCommandService _agentsService;
    private readonly ToolsCommandService _toolsService;
    private readonly McpCommandService _mcpService;
    private readonly GitCommandService _gitService;
    private readonly FileCommandService _fileService;
    private readonly InfoCommandService _infoService;
    private readonly GraphCommandService _graphService;
    private readonly SpecCommandService _specService;

    public CommandRouter(
        ModelCommandService modelService,
        JobsCommandService jobsService,
        ConfigCommandService configService,
        SnippetCommandService snippetService,
        WorkflowCommandService workflowService,
        PipeCommandService pipeService,
        AgentsCommandService agentsService,
        ToolsCommandService toolsService,
        McpCommandService mcpService,
        GitCommandService gitService,
        FileCommandService fileService,
        InfoCommandService infoService,
        GraphCommandService graphService,
        SpecCommandService specService)
    {
        _modelService = modelService;
        _jobsService = jobsService;
        _configService = configService;
        _snippetService = snippetService;
        _workflowService = workflowService;
        _pipeService = pipeService;
        _agentsService = agentsService;
        _toolsService = toolsService;
        _mcpService = mcpService;
        _gitService = gitService;
        _fileService = fileService;
        _infoService = infoService;
        _graphService = graphService;
        _specService = specService;
    }

    public CommandResult Execute(Command cmd) => cmd switch
    {
        ModelCommand or ModelsCommand => _modelService.Execute(cmd),
        JobsCommand => _jobsService.Execute(cmd),
        ConfigCommand => _configService.Execute(cmd),
        SnippetCommand => _snippetService.Execute(cmd),
        WorkflowCommand => _workflowService.Execute(cmd),
        PipeCommand => _pipeService.Execute(cmd),
        AgentsCommand => _agentsService.Execute(cmd),
        ToolsCommand => _toolsService.Execute(cmd),
        McpCommand => _mcpService.Execute(cmd),
        GitCommand => _gitService.Execute(cmd),
        LsCommand or CdCommand or PwdCommand => _fileService.Execute(cmd),
        HelpCommand or StatusCommand => _infoService.Execute(cmd),
        GraphCommand => _graphService.Execute(cmd),
        SpecCommand => _specService.Execute(cmd),
        _ => new SuccessResult("ok"),
    };
}

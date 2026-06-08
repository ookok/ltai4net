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
    private readonly ThemeCommandService _themeService;

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
        SpecCommandService specService,
        ThemeCommandService? themeService = null)
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
        _themeService = themeService ?? new ThemeCommandService();
    }

    public Task<CommandResult> ExecuteAsync(Command cmd) => cmd switch
    {
        ModelCommand or ModelsCommand => _modelService.ExecuteAsync(cmd),
        JobsCommand => _jobsService.ExecuteAsync(cmd),
        ConfigCommand => _configService.ExecuteAsync(cmd),
        SnippetCommand => _snippetService.ExecuteAsync(cmd),
        WorkflowCommand => _workflowService.ExecuteAsync(cmd),
        PipeCommand => _pipeService.ExecuteAsync(cmd),
        AgentsCommand => _agentsService.ExecuteAsync(cmd),
        ToolsCommand => _toolsService.ExecuteAsync(cmd),
        McpCommand => _mcpService.ExecuteAsync(cmd),
        GitCommand => _gitService.ExecuteAsync(cmd),
        LsCommand or CdCommand or PwdCommand => _fileService.ExecuteAsync(cmd),
        HelpCommand or StatusCommand => _infoService.ExecuteAsync(cmd),
        GraphCommand => _graphService.ExecuteAsync(cmd),
        SpecCommand => _specService.ExecuteAsync(cmd),
        ThemeCommand => _themeService.ExecuteAsync(cmd),
        _ => Task.FromResult<CommandResult>(new SuccessResult("ok")),
    };
}

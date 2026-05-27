namespace LTAI.Core.Configuration;

public interface IProjectSpecProvider
{
    ProjectSpec GetProjectSpec();
    void SetProjectSpec(ProjectSpec spec);
    string GetBuildCommand();
    string GetTestCommand();
    string GetLintCommand();
    string GetFormatCommand();
    string GetRunCommand();
}

public sealed class ProjectSpecProvider : IProjectSpecProvider
{
    private ProjectSpec _spec;
    private readonly object _lock = new();

    public ProjectSpecProvider(ProjectSpec spec)
    {
        _spec = spec;
    }

    public ProjectSpec GetProjectSpec()
    {
        lock (_lock) return _spec;
    }

    public void SetProjectSpec(ProjectSpec spec)
    {
        lock (_lock) { _spec = spec; }
    }

    public string GetBuildCommand() { lock (_lock) return $"{_spec.BuildCommand} {_spec.BuildArgs}"; }
    public string GetTestCommand() { lock (_lock) return $"{_spec.TestCommand} {_spec.TestArgs}"; }
    public string GetLintCommand() { lock (_lock) return $"{_spec.LintCommand} {_spec.LintArgs}"; }
    public string GetFormatCommand() { lock (_lock) return $"{_spec.FormatCommand} {_spec.FormatArgs}"; }
    public string GetRunCommand() { lock (_lock) return $"{_spec.RunCommand} {_spec.RunArgs}"; }
}

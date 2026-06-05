using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LTAI.Desktop.ViewModels;

public sealed partial class TextPadViewModel : ViewModelBase
{
    private readonly string _rootDir;
    private string? _currentFile;
    private bool _isReadOnly = true;
    private string _projectType = "unknown";
    private int _scrollOffset;
    private bool _showSplit;
    private bool _editMode;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _gitBranch = "";

    [ObservableProperty]
    private bool _hasPendingError;

    public string RootDir => _rootDir;
    public string? CurrentFile { get => _currentFile; set => SetProperty(ref _currentFile, value); }
    public bool IsReadOnly { get => _isReadOnly; set => SetProperty(ref _isReadOnly, value); }
    public string ProjectType { get => _projectType; set => SetProperty(ref _projectType, value); }
    public int ScrollOffset { get => _scrollOffset; set => SetProperty(ref _scrollOffset, value); }
    public bool ShowSplit { get => _showSplit; set => SetProperty(ref _showSplit, value); }
    public bool EditMode { get => _editMode; set => SetProperty(ref _editMode, value); }

    public Dictionary<string, string> GitFileStatus { get; } = new();
    public ObservableCollection<(string file, int line, string msg)> Problems { get; } = new();

    private string? _lastError;
    private string? _lastErrorCommand;
    public string? LastError => _lastError;
    public string? LastErrorCommand => _lastErrorCommand;

    public TextPadViewModel(string rootDir)
    {
        _rootDir = rootDir;
    }

    public void SetError(string command, string error)
    {
        _lastErrorCommand = command;
        _lastError = error;
        HasPendingError = true;
    }

    public void ClearError()
    {
        _lastError = null;
        _lastErrorCommand = null;
        HasPendingError = false;
    }
}

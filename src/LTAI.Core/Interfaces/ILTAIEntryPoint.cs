namespace LTAI.Core.Interfaces;

public interface ILTAIEntryPoint
{
    bool CanHandle(string command);
    Task RunAsync(string[] args);
}

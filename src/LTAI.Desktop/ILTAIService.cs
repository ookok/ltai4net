namespace LTAI.Desktop;

/// <summary>Interface for LTAIService. Enables mocking ChatView dependencies in tests.</summary>
public interface ILTAIService
{
    IChatService Chat { get; }
    string Mode { get; }
    IServiceProvider? Services { get; }
}

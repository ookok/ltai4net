namespace LTAI.Agent.Prompts;

public interface IPromptLoader
{
    string Load(string name);
    string LoadLang(string name);
    void ClearCache();
}

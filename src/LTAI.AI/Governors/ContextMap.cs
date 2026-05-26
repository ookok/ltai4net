namespace LTAI.AI.Governors;

public sealed class ContextMap
{
    private readonly ContextMapStore _store;

    public ContextMapStore Store => _store;

    public ContextMap(ContextMapStore store)
    {
        _store = store;
    }

    public string InjectIntoPrompt(string prompt)
    {
        var map = _store.BuildContextMap();
        if (string.IsNullOrWhiteSpace(map) || map.Split('\n').Length <= 2)
            return prompt;

        return $"{map}\n\n{prompt}";
    }
}

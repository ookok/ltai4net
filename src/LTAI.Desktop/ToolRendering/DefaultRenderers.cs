namespace LTAI.Desktop.ToolRendering;

public static class DefaultRenderers
{
    public static ToolResultRendererRegistry Create()
    {
        var registry = new ToolResultRendererRegistry();
        registry.Register(new ToolResultJsonRenderer());
        registry.Register(new HandoffRenderer());
        registry.Register(new BudgetHintRenderer());
        registry.Register(new ToolCallEmojiRenderer());
        return registry;
    }
}

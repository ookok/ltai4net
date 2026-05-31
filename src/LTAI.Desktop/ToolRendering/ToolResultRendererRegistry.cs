using System.Collections.Generic;
using Avalonia.Controls;

namespace LTAI.Desktop.ToolRendering;

public sealed class ToolResultRendererRegistry
{
    private readonly List<IToolResultRenderer> _renderers = new();

    public void Register(IToolResultRenderer renderer) => _renderers.Add(renderer);

    public Control? Render(string token, string? context = null)
    {
        foreach (var r in _renderers)
        {
            if (r.CanRender(token))
                return r.Render(token, context);
        }
        return null;
    }
}

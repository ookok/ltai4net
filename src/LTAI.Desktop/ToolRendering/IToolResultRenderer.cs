using Avalonia.Controls;

namespace LTAI.Desktop.ToolRendering;

public interface IToolResultRenderer
{
    bool CanRender(string token);
    Control? Render(string token, string? context = null);
}

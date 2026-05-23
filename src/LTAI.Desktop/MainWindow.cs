using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LTAI.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        Title = "LTAI v7.0 — Sentient Mesh";
        Width = 1280;
        Height = 800;
        Background = new SolidColorBrush(Color.Parse("#0d1117"));

        var svc = ServiceLocator.Get<LTAIService>();

        Content = new TabControl
        {
            TabStripPlacement = Dock.Top,
            Items =
            {
                new TabItem { Header = "Dashboard", Content = new DashboardView(svc) },
                new TabItem { Header = "Chat",      Content = new ChatView(svc) },
                new TabItem { Header = "Diagnostics", Content = new DiagnosticsView(svc) },
            }
        };
    }
}

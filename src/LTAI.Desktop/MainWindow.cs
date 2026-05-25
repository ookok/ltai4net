using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LTAI.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        Title = "LTAI V0.51 — Sentient Mesh";
        Width = 1280;
        Height = 800;
        Background = new SolidColorBrush(Color.Parse("#0d1117"));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ltai-icon.png");
        if (File.Exists(iconPath))
            Icon = new WindowIcon(iconPath);

        var svc = ServiceLocator.Get<LTAIService>();

        var tabControl = new TabControl { TabStripPlacement = Dock.Top };

        tabControl.Items.Add(new TabItem { Header = "Dashboard",   Content = new DashboardView(svc) });
        tabControl.Items.Add(new TabItem { Header = "Chat",        Content = new ChatView(svc) });
        tabControl.Items.Add(new TabItem { Header = "LLM Config",  Content = new LLMConfigView(svc) });
        tabControl.Items.Add(new TabItem { Header = "Pipeline",    Content = new PipelineView(svc) });
        tabControl.Items.Add(new TabItem { Header = "Session",     Content = new SessionView(svc) });
        tabControl.Items.Add(new TabItem { Header = "Diagnostics", Content = new DiagnosticsView(svc) });

        tabControl.SelectedIndex = 1;

        KeyDown += (_, e) =>
        {
            var handled = true;
            switch (e.KeyModifiers)
            {
                case Avalonia.Input.KeyModifiers.Control:
                    switch (e.Key)
                    {
                        case Avalonia.Input.Key.D1: tabControl.SelectedIndex = 0; break;
                        case Avalonia.Input.Key.D2: tabControl.SelectedIndex = 1; break;
                        case Avalonia.Input.Key.D3: tabControl.SelectedIndex = 2; break;
                        case Avalonia.Input.Key.D4: tabControl.SelectedIndex = 3; break;
                        case Avalonia.Input.Key.D5: tabControl.SelectedIndex = 4; break;
                        case Avalonia.Input.Key.D6: tabControl.SelectedIndex = 5; break;
                        case Avalonia.Input.Key.T: LtaiTheme.Toggle(); break;
                        default: handled = false; break;
                    }
                    break;
                case Avalonia.Input.KeyModifiers.None:
                    if (e.Key == Avalonia.Input.Key.Escape)
                    {
                        var chat = tabControl.Items[1] as TabItem;
                        var chatView = chat?.Content as ChatView;
                        chatView?.Cancel();
                    }
                    else handled = false;
                    break;
                default: handled = false; break;
            }
            if (handled) e.Handled = true;
        };

        Content = tabControl;
    }
}
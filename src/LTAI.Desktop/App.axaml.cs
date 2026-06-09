using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public class App : Application
{
    public static LTAIService? Ltais { get; set; }
    public static LTAI.Agent.ChatAgent? ChatAgent { get; set; }
    public static Microsoft.Extensions.Options.IOptions<LTAI.Core.Configuration.LTAIOptions>? Options { get; set; }
    public static LTAI.AI.MultiProviderChatClient? Router { get; set; }
    public static System.Net.Http.IHttpClientFactory? HttpFactory { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            var themeResources = LtaiTheme.GetResources();
            if (themeResources != null)
                Resources.MergedDictionaries.Add(themeResources);
        }
        catch { }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var loadingPanel = new StackPanel { Spacing = 10, Margin = new(30) };
            loadingPanel.Children.Add(new TextBlock
            {
                Text = "LTAI 正在初始化...",
                FontSize = 18, FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Colors.White),
            });
            var loadingHint = new TextBlock
            {
                Text = "首次启动可能需要加载 ONNX 模型。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#aaaaaa")),
            };
            loadingPanel.Children.Add(loadingHint);

            var loadingWindow = new Window
            {
                Title = "LTAI",
                Width = 540, Height = 260,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                CanResize = false,
                Content = loadingPanel,
                Background = new SolidColorBrush(Color.Parse("#1e1e1e")),
            };
            desktop.MainWindow = loadingWindow;

            Task.Run(async () =>
            {
                Exception? initError = null;
                try { await Program.InitializeServicesAsync().ConfigureAwait(false); }
                catch (Exception ex) { initError = ex; }

                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (initError != null)
                            ShowErrorWindow(desktop, loadingWindow, initError);
                        else
                            OpenMainWindow(desktop, loadingWindow);
                    });
                }
                catch (Exception ex)
                {
                    File.AppendAllText("desktop-startup.log",
                        $"[{DateTime.UtcNow:O}] InvokeAsync failed: {ex}\n");
                }
            });
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void OpenMainWindow(IClassicDesktopStyleApplicationLifetime desktop, Window loadingWindow)
    {
        try
        {
            File.AppendAllText("desktop-startup.log",
                $"[{DateTime.UtcNow:O}] OpenMainWindow: creating MainWindow...\n");
            var mainWindow = new MainWindow(App.Ltais!);
            File.AppendAllText("desktop-startup.log",
                $"[{DateTime.UtcNow:O}] OpenMainWindow: MainWindow created, calling Show...\n");
            mainWindow.Show();
            File.AppendAllText("desktop-startup.log",
                $"[{DateTime.UtcNow:O}] OpenMainWindow: setting desktop.MainWindow...\n");
            desktop.MainWindow = mainWindow;
            File.AppendAllText("desktop-startup.log",
                $"[{DateTime.UtcNow:O}] OpenMainWindow: closing loading window...\n");
            loadingWindow.Close();
            File.AppendAllText("desktop-startup.log",
                $"[{DateTime.UtcNow:O}] OpenMainWindow: done\n");
        }
        catch (Exception ex)
        {
            var msg = $"UI 初始化失败:\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace ?? ""}\n\n请重启应用。";
            File.AppendAllText("desktop-startup.log",
                $"[{DateTime.UtcNow:O}] OpenMainWindow failed: {ex}\n");
            loadingWindow.Content = new StackPanel { Spacing = 10, Margin = new(20), Children =
            {
                new TextBlock { Text = "UI 初始化失败", FontSize = 18, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Colors.White) },
                new TextBox { Text = msg, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    MinHeight = 120, Foreground = new SolidColorBrush(Colors.White),
                    Background = new SolidColorBrush(Color.Parse("#2c2c2e")) },
            }};
            loadingWindow.Height = 400;
            loadingWindow.Width = 600;
        }
    }

    private static void ShowErrorWindow(IClassicDesktopStyleApplicationLifetime desktop, Window loadingWindow, Exception ex)
    {
        var root = new StackPanel { Spacing = 10, Margin = new(20) };

        root.Children.Add(new TextBlock { Text = "服务初始化失败", FontSize = 18,
            FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Colors.White) });

        root.Children.Add(new TextBlock { Text = ex.GetType().Name, FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#ff453a")), FontWeight = FontWeight.SemiBold });

        var errorText = $"{ex.Message}\n\n{ex.StackTrace ?? "(no stack)"}";
        var errorBox = new TextBox
        {
            Text = errorText, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 100, MaxHeight = 180,
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Color.Parse("#2c2c2e")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3a3a3c")),
            BorderThickness = new Thickness(1),
        };
        root.Children.Add(errorBox);

        root.Children.Add(new TextBlock { Text = "请检查网络连接和配置后重试。离线模式可用：配置 API Key 后重启。",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Gray),
            FontSize = 12, FontStyle = FontStyle.Italic });

        var btnRow = new StackPanel { Spacing = 8, Margin = new(0, 4, 0, 0) };

        var configBtn = new Button { Content = "⚙️  打开配置", Width = 120 };
        configBtn.Click += (_, _) => new ConfigDialog().ShowDialog(loadingWindow);
        btnRow.Children.Add(configBtn);

        var copyBtn = new Button { Content = "📋  复制错误", Width = 120 };
        copyBtn.Click += (_, _) =>
        {
            try
            {
                // Select all text in the box for manual copy
                errorBox.SelectAll();
                errorBox.Focus();
                copyBtn.Content = "📋  按 Ctrl+C 复制";
            }
            catch { }
        };
        btnRow.Children.Add(copyBtn);

        var restartBtn = new Button { Content = "🔄  重启", Width = 100 };
        restartBtn.Click += (_, _) =>
        {
            loadingWindow.Close();
            // Delay slightly then restart
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                Environment.Exit(0);
            });
        };
        btnRow.Children.Add(restartBtn);

        root.Children.Add(btnRow);
        loadingWindow.Content = root;
        loadingWindow.Title = "LTAI — 初始化失败";
        loadingWindow.Height = 480;
        loadingWindow.Width = 580;
    }
}

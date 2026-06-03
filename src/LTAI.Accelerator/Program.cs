using System.Threading;
using Avalonia;

namespace LTAI.Accelerator;

public static class Program
{
    private static readonly Mutex _singleton = new(true, "LTAI.Accelerator");

    [STAThread]
    static void Main(string[] args)
    {
        if (!_singleton.WaitOne(TimeSpan.Zero, true))
        {
            Console.Error.WriteLine("LTAI Accelerator 已在运行中");
            return;
        }
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _singleton.ReleaseMutex();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}

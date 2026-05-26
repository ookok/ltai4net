using System.Runtime.InteropServices;

namespace LTAI.Core.System;

public static class NotificationService
{
    public static void Show(string title, string message)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                ShowWindows(title, message);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                ShowMac(title, message);
            else
                ShowLinux(title, message);
        }
        catch { /* notifications are best-effort */ }
    }

    private static void ShowWindows(string title, string message)
    {
        var psi = new global::System.Diagnostics.ProcessStartInfo("powershell",
            $"-Command \"[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; " +
            $"$template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02); " +
            $"$template.GetElementsByTagName('text')[0].AppendChild($template.CreateTextNode('{title}')) | Out-Null; " +
            $"$template.GetElementsByTagName('text')[1].AppendChild($template.CreateTextNode('{message}')) | Out-Null; " +
            $"$toast = [Windows.UI.Notifications.ToastNotification]::new($template); " +
            $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('LTAI').Show($toast);\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        global::System.Diagnostics.Process.Start(psi)?.WaitForExit(2000);
    }

    private static void ShowMac(string title, string message)
    {
        var psi = new global::System.Diagnostics.ProcessStartInfo("osascript",
            $"-e 'display notification \"{message}\" with title \"{title}\"'")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        global::System.Diagnostics.Process.Start(psi)?.WaitForExit(2000);
    }

    private static void ShowLinux(string title, string message)
    {
        var psi = new global::System.Diagnostics.ProcessStartInfo("notify-send",
            $"\"{title}\" \"{message}\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        global::System.Diagnostics.Process.Start(psi)?.WaitForExit(2000);
    }
}

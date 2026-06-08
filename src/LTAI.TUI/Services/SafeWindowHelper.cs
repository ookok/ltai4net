namespace LTAI.TUI.Services;

public static class SafeWindowHelper
{
    public static int SafeWidth
    {
        get
        {
            try { return Console.WindowWidth; }
            catch { return 80; }
        }
    }

    public static int SafeHeight
    {
        get
        {
            try { return Console.WindowHeight; }
            catch { return 24; }
        }
    }
}

namespace LTAI.TUI.Input;

/// <summary>
/// Terminal input reader using Console.ReadKey for cross-platform reliability.
/// No SGR mouse mode or Kitty keyboard protocol — those interfere with keyboard input.
/// Mouse click/wheel support is dropped in favor of reliable keyboard input.
/// </summary>
public static class MouseTracker
{
    public static void Enable() { }
    public static void Disable() { }

    public sealed record InputEvent
    {
        public ConsoleKeyInfo? KeyInfo { get; init; }
        public int ScrollDelta { get; init; }
        public (int row, int col)? ClickPosition { get; init; }
        public bool IsRelease { get; init; }
        public string? Raw { get; init; }
    }

    /// <summary>Read the next keyboard input. Blocks until a key is pressed.</summary>
    public static InputEvent ReadNext(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
                return new InputEvent { KeyInfo = Console.ReadKey(true) };
            try { Task.Delay(10, ct).GetAwaiter().GetResult(); }
            catch { break; }
        }
        return new InputEvent { Raw = "" };
    }
}

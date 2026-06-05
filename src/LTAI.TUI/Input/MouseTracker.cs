using System.Text;

namespace LTAI.TUI.Input;

/// <summary>
/// VT mouse + Kitty keyboard protocol support.
/// Enables SGR mouse mode, parses mouse events, and supports extended keyboard sequences.
/// </summary>
public static class MouseTracker
{
    private static bool _enabled;

    /// <summary>Enable SGR mouse mode + Kitty keyboard protocol on the terminal.</summary>
    public static void Enable()
    {
        if (_enabled) return;
        // SGR mouse mode (1006) + button events (1002) + basic mode (1000)
        Console.Write("\x1b[?1000h\x1b[?1002h\x1b[?1006h");
        // Kitty keyboard protocol
        Console.Write("\x1b[>1u");
        _enabled = true;
    }

    /// <summary>Disable mouse tracking (restore terminal state).</summary>
    public static void Disable()
    {
        if (!_enabled) return;
        Console.Write("\x1b[?1006l\x1b[?1002l\x1b[?1000l");
        Console.Write("\x1b[<u");
        _enabled = false;
    }

    /// <summary>Result of parsing a raw terminal input sequence.</summary>
    public sealed record InputEvent
    {
        /// <summary>Normal ConsoleKeyInfo (from ReadKey or keyboard input).</summary>
        public ConsoleKeyInfo? KeyInfo { get; init; }
        /// <summary>Mouse wheel scroll delta. Positive = up, negative = down.</summary>
        public int ScrollDelta { get; init; }
        /// <summary>Mouse button click position (row, col).</summary>
        public (int row, int col)? ClickPosition { get; init; }
        /// <summary>True if this was a mouse button release (for click tracking).</summary>
        public bool IsRelease { get; init; }
        /// <summary>The raw unhandled sequence bytes (for fallback).</summary>
        public string? Raw { get; init; }
    }

    /// <summary>
    /// Read the next input event — either a normal keystroke, a mouse event, or a keyboard protocol sequence.
    /// Blocks until input is available.
    /// </summary>
    public static InputEvent ReadNext(CancellationToken ct = default)
    {
        var stdin = Console.OpenStandardInput();
        var buf = new byte[32];
        var offset = 0;

        while (!ct.IsCancellationRequested)
        {
            int b;
            try { b = stdin.ReadByte(); }
            catch { break; }
            if (b < 0) break;
            buf[offset++] = (byte)b;

            // ESC sequence start
            if (b == 0x1b && offset == 1) continue;
            // Possible CSI sequence: ESC [
            if (b == '[' && offset == 2 && buf[0] == 0x1b) continue;
            // SGR mouse: ESC [ < C ; r ; c m/M
            if (b == '<' && offset == 3 && buf[0] == 0x1b && buf[1] == '[')
            {
                // Read until 'm' or 'M'
                var payload = new StringBuilder();
                while (!ct.IsCancellationRequested)
                {
                    var ch = stdin.ReadByte();
                    if (ch < 0) break;
                    buf[offset++] = (byte)ch;
                    if (ch == 'm' || ch == 'M') break;
                }
                var seq = Encoding.UTF8.GetString(buf, 0, offset);
                var parts = seq.TrimEnd('m', 'M').TrimStart('\x1b', '[', '<').Split(';');
                if (parts.Length >= 3
                    && int.TryParse(parts[0], out var btn)
                    && int.TryParse(parts[1], out var col)
                    && int.TryParse(parts[2], out var row))
                {
                    var isRelease = seq.EndsWith("m");
                    // Wheel events: btn 64 = wheel up, btn 65 = wheel down
                    if (btn == 64 || btn == 65)
                        return new InputEvent { ScrollDelta = btn == 64 ? 3 : -3 };
                    // Button click
                    if (!isRelease)
                        return new InputEvent { ClickPosition = (row, col), IsRelease = false };
                    return new InputEvent { IsRelease = true };
                }
                return new InputEvent { Raw = seq };
            }
            // Kitty keyboard protocol: ESC [ N ; modifiers u
            if (b == 'u' && offset >= 4 && buf[0] == 0x1b && buf[1] == '[')
            {
                var seq = Encoding.UTF8.GetString(buf, 0, offset);
                return new InputEvent { Raw = seq };
            }

            // If we have a complete ordinary key (single byte or two-byte UTF-8)
            if (offset >= 1 && buf[0] != 0x1b && b < 0x80)
            {
                var keyInfo = MapByteToKeyInfo(buf[0]);
                if (keyInfo.HasValue) return new InputEvent { KeyInfo = keyInfo.Value };
                return new InputEvent { Raw = Encoding.UTF8.GetString(buf, 0, offset) };
            }

            // Finished a short VT sequence that isn't mouse
            if (offset == 2 && buf[0] == 0x1b && b is >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A)
            {
                var ch = (char)(b + 0x20); // Alt+key
                return new InputEvent { KeyInfo = new ConsoleKeyInfo(ch, (ConsoleKey)b, false, false, true) };
            }
        }
        return new InputEvent { Raw = "" };
    }

    private static ConsoleKeyInfo? MapByteToKeyInfo(int b)
    {
        if (b is >= 0x20 and <= 0x7E)
        {
            var ch = (char)b;
            var ck = (ConsoleKey)(b >= 0x61 ? b - 0x20 : b); // approximate
            return new ConsoleKeyInfo(ch, ck, b >= 0x41 && b <= 0x5A, false, false);
        }
        if (b == 0x0d) return new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);
        if (b == 0x7f) return new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false);
        if (b == 0x09) return new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false);
        if (b == 0x1b) return new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false);
        return null;
    }

    /// <summary>Try to parse a Kitty keyboard protocol sequence into a ConsoleKeyInfo.
    /// Format: ESC [ code ; modifiers u</summary>
    public static ConsoleKeyInfo? ParseKittySequence(string seq)
    {
        // TODO: full kitty protocol decode if needed
        return null;
    }

    /// <summary>Apply scroll with inertia to a scroll offset.</summary>
    public static int ApplyScrollInertia(int current, int delta, int max, out float velocity)
    {
        velocity = delta * 0.5f;
        return Math.Clamp(current - delta, 0, Math.Max(0, max - 1));
    }

    /// <summary>Decay velocity by friction and produce the final scroll delta for this frame.</summary>
    public static float DecayVelocity(float velocity, float friction = 0.85f)
    {
        if (Math.Abs(velocity) < 0.5f) return 0;
        return velocity * friction;
    }
}

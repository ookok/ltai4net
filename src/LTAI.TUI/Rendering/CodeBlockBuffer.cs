using System.Collections.Concurrent;

namespace LTAI.TUI.Rendering;

public static class CodeBlockBuffer
{
    private static readonly ConcurrentQueue<(string code, string? lang)> _blocks = new();
    private const int MaxBlocks = 20;

    public static void Register(string code, string? lang)
    {
        _blocks.Enqueue((code, lang));
        while (_blocks.Count > MaxBlocks && _blocks.TryDequeue(out _)) { }
    }

    public static (string code, string? lang)? PopLatest()
    {
        if (_blocks.TryDequeue(out var block))
            return block;
        return null;
    }

    public static (string code, string? lang)? PeekLatest()
    {
        if (_blocks.TryPeek(out var block))
            return block;
        return null;
    }

    public static bool TryCopyLatestToClipboard()
    {
        if (!_blocks.TryPeek(out var latest)) return false;
        try
        {
            var text = latest.code;
            // Use TextCopy if available, otherwise fallback
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-Command \"Set-Clipboard -Value '{text.Replace("'", "''")}'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(1000);
            return true;
        }
        catch { return false; }
    }
}

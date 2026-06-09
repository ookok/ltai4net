using System.Collections.Concurrent;
using System.Net;
using System.Text;
using SixLabors.ImageSharp;
using Spectre.Console;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace LTAI.TUI.Rendering;

/// <summary>
/// Downloads images from URLs and renders them as ANSI/Unicode block art
/// for inline terminal display. Works on any terminal.
/// </summary>
public sealed class ImageRenderer
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly string _cacheDir = Path.Combine(
        AppContext.BaseDirectory, ".livingtree", "images");

    private const int MaxRenderWidth = 60;
    private const int MaxRenderHeight = 30;

    // Image-level cache: url → rendered Spectre markup string
    private static readonly ConcurrentDictionary<string, string> _imageMarkupCache = new();

    // Track in-flight downloads to avoid duplicates
    private static readonly ConcurrentDictionary<string, Task> _downloadsInFlight = new();

    static ImageRenderer()
    {
        try { Directory.CreateDirectory(_cacheDir); } catch { }
    }

    /// <summary>
    /// Get the Spectre markup string for an image URL.
    /// Returns cached markup if available, otherwise a placeholder and starts background download.
    /// </summary>
    public static string GetImageMarkup(string url, string? alt = null)
    {
        if (_imageMarkupCache.TryGetValue(url, out var cached))
            return cached;

        // Show placeholder and trigger background download
        _ = DownloadAndCacheAsync(url);
        var altText = alt ?? url;
        if (altText.Length > 60) altText = altText[..60] + "...";
        return $"[grey][🖼 {altText.EscapeMarkup()}][/]";
    }

    public static void ClearCache()
    {
        _imageMarkupCache.Clear();
    }

    public static bool IsCached(string url) => _imageMarkupCache.ContainsKey(url);

    private static async Task DownloadAndCacheAsync(string url)
    {
        // Deduplicate in-flight downloads
        if (_downloadsInFlight.TryGetValue(url, out var existing))
        {
            await existing.ConfigureAwait(false);
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_downloadsInFlight.TryAdd(url, tcs.Task))
        {
            var task = _downloadsInFlight[url];
            await task.ConfigureAwait(false);
            return;
        }

        try
        {
            var cacheKey = WebUtility.UrlEncode(url);
            var cachePath = Path.Combine(_cacheDir, cacheKey + ".png");

            byte[] imageData;
            if (File.Exists(cachePath))
            {
                imageData = await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
            }
            else
            {
                using var resp = await _http.GetAsync(url).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                imageData = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                _ = PersistCacheAsync(cachePath, imageData);
            }

            var markup = RenderImageToMarkup(imageData);
            _imageMarkupCache[url] = markup;
            tcs.TrySetResult();
        }
        catch
        {
            tcs.TrySetResult(); // Don't cache on failure, retry next time
        }
        finally
        {
            _downloadsInFlight.TryRemove(url, out _);
        }
    }

    private static async Task PersistCacheAsync(string path, byte[] data)
    {
        try { await File.WriteAllBytesAsync(path, data).ConfigureAwait(false); }
        catch { }
    }

    internal static string RenderImageToMarkup(byte[] imageData)
    {
        using var img = Image.Load<Rgba32>(imageData);
        return ImageToSpectreMarkup(img);
    }

    private static string ImageToSpectreMarkup(Image<Rgba32> img)
    {
        var termWidth = 80;
        try { termWidth = Console.WindowWidth; } catch { }

        var renderW = Math.Min(MaxRenderWidth, termWidth - 8);
        if (renderW < 8) return ""; // terminal too narrow

        var renderH = Math.Min(MaxRenderHeight, (int)(img.Height * renderW / (double)img.Width / 2));
        if (renderH < 1) renderH = 1;

        img.Mutate(x => x.Resize(renderW, renderH * 2));

        var sb = new StringBuilder();
        sb.AppendLine($"[grey]┌{new string('─', renderW)}┐[/]");
        for (int row = 0; row < renderH; row++)
        {
            sb.Append("[grey]│[/]");
            for (int col = 0; col < renderW; col++)
            {
                var top = img[col, row * 2];
                var bot = img[col, Math.Min(row * 2 + 1, img.Height - 1)];
                var topHex = RgbToHex(top);
                var botHex = RgbToHex(bot);

                if (top.A < 128 && bot.A < 128)
                    sb.Append(' ');
                else if (top.A < 128)
                    sb.Append($"[{botHex}]▄[/]");
                else if (bot.A < 128)
                    sb.Append($"[{topHex}]▀[/]");
                else
                    sb.Append($"[{topHex} on {botHex}]▀[/]");
            }
            sb.AppendLine("[grey]│[/]");
        }
        sb.Append($"[grey]└{new string('─', renderW)}┘[/]");
        return sb.ToString();
    }

    private static string RgbToHex(Rgba32 c) =>
        $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

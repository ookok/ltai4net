using LTAI.Infra.Browser.Models;
using HtmlAgilityPack;

namespace LTAI.Infra.Browser;

public static class AdaptiveExtractor
{
    private static readonly string[] XPathSelectors =
    {
        "//table//tr",
        "//ul//li",
        "//ol//li",
        "//*[contains(@class,'item') or contains(@class,'Item')]",
        "//*[contains(@class,'list') or contains(@class,'List')]",
        "//*[contains(@class,'row') or contains(@class,'Row')]",
        "//*[contains(@class,'article') or contains(@class,'Article')]",
        "//*[contains(@class,'result') or contains(@class,'Result')]",
        "//*[contains(@class,'content') or contains(@class,'Content')]",
        "//*[contains(@class,'entry') or contains(@class,'Entry')]",
        "//*[contains(@class,'post') or contains(@class,'Post')]"
    };

    private static readonly string[] BlockedKeywords =
    {
        "云防御", "拦截", "captcha", "blocked", "访问受限", "请验证"
    };

    public static async Task<List<Dictionary<string, object?>>> ExtractAsync(
        Microsoft.Playwright.IPage page,
        string task)
    {
        var html = await page.ContentAsync();
        return ExtractFromHtml(html, task);
    }

    public static List<Dictionary<string, object?>> ExtractFromHtml(
        string html,
        string task)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        if (IsBlockedPage(doc))
            return new List<Dictionary<string, object?>>
            {
                new() { ["error"] = "Page appears to be blocked or requires verification" }
            };

        foreach (var xpath in XPathSelectors)
        {
            var nodes = doc.DocumentNode.SelectNodes(xpath);
            if (nodes != null && nodes.Count >= 3)
            {
                return nodes.Take(100).Select((n, i) => new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["text"] = n.InnerText.Trim(),
                    ["html"] = n.OuterHtml.Length <= 2000 ? n.OuterHtml : n.OuterHtml[..2000],
                    ["selector"] = xpath
                }).ToList();
            }
        }

        var bodyText = doc.DocumentNode.InnerText.Trim();
        if (!string.IsNullOrEmpty(bodyText))
        {
            var chunks = ChunkText(bodyText, 2000);
            return chunks.Select((t, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["text"] = t,
                ["selector"] = "body"
            }).ToList();
        }

        return new List<Dictionary<string, object?>>
        {
            new() { ["text"] = bodyText[..Math.Min(bodyText.Length, 5000)] }
        };
    }

    public static List<Dictionary<string, object?>> SearchByKeyword(
        string html,
        string keyword)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<Dictionary<string, object?>>();
        var nodes = doc.DocumentNode.Descendants()
            .Where(n => n.InnerText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(20);

        foreach (var node in nodes)
        {
            var parent = FindNearestContainer(node);
            results.Add(new Dictionary<string, object?>
            {
                ["text"] = parent?.InnerText.Trim() ?? node.InnerText.Trim(),
                ["html"] = parent?.OuterHtml ?? node.OuterHtml,
                ["keyword"] = keyword
            });
        }

        return results;
    }

    public static List<string> ExtractDownloadLinks(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var extensions = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".zip", ".rar", ".7z", ".ppt", ".pptx" };
        var links = doc.DocumentNode.SelectNodes("//a[@href]");
        var downloadLinks = new List<string>();

        if (links != null)
        {
            foreach (var link in links)
            {
                var href = link.GetAttributeValue("href", "");
                if (string.IsNullOrEmpty(href))
                    continue;

                foreach (var ext in extensions)
                {
                    if (href.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadLinks.Add(href);
                        break;
                    }
                }
            }
        }

        return downloadLinks;
    }

    private static bool IsBlockedPage(HtmlDocument doc)
    {
        var text = doc.DocumentNode.InnerText;
        return BlockedKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static HtmlNode? FindNearestContainer(HtmlNode node)
    {
        var root = node.OwnerDocument.DocumentNode;
        var current = node;
        while (current != null && current != root)
        {
            var name = current.Name.ToLowerInvariant();
            if (name is "div" or "section" or "article" or "li" or "tr" or "td")
            {
                var text = current.InnerText.Trim();
                if (text.Length > 20)
                    return current;
            }
            current = current.ParentNode;
        }
        return node.ParentNode;
    }

    private static List<string> ChunkText(string text, int chunkSize)
    {
        var chunks = new List<string>();
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var len = Math.Min(chunkSize, text.Length - i);
            chunks.Add(text.Substring(i, len).Trim());
        }
        return chunks;
    }
}

using LTAI.Browser;
using LTAI.Browser.Models;
using Xunit;

namespace LTAI.Browser.Tests;

public class AdaptiveExtractorTests
{
    [Fact]
    public void ExtractFromSimpleHtml_ReturnsItems()
    {
        var html = @"
<html><body>
<ul>
  <li>Item 1: Hello</li>
  <li>Item 2: World</li>
  <li>Item 3: Test</li>
  <li>Item 4: Data</li>
</ul>
</body></html>";

        var items = AdaptiveExtractor.ExtractFromHtml(html, "extract items");
        Assert.NotEmpty(items);
        Assert.True(items.Count >= 3);
    }

    [Fact]
    public void ExtractFromTable_ReturnsRows()
    {
        var html = @"
<html><body>
<table>
  <tr><td>A</td><td>1</td></tr>
  <tr><td>B</td><td>2</td></tr>
  <tr><td>C</td><td>3</td></tr>
  <tr><td>D</td><td>4</td></tr>
</table>
</body></html>";

        var items = AdaptiveExtractor.ExtractFromHtml(html, "extract table");
        Assert.NotEmpty(items);
        Assert.True(items.Count >= 4);
    }

    [Fact]
    public void ExtractWithBlockedKeywords_ReturnsBlocked()
    {
        var html = "<html><body>请验证 you are not a robot</body></html>";
        var items = AdaptiveExtractor.ExtractFromHtml(html, "extract");
        Assert.Single(items);
        Assert.Contains("error", items[0].Keys);
    }

    [Fact]
    public void ExtractFromEmptyHtml_ReturnsBodyText()
    {
        var html = "<html><body>Just some plain text content for testing purposes</body></html>";
        var items = AdaptiveExtractor.ExtractFromHtml(html, "extract");
        Assert.NotEmpty(items);
    }

    [Fact]
    public void SearchByKeyword_FindsMatches()
    {
        var html = @"
<html><body>
<div>This is about machine learning</div>
<div>This is about deep learning neural networks</div>
<div>This is about cooking recipes</div>
</body></html>";

        var results = AdaptiveExtractor.SearchByKeyword(html, "learning");
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Contains("learning", (string?)r["text"] ?? ""));
    }

    [Fact]
    public void ExtractDownloadLinks_FindsSupportedExtensions()
    {
        var html = @"
<html><body>
<a href='report.pdf'>Report</a>
<a href='data.xlsx'>Data</a>
<a href='image.png'>Image</a>
<a href='archive.zip'>Archive</a>
</body></html>";

        var links = AdaptiveExtractor.ExtractDownloadLinks(html);
        Assert.Equal(3, links.Count);
        Assert.Contains("report.pdf", links);
        Assert.Contains("data.xlsx", links);
        Assert.Contains("archive.zip", links);
    }
}

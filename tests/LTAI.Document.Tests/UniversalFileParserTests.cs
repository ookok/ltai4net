using System.Text.Json;
using LTAI.Document;
using LTAI.Document.Interfaces;
using LTAI.Document.Parsers;
using Xunit;

namespace LTAI.Document.Tests;

public class UniversalFileParserTests
{
    [Fact]
    public void DetectFormat_Jpeg_ReturnsJpeg()
    {
        var tempFile = Path.GetTempFileName() + ".jpg";
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 });
            var parser = CreateParser();
            var format = parser.DetectFormat(tempFile);
            Assert.Equal("jpeg", format);
        }
        finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
    }

    [Fact]
    public void DetectFormat_Png_ReturnsPng()
    {
        var tempFile = Path.GetTempFileName() + ".png";
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var parser = CreateParser();
            var format = parser.DetectFormat(tempFile);
            Assert.Equal("png", format);
        }
        finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
    }

    [Fact]
    public void DetectFormat_Pdf_ReturnsPdf()
    {
        var tempFile = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0x25, 0x50, 0x44, 0x46 });
            var parser = CreateParser();
            var format = parser.DetectFormat(tempFile);
            Assert.Equal("pdf", format);
        }
        finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
    }

    [Fact]
    public void DetectFormat_Zip_ReturnsZip()
    {
        var tempFile = Path.GetTempFileName() + ".zip";
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
            var parser = CreateParser();
            var format = parser.DetectFormat(tempFile);
            Assert.Equal("zip", format);
        }
        finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
    }

    [Fact]
    public void DetectFormat_Sqlite_ReturnsSqlite()
    {
        var tempFile = Path.GetTempFileName() + ".db";
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66 });
            var parser = CreateParser();
            var format = parser.DetectFormat(tempFile);
            Assert.Equal("sqlite", format);
        }
        finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
    }

    [Fact]
    public void DetectFormat_Unknown_ReturnsExtension()
    {
        var tempFile = Path.GetTempFileName() + ".xyz";
        try
        {
            File.WriteAllText(tempFile, "random data");
            var parser = CreateParser();
            var format = parser.DetectFormat(tempFile);
            Assert.Equal("xyz", format);
        }
        finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
    }

    [Fact]
    public async Task ParseJsonFile_Success()
    {
        var tempFile = Path.GetTempFileName() + ".json";
        try
        {
            var json = JsonSerializer.Serialize(new { name = "test", value = 42 });
            await File.WriteAllTextAsync(tempFile, json);
            var parser = CreateParser();
            var result = await parser.ParseAsync(tempFile);
            Assert.True(result.Success);
            Assert.Equal("json", result.Format);
            Assert.Contains("test", result.Text);
        }
        finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
    }

    [Fact]
    public async Task ParseCsvFile_Success()
    {
        var tempFile = Path.GetTempFileName() + ".csv";
        try
        {
            await File.WriteAllTextAsync(tempFile, "name,age,city\nAlice,30,NYC\nBob,25,LA");
            var parser = CreateParser();
            var result = await parser.ParseAsync(tempFile);
            Assert.True(result.Success);
            Assert.Equal("csv", result.Format);
            Assert.NotEmpty(result.Tables);
            Assert.Equal(2, result.Tables.Count);
        }
        finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
    }

    [Fact]
    public async Task ParseNonExistentFile_ReturnsFail()
    {
        var parser = CreateParser();
        var result = await parser.ParseAsync("/nonexistent/file.txt");
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public void ListParsers_ReturnsAvailableParsers()
    {
        var parser = CreateParser();
        var parsers = parser.ListParsers();
        Assert.NotEmpty(parsers);
    }

    private static UniversalFileParser CreateParser()
    {
        var parsers = new IDocumentParser[]
        {
            new JsonParser(),
            new XmlParser(),
            new CsvParser(),
            new TextParser()
        };
        return new UniversalFileParser(parsers);
    }
}

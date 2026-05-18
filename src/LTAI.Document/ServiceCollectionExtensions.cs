using LTAI.Document.Interfaces;
using LTAI.Document.Parsers;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Document;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIDocument(this IServiceCollection services)
    {
        services.AddSingleton<IDocumentParser, JsonParser>();
        services.AddSingleton<IDocumentParser, XmlParser>();
        services.AddSingleton<IDocumentParser, CsvParser>();
        services.AddSingleton<IDocumentParser, TextParser>();
        services.AddSingleton<IDocumentParser, MarkdownParser>();
        services.AddSingleton<IDocumentParser, IniConfigParser>();
        services.AddSingleton<IDocumentParser, YamlTomlParser>();
        services.AddSingleton<IDocumentParser, HtmlTextParser>();
        services.AddSingleton<IDocumentParser, LogParser>();
        services.AddSingleton<UniversalFileParser>();
        return services;
    }
}

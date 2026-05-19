using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Templates;

public static class TemplatesExtensions
{
    public static IServiceCollection AddLTAITemplates(this IServiceCollection services)
    {
        services.AddRazorPages();
        return services;
    }

    public static WebApplication MapLTAITemplates(this WebApplication app)
    {
        app.MapRazorPages();
        return app;
    }
}

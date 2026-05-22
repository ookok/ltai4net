using LTAI.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Core;

public sealed class LTAIHostBuilder
{
    private readonly IServiceCollection _services;
    private readonly IConfiguration _configuration;

    public IServiceCollection Services => _services;
    public IConfiguration Configuration => _configuration;

    private LTAIHostBuilder(IServiceCollection services, IConfiguration configuration)
    {
        _services = services;
        _configuration = configuration;
    }

    public static LTAIHostBuilder Create(IServiceCollection services, IConfiguration configuration)
        => new(services, configuration);

    public static LTAIHostBuilder CreateEmpty()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        return new LTAIHostBuilder(services, config);
    }

    public LTAIHostBuilder ConfigureLTAIOptions(string sectionName = LTAIOptions.SectionName)
    {
        var section = _configuration.GetSection(sectionName);
        _services.Configure<LTAIOptions>(section);

        return this;
    }

    public LTAIHostBuilder AddCoreLogging()
    {
        _services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddConsole());
        return this;
    }

    public LTAIHostBuilder WithProfile(string profile)
    {
        _services.AddSingleton(new HostProfile(profile));
        return this;
    }

    public T Build<T>() where T : notnull
    {
        var sp = _services.BuildServiceProvider();
        return sp.GetRequiredService<T>();
    }
}

public sealed record HostProfile(string Name)
{
    public static readonly HostProfile WebApi = new("webapi");
    public static readonly HostProfile Tui = new("tui");
    public static readonly HostProfile Mcp = new("mcp");
    public static readonly HostProfile Desktop = new("desktop");
    public static readonly HostProfile WebApp = new("webapp");
}

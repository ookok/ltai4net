using LTAI.Tools.General;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Tools;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAITools(this IServiceCollection services)
    {
        foreach (var tool in CreateFileSystemTools())
            services.AddSingleton(tool);
        foreach (var tool in CreateHttpTools())
            services.AddSingleton(tool);
        foreach (var tool in CreateMathTools())
            services.AddSingleton(tool);
        foreach (var tool in CreateShellTools())
            services.AddSingleton(tool);
        return services;
    }

    public static AITool[] CreateFileSystemTools() =>
    [
        AIFunctionFactory.Create(FileSystemTools.ReadFileAsync, nameof(FileSystemTools.ReadFileAsync)),
        AIFunctionFactory.Create(FileSystemTools.WriteFileAsync, nameof(FileSystemTools.WriteFileAsync)),
        AIFunctionFactory.Create(FileSystemTools.ListDirectory, nameof(FileSystemTools.ListDirectory)),
        AIFunctionFactory.Create(FileSystemTools.Exists, nameof(FileSystemTools.Exists)),
        AIFunctionFactory.Create(FileSystemTools.DeleteFile, nameof(FileSystemTools.DeleteFile)),
        AIFunctionFactory.Create(FileSystemTools.GetMetadata, nameof(FileSystemTools.GetMetadata)),
    ];

    public static AITool[] CreateHttpTools() =>
    [
        AIFunctionFactory.Create(HttpTools.FetchAsync, nameof(HttpTools.FetchAsync)),
        AIFunctionFactory.Create(HttpTools.PostJsonAsync, nameof(HttpTools.PostJsonAsync)),
    ];

    public static AITool[] CreateMathTools() =>
    [
        AIFunctionFactory.Create(MathTools.Evaluate, nameof(MathTools.Evaluate)),
        AIFunctionFactory.Create(MathTools.BasicMath, nameof(MathTools.BasicMath)),
        AIFunctionFactory.Create(MathTools.Random, nameof(MathTools.Random)),
        AIFunctionFactory.Create(MathTools.Convert, nameof(MathTools.Convert)),
    ];

    public static AITool[] CreateShellTools() =>
    [
        AIFunctionFactory.Create(ShellTools.ExecuteAsync, nameof(ShellTools.ExecuteAsync)),
        AIFunctionFactory.Create(ShellTools.GetWorkingDirectory, nameof(ShellTools.GetWorkingDirectory)),
        AIFunctionFactory.Create(ShellTools.GetEnvironmentInfo, nameof(ShellTools.GetEnvironmentInfo)),
        AIFunctionFactory.Create(ShellTools.GetEnvironmentVariable, nameof(ShellTools.GetEnvironmentVariable)),
    ];
}

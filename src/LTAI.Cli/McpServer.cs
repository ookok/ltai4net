// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  LTAI.McpServer — Expose LTAI read-only tools via Model Context
//  Protocol (stdio transport) so external IDEs (VS Code, Cursor,
//  Claude Desktop) can invoke them as MCP tools.
//
//  Scope: READ-ONLY tools only. File writing, shell execution, and
//  any state-mutating operations are intentionally NOT exposed.
//  External clients must treat LTAI as a trusted observer, not a
//  remote execution surface.
//
//  Transport: stdio. Launched by the host IDE as a child process.
//  Discovery: clients run `ltai mcp-server` and the process speaks
//  JSON-RPC over its stdin/stdout.
//
//  Tools exposed:
//    read_file, list_files, glob, directory_tree, file_info
//    search_content, search_files, regex_test
// ═══════════════════════════════════════════════════════════════

using LTAI.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LTAI.CLI;

public static class McpServer
{
    /// <summary>
    /// Start an MCP server on stdio, exposing the configured read-only
    /// toolset. Returns when the parent process closes stdin (typical
    /// for an IDE-managed child process).
    /// </summary>
    public static async Task<int> RunAsync(string workspace)
    {
        var builder = Host.CreateApplicationBuilder();

        // Critical for stdio: any provider that writes to stdout will
        // corrupt the JSON-RPC channel. Clear all default providers.
        builder.Logging.ClearProviders();

        var fs = new FileSystemTools(workspace);
        var search = new SearchTools(workspace);

        var aiTools = new (string Name, AIFunction Function)[]
        {
            ("read_file",      AIFunctionFactory.Create(fs.ReadFileContent, "read_file", "Read a text file (UTF-8) by path.")),
            ("list_files",     AIFunctionFactory.Create(fs.ListFiles, "list_files", "List files in a directory.")),
            ("glob",           AIFunctionFactory.Create(fs.Glob, "glob", "Match files by glob pattern.")),
            ("directory_tree", AIFunctionFactory.Create(fs.DirectoryTree, "directory_tree", "Recursive directory listing with depth limit.")),
            ("file_info",      AIFunctionFactory.Create(fs.GetFileInfo, "file_info", "Get file metadata (size, mtime, perms).")),
            ("search_content", AIFunctionFactory.Create(search.SearchContent, "search_content", "Regex search inside files (ripgrep semantics).")),
            ("search_files",   AIFunctionFactory.Create(search.SearchFiles, "search_files", "Filename search by name pattern.")),
            ("regex_test",     AIFunctionFactory.Create(TextTools.RegexTest, "regex_test", "Test a regex pattern against input.")),
        };

        var mcpTools = aiTools.Select(t => McpServerTool.Create(t.Function)).ToArray();

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "ltai-readonly",
                    Version = "1.0.0",
                };
            })
            .WithStdioServerTransport()
            .WithTools(mcpTools);

        var app = builder.Build();
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }
}

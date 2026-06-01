// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  MemoryProviderSelector — Pick the right AIContextProvider for
//  cross-session long-term memory.
//
//  Priority:
//    1. If MEM0_API_KEY env var is set → MAF Mem0Provider (remote)
//    2. Else → EmbeddedMemoryProvider (local SQLite + embedding)
//
//  Both providers implement the same AIContextProvider contract:
//    - Before invocation: search top-K memories → inject as user msg
//    - After invocation: persist request + response messages
// ═══════════════════════════════════════════════════════════════

using System.Net.Http.Headers;
using LTAI.AI;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Mem0;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

public static class MemoryProviderSelector
{
    public static AIContextProvider Select(
        EmbeddingClient embedder,
        string dataDir,
        ILoggerFactory? loggerFactory)
    {
        var mem0Key = SecretManager.Get("MEM0_API_KEY");
        if (!string.IsNullOrEmpty(mem0Key))
        {
            try
            {
                var http = new HttpClient { BaseAddress = new Uri("https://api.mem0.ai") };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", mem0Key);

                var scope = new Mem0ProviderScope { ApplicationId = "ltai", UserId = "default" };
                var provider = new Mem0Provider(
                    http,
                    stateInitializer: _ => new Microsoft.Agents.AI.Mem0.Mem0Provider.State(scope),
                    loggerFactory: loggerFactory);
                loggerFactory?.CreateLogger("Memory").LogInformation(
                    "MemoryProviderSelector: using MAF Mem0Provider (remote api.mem0.ai)");
                return provider;
            }
            catch (Exception ex)
            {
                loggerFactory?.CreateLogger("Memory").LogWarning(ex,
                    "MemoryProviderSelector: Mem0 init failed, falling back to local EmbeddedMemoryProvider");
            }
        }

        var dbPath = Path.Combine(dataDir, "memory.db");
        loggerFactory?.CreateLogger("Memory").LogInformation(
            "MemoryProviderSelector: using local EmbeddedMemoryProvider (db={Path})", dbPath);
        return new EmbeddedMemoryProvider(embedder, dbPath,
            logger: loggerFactory?.CreateLogger<EmbeddedMemoryProvider>());
    }
}

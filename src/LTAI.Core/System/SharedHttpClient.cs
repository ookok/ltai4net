using System.Net;
using System.Net.Http;

namespace LTAI.Core.System;

/// <summary>
/// Single shared HttpClient instance for the entire process.
/// Using one HttpClient with a pooled SocketsHttpHandler avoids the
/// 20+ separate connection-pool problem caused by scattered
/// static readonly HttpClient fields across the codebase.
///
/// Use this in static tool classes that cannot accept IHttpClientFactory
/// injection due to AIFunctionFactory.Create delegate constraints.
/// For injectable services, prefer IHttpClientFactory.
/// </summary>
public static class SharedHttpClient
{
    /// <summary>
    /// A single HttpClient with pooled connections, 30s default timeout,
    /// automatic decompression, and keep-alive enabled.
    /// </summary>
    public static readonly HttpClient Instance = new(
        new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 10,
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All,
        })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
}

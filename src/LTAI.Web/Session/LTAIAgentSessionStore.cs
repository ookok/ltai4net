// Copyright (c) LTAI. All rights reserved.

using System.Text.Json;
using LTAI.Core.Session;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Web.Session;

/// <summary>
/// Bridges LTAI's <see cref="SessionManager"/> (AES-encrypted file persistence)
/// into MAF's <see cref="AgentSessionStore"/> abstraction. Enables A2A and AGUI
/// protocol endpoints to persist agent conversations across process restarts.
///
/// The <c>conversationId</c> from A2A (RequestContext.ContextId) / AGUI (input.ThreadId)
/// maps directly to LTAI session filenames in .livingtree/sessions/.
/// </summary>
public sealed class LTAIAgentSessionStore : AgentSessionStore
{
    private readonly SessionManager _sessionManager;
    private readonly ILogger<LTAIAgentSessionStore> _logger;

    public LTAIAgentSessionStore(SessionManager sessionManager, ILogger<LTAIAgentSessionStore> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public override async ValueTask SaveSessionAsync(
        AIAgent agent, string conversationId, AgentSession session, CancellationToken ct)
    {
        try
        {
            var jsonElement = await agent.SerializeSessionAsync(session, cancellationToken: ct)
                .ConfigureAwait(false);

            var handle = new JsonSessionHandle(conversationId, jsonElement);
            await _sessionManager.SaveSessionAsync(handle).ConfigureAwait(false);

            _logger.LogDebug("A2A session {ConversationId} saved ({Bytes} bytes)",
                conversationId, jsonElement.GetRawText().Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save A2A session {ConversationId}", conversationId);
        }
    }

    public override async ValueTask<AgentSession> GetSessionAsync(
        AIAgent agent, string conversationId, CancellationToken ct)
    {
        try
        {
            var handle = await _sessionManager.LoadSessionAsync(conversationId).ConfigureAwait(false);
            if (handle == null)
            {
                _logger.LogDebug("A2A session {ConversationId} not found, creating new", conversationId);
                return await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            }

            var json = handle.SerializeToJson();
            if (string.IsNullOrEmpty(json))
            {
                _logger.LogDebug("A2A session {ConversationId} empty, creating new", conversationId);
                return await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            }

            var element = JsonDocument.Parse(json).RootElement;
            var session = await agent.DeserializeSessionAsync(element, cancellationToken: ct)
                .ConfigureAwait(false);

            _logger.LogDebug("A2A session {ConversationId} restored ({Bytes} bytes)",
                conversationId, json.Length);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore A2A session {ConversationId}, creating new",
                conversationId);
            return await agent.CreateSessionAsync(ct).ConfigureAwait(false);
        }
    }
}

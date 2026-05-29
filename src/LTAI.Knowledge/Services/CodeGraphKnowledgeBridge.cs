using LTAI.Knowledge.Core;
using LTAI.Tools.CodeGraph;

namespace LTAI.Knowledge.Services;

/// <summary>
/// Bridges CodeGraphEnhanced → KnowledgeGraph during indexing.
/// Syncs code nodes/edges as knowledge graph entities/relations,
/// enabling unified queries across code and document knowledge.
/// </summary>
public sealed class CodeGraphKnowledgeBridge
{
    private readonly CodeGraphEnhanced _codeGraph;
    private readonly KnowledgeGraph _knowledgeGraph;
    private int _syncedNodes;
    private int _syncedEdges;

    public CodeGraphKnowledgeBridge(CodeGraphEnhanced codeGraph, KnowledgeGraph knowledgeGraph)
    {
        _codeGraph = codeGraph;
        _knowledgeGraph = knowledgeGraph;
    }

    public (int Nodes, int Edges) SyncToKnowledgeGraph()
    {
        _syncedNodes = 0;
        _syncedEdges = 0;

        foreach (var node in _codeGraph.GetAllNodes())
        {
            var kgId = $"code:{node.Id}";
            _knowledgeGraph.AddEntity(new LTAI.Knowledge.Core.Models.Entity(
                kgId,
                node.Name,
                new Dictionary<string, object>
                {
                    ["kind"] = node.Kind ?? "",
                    ["file"] = node.File ?? "",
                    ["line"] = node.Line.ToString(),
                    ["fingerprint"] = node.Fingerprint.ToString()
                }));
            _syncedNodes++;
        }

        foreach (var edge in _codeGraph.GetAllEdges())
        {
            var sourceId = $"code:{edge.SourceId}";
            var targetId = $"code:{edge.TargetId}";
            _knowledgeGraph.AddRelation(sourceId, targetId, edge.Relation ?? "calls");
            _syncedEdges++;
        }

        _knowledgeGraph.GetStats();

        return (_syncedNodes, _syncedEdges);
    }
}

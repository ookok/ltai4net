using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace LTAI.TreeLLM.Session;

public sealed class StreamingGrammarGuard
{
    private int _braceDepth;
    private int _bracketDepth;
    private int _parenDepth;
    private int _jsonDepth;
    private bool _inString;
    private char _stringQuote;
    private bool _escapeNext;
    private int _totalChunks;
    private readonly List<char> _openStack = new();

    public bool IsBalanced => _braceDepth == 0 && _bracketDepth == 0 && _parenDepth == 0 && !_inString;
    public int CurrentDepth => _braceDepth + _bracketDepth + _parenDepth + _jsonDepth;
    public int RepairsApplied { get; private set; }
    public int ChunksProcessed => _totalChunks;

    public void Reset()
    {
        _braceDepth = 0;
        _bracketDepth = 0;
        _parenDepth = 0;
        _jsonDepth = 0;
        _inString = false;
        _stringQuote = '\0';
        _escapeNext = false;
        _totalChunks = 0;
        _openStack.Clear();
        RepairsApplied = 0;
    }

    public string ProcessChunk(string chunk)
    {
        _totalChunks++;
        for (var i = 0; i < chunk.Length; i++)
        {
            var c = chunk[i];

            if (_escapeNext)
            {
                _escapeNext = false;
                continue;
            }

            if (_inString)
            {
                if (c == '\\')
                    _escapeNext = true;
                else if (c == _stringQuote)
                    _inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    _inString = true;
                    _stringQuote = c;
                    break;
                case '{':
                    _braceDepth++;
                    _jsonDepth++;
                    _openStack.Add('}');
                    break;
                case '}':
                    if (_braceDepth > 0) _braceDepth--;
                    if (_jsonDepth > 0) _jsonDepth--;
                    if (_openStack.Count > 0 && _openStack[^1] == '}')
                        _openStack.RemoveAt(_openStack.Count - 1);
                    break;
                case '[':
                    _bracketDepth++;
                    _jsonDepth++;
                    _openStack.Add(']');
                    break;
                case ']':
                    if (_bracketDepth > 0) _bracketDepth--;
                    if (_jsonDepth > 0) _jsonDepth--;
                    if (_openStack.Count > 0 && _openStack[^1] == ']')
                        _openStack.RemoveAt(_openStack.Count - 1);
                    break;
                case '(':
                    _parenDepth++;
                    _openStack.Add(')');
                    break;
                case ')':
                    if (_parenDepth > 0) _parenDepth--;
                    if (_openStack.Count > 0 && _openStack[^1] == ')')
                        _openStack.RemoveAt(_openStack.Count - 1);
                    break;
            }
        }

        return chunk;
    }

    public async IAsyncEnumerable<string> GuardStreamAsync(
        IAsyncEnumerable<string> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Reset();
        var fullContent = new System.Text.StringBuilder();

        await foreach (var chunk in source.WithCancellation(cancellationToken))
        {
            var processed = ProcessChunk(chunk);
            fullContent.Append(processed);
            yield return processed;
        }

        if (!IsBalanced)
        {
            var repair = BuildRepairSuffix();
            if (repair.Length > 0)
            {
                RepairsApplied++;
                yield return repair;
            }
        }
    }

    public string FinalizeAndRepair(string fullContent)
    {
        if (IsBalanced)
            return fullContent;

        var repair = BuildRepairSuffix();
        if (repair.Length > 0)
            RepairsApplied++;

        return fullContent + repair;
    }

    private string BuildRepairSuffix()
    {
        var sb = new System.Text.StringBuilder();

        if (_inString)
            sb.Append(_stringQuote);

        for (var i = _openStack.Count - 1; i >= 0; i--)
            sb.Append(_openStack[i]);

        return sb.ToString();
    }
}

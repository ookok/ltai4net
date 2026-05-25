using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills.Runtime;

/// <summary>
/// Scoped variable store for Skill DSL execution.
/// Supports: $var, $var.prop, $var[0], $env:HOME, $query, $date
/// </summary>
public sealed class SkillVarScope
{
    private readonly Dictionary<string, SkillValue> _vars = new();
    private readonly Dictionary<string, SkillValue> _envVars = new();
    private readonly ILogger<SkillVarScope> _logger;
    private SkillVarScope? _parent;

    public SkillVarScope(ILogger<SkillVarScope>? logger = null)
    {
        _logger = logger ?? new NullLogger<SkillVarScope>();
        InitBuiltins();
    }

    private void InitBuiltins()
    {
        Set("date", SkillValue.FromString(DateTime.Now.ToString("yyyy-MM-dd")));
        Set("now", SkillValue.FromString(DateTime.Now.ToString("HH:mm:ss")));
        Set("true", SkillValue.FromBool(true));
        Set("false", SkillValue.FromBool(false));
        Set("nil", SkillValue.Nil);
    }

    public SkillVarScope Push()
    {
        return new SkillVarScope(_logger) { _parent = this };
    }

    public SkillVarScope Pop() => _parent ?? this;

    public void Set(string name, SkillValue value)
    {
        _vars[name] = value;
    }

    public SkillValue Get(string name)
    {
        if (_vars.TryGetValue(name, out var v)) return v;
        if (_parent != null) return _parent.Get(name);
        return SkillValue.Nil;
    }

    public SkillValue Resolve(string expr)
    {
        if (string.IsNullOrEmpty(expr)) return SkillValue.Nil;

        if (expr.StartsWith("$env:"))
            return ResolveEnv(expr[5..]);

        if (expr.StartsWith("$"))
            return ResolveVar(expr[1..]);

        if (expr.StartsWith("\"") && expr.EndsWith("\""))
            return SkillValue.FromString(expr[1..^1]);

        if (double.TryParse(expr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var num))
            return SkillValue.FromNumber(num);

        if (expr is "true") return SkillValue.FromBool(true);
        if (expr is "false") return SkillValue.FromBool(false);
        if (expr is "nil" or "null") return SkillValue.Nil;

        return SkillValue.FromString(expr);
    }

    private SkillValue ResolveVar(string path)
    {
        var parts = path.Split('.');
        var val = Get(parts[0]);

        for (int i = 1; i < parts.Length; i++)
        {
            var prop = parts[i];

            if (prop.EndsWith("]") && prop.Contains('['))
            {
                var bracketIdx = prop.IndexOf('[');
                var propName = prop[..bracketIdx];
                var indexStr = prop[(bracketIdx + 1)..^1];

                if (!string.IsNullOrEmpty(propName))
                    val = val[propName];

                if (int.TryParse(indexStr, out var idx))
                    val = val[idx];
                else
                    val = val[indexStr];
            }
            else
            {
                val = val[prop];
            }
        }

        return val;
    }

    private SkillValue ResolveEnv(string name)
    {
        if (_envVars.TryGetValue(name, out var cached)) return cached;

        var envVal = Environment.GetEnvironmentVariable(name);
        var val = envVal != null ? SkillValue.FromString(envVal) : SkillValue.Nil;
        _envVars[name] = val;
        return val;
    }

    public void InjectContext(string query, string domain, string model)
    {
        Set("query", SkillValue.FromString(query));
        Set("domain", SkillValue.FromString(domain));
        Set("model", SkillValue.FromString(model));
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}

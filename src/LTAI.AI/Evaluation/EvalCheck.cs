// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LTAI.AI.Evaluation;

/// <summary>
/// Result of a single evaluation check applied to an <see cref="EvalItem"/>.
/// </summary>
/// <param name="Passed">Whether the check passed.</param>
/// <param name="Reason">Human-readable explanation of the result.</param>
/// <param name="CheckName">Name of the check that produced this result.</param>
public sealed record EvalCheckResult(bool Passed, string Reason, string CheckName);

/// <summary>
/// Delegate for an evaluation check function.
/// Takes an <see cref="EvalItem"/> and returns an <see cref="EvalCheckResult"/>.
/// </summary>
public delegate ValueTask<EvalCheckResult> EvalCheck(EvalItem item, CancellationToken cancellationToken = default);

/// <summary>
/// Built-in evaluation check factories.
/// </summary>
public static class EvalChecks
{
    /// <summary>
    /// Creates a check that verifies the response contains all specified keywords.
    /// </summary>
    public static EvalCheck KeywordCheck(params string[] keywords) =>
        KeywordCheck(caseSensitive: false, keywords);

    /// <summary>
    /// Creates a check that verifies the response contains all specified keywords.
    /// </summary>
    public static EvalCheck KeywordCheck(bool caseSensitive, params string[] keywords)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return (item, ct) =>
        {
            var missing = keywords.Where(kw => !item.Response.Contains(kw, comparison)).ToList();
            var passed = missing.Count == 0;
            var reason = passed
                ? $"All keywords found: {string.Join(", ", keywords)}"
                : $"Missing keywords: {string.Join(", ", missing)}";
            return ValueTask.FromResult(new EvalCheckResult(passed, reason, "keyword_check"));
        };
    }

    /// <summary>
    /// Creates a check that verifies the response is non-empty and meets a minimum length.
    /// </summary>
    public static EvalCheck NonEmptyCheck(int minLength = 1)
    {
        return (item, ct) =>
        {
            var trimmed = item.Response.Trim();
            var passed = trimmed.Length >= minLength;
            var reason = passed
                ? $"Response length {trimmed.Length} meets minimum {minLength}"
                : $"Response length {trimmed.Length} is below minimum {minLength}";
            return ValueTask.FromResult(new EvalCheckResult(passed, reason, "non_empty"));
        };
    }

    /// <summary>
    /// Creates a check that verifies the response contains the expected output (from <see cref="EvalItem.Context"/>).
    /// Falls back to EvalItem.Query if Context is null.
    /// </summary>
    public static EvalCheck ContainsExpectedCheck(bool caseSensitive = false)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return (item, ct) =>
        {
            var expected = item.Context ?? item.Query;
            var passed = item.Response.Contains(expected, comparison);
            var reason = passed
                ? $"Response contains expected text"
                : $"Response does not contain expected text: \"{Truncate(expected, 80)}\"";
            return ValueTask.FromResult(new EvalCheckResult(passed, reason, "contains_expected"));
        };
    }

    /// <summary>
    /// Creates a check that verifies specific tools were called.
    /// </summary>
    public static EvalCheck ToolCalledCheck(params string[] toolNames)
    {
        return (item, ct) =>
        {
            var responseLower = item.Response.ToLowerInvariant();
            var found = toolNames.Where(t => responseLower.Contains(t.ToLowerInvariant())).ToHashSet(StringComparer.Ordinal);
            var missing = toolNames.Where(t => !found.Contains(t)).ToList();
            var passed = missing.Count == 0;
            var reason = passed
                ? $"All tools called: {string.Join(", ", toolNames)}"
                : $"Missing tool calls: {string.Join(", ", missing)}";
            return ValueTask.FromResult(new EvalCheckResult(passed, reason, "tool_called"));
        };
    }

    /// <summary>
    /// Creates a check that verifies the response length is within a specified range.
    /// </summary>
    public static EvalCheck ResponseLengthCheck(int minLength, int maxLength = int.MaxValue)
    {
        return (item, ct) =>
        {
            var len = item.Response.Length;
            var passed = len >= minLength && len <= maxLength;
            var reason = passed
                ? $"Response length {len} in range [{minLength}, {maxLength}]"
                : $"Response length {len} outside range [{minLength}, {maxLength}]";
            return ValueTask.FromResult(new EvalCheckResult(passed, reason, "response_length"));
        };
    }

    /// <summary>
    /// Creates a check from a synchronous predicate.
    /// </summary>
    public static EvalCheck FromPredicate(Func<EvalItem, bool> predicate, string checkName)
    {
        return (item, ct) =>
        {
            var passed = predicate(item);
            return ValueTask.FromResult(new EvalCheckResult(passed, passed ? "Passed" : "Failed", checkName));
        };
    }

    /// <summary>
    /// Creates a check from an async predicate.
    /// </summary>
    public static EvalCheck FromAsyncPredicate(Func<EvalItem, CancellationToken, Task<bool>> predicate, string checkName)
    {
        return async (item, ct) =>
        {
            var passed = await predicate(item, ct).ConfigureAwait(false);
            return new EvalCheckResult(passed, passed ? "Passed" : "Failed", checkName);
        };
    }

    private static string Truncate(string s, int maxLength) =>
        s.Length <= maxLength ? s : s[..maxLength] + "...";
}

namespace LTAI.Core.System;

/// <summary>Stub — replaced by LLM-based classification. All return "general".</summary>
public static class ClassificationRegistry
{
    public static (string category, string[] keywords) FindMatch(string text) => ("general", []);
    public static readonly KeywordClassifier EndpointCategory = new();
    public static readonly KeywordClassifier AuthType = new();
    public static readonly KeywordClassifier ModelCapability = new();
    public static readonly MultiKeywordClassifier UrlCapability = new();
    public static readonly MultiKeywordClassifier SuspiciousContent = new();
    public static readonly MultiKeywordClassifier PipelineTrigger = new();
    public static readonly MultiKeywordClassifier ReasoningType = new();
    public static readonly MultiKeywordClassifier CodeLanguage = new();
    public static readonly MultiKeywordClassifier ContentFormat = new();
    public static readonly (string, string[])[] ContentTopics = [("general", [])];
}

/// <summary>Simple keyword classifier stub — returns "general".</summary>
public sealed class KeywordClassifier
{
    public string Classify(string text) => "general";
}

/// <summary>Multi-keyword classifier stub — returns "general".</summary>
public sealed class MultiKeywordClassifier
{
    public string Classify(string text) => "general";
}

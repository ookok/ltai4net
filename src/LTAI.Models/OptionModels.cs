namespace LTAI.Models;

public sealed record OptionKey
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public string? Default { get; set; }
    public string? EnvVar { get; set; }
    public string? Description { get; set; }
    public bool Required { get; set; }
}

public sealed record OptionFile
{
    public string Name { get; set; } = "";
    public string Section { get; set; } = "";
    public string? Description { get; set; }
    public List<OptionKey> Keys { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public string? SourceFile { get; set; }

    public bool IsActive => Keys.Count > 0;
}

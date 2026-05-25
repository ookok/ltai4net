namespace LTAI.Tools.Tools;

public static class JsonToolResult
{
    public const string StatusKey = "status";
    public const string DataKey = "data";
    public const string CountKey = "count";
    public const string ErrorKey = "error";
    public const string ErrorCodeKey = "code";
    public const string ErrorMessageKey = "message";
    public const string WarningKey = "warnings";

    private static readonly System.Text.Json.JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    public static string Success(object data) =>
        System.Text.Json.JsonSerializer.Serialize(new { status = "ok", data }, _opts);

    public static string Error(string message, string? code = null) =>
        System.Text.Json.JsonSerializer.Serialize(new { status = "error", error = new { code = code ?? "unknown", message } }, _opts);

    public static string List(string[] items) =>
        System.Text.Json.JsonSerializer.Serialize(new { status = "ok", count = items.Length, data = items }, _opts);

    public static string Table(Dictionary<string, object> row) =>
        System.Text.Json.JsonSerializer.Serialize(new { status = "ok", data = row }, _opts);

    public static string MultiTable(List<Dictionary<string, object>> rows) =>
        System.Text.Json.JsonSerializer.Serialize(new { status = "ok", count = rows.Count, data = rows }, _opts);
}

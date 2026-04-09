using System.Text.Json;
using System.Text.Json.Serialization;

namespace SQL_Server_MCP.Models;

public sealed record ToolResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("data")] string Data,
    [property: JsonPropertyName("error")] string? Error = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static ToolResult Ok(string data) => new(true, data);

    public static ToolResult Ok(object data) =>
        new(true, JsonSerializer.Serialize(data, SerializerOptions));

    public static ToolResult Fail(string error) => new(false, "[]", error);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}

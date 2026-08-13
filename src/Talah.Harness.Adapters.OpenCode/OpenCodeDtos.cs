using System.Text.Json;
using System.Text.Json.Serialization;

namespace Talah.Harness.Adapters.OpenCode;

public sealed record OpenCodeHealth(bool Healthy, string Version);

public sealed record OpenCodeProviderList(
    [property: JsonPropertyName("all")] IReadOnlyList<OpenCodeProvider> All,
    [property: JsonPropertyName("default")] IReadOnlyDictionary<string, string> Default,
    [property: JsonPropertyName("connected")] IReadOnlyList<string> Connected);

public sealed record OpenCodeProvider(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("models")] IReadOnlyDictionary<string, OpenCodeModel> Models,
    Dictionary<string, JsonElement>? Extra = null);

public sealed record OpenCodeModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    Dictionary<string, JsonElement>? Extra = null);

public sealed record OpenCodeAuthMethod(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("label")] string? Label,
    Dictionary<string, JsonElement>? Extra = null);

public sealed record OpenCodeOAuthAuthorization(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("instructions")] string? Instructions,
    Dictionary<string, JsonElement>? Extra = null);

public sealed record OpenCodeSession(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("parentID")] string? ParentId,
    [property: JsonPropertyName("directory")] string? Directory,
    [property: JsonPropertyName("workspaceID")] string? WorkspaceId,
    [property: JsonPropertyName("time")] OpenCodeSessionTime Time,
    Dictionary<string, JsonElement>? Extra = null);

public sealed record OpenCodeSessionTime(
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("updated")] long Updated,
    [property: JsonPropertyName("archived")] long? Archived = null);

public sealed record OpenCodeMessageEnvelope(
    [property: JsonPropertyName("info")] JsonElement Info,
    [property: JsonPropertyName("parts")] IReadOnlyList<JsonElement> Parts);

public sealed record OpenCodeFileDiff(
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("patch")] string? Patch,
    [property: JsonPropertyName("additions")] int Additions,
    [property: JsonPropertyName("deletions")] int Deletions,
    [property: JsonPropertyName("status")] string? Status,
    Dictionary<string, JsonElement>? Extra = null);

public sealed record OpenCodeSseEvent(string? Id, string? Event, string Data, JsonElement VendorJson);

public sealed record OpenCodeServerMetadata(
    IReadOnlyDictionary<string, JsonElement> Mcp,
    IReadOnlyList<JsonElement> Agents);

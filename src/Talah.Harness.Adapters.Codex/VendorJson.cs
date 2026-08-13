using System.Text.Json;

namespace Talah.Harness.Adapters.Codex;

internal static class VendorJson
{
    private static readonly string[] SecretNames =
    [
        "apiKey", "accessToken", "refreshToken", "token", "authorization", "secret"
    ];

    public static JsonElement Sanitize(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteSanitized(writer, value, null);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteSanitized(Utf8JsonWriter writer, JsonElement value, string? propertyName)
    {
        if (propertyName is not null && SecretNames.Any(
            name => propertyName.Contains(name, StringComparison.OrdinalIgnoreCase)))
        {
            writer.WriteStringValue("[REDACTED]");
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteSanitized(writer, property.Value, property.Name);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteSanitized(writer, item, propertyName);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}

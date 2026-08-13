using System.Text.Json;
using System.Text.RegularExpressions;
using Talah.Harness.Contracts;

namespace Talah.Harness.Application;

internal static partial class SensitiveEventSanitizer
{
    private const string Replacement = "[REDACTED]";

    public static KernelEvent Sanitize(KernelEvent kernelEvent) => kernelEvent with
    {
        Data = SanitizeData(kernelEvent.Data),
        VendorData = Sanitize(kernelEvent.VendorData)
    };

    private static KernelEventData SanitizeData(KernelEventData data) => data switch
    {
        ItemEventData item => item with { Item = item.Item with { VendorData = Sanitize(item.Item.VendorData) } },
        PermissionEventData permission => permission with
        {
            Request = permission.Request with { VendorData = Sanitize(permission.Request.VendorData) }
        },
        ElicitationEventData elicitation => elicitation with
        {
            Request = elicitation.Request with { VendorData = Sanitize(elicitation.Request.VendorData) }
        },
        DiagnosticEventData diagnostic => diagnostic with
        {
            Diagnostic = diagnostic.Diagnostic with { VendorData = Sanitize(diagnostic.Diagnostic.VendorData) }
        },
        _ => data
    };

    private static JsonElement? Sanitize(JsonElement? source)
    {
        if (source is null) return null;
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) Write(writer, source.Value, null);
        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, string? propertyName)
    {
        if (propertyName is not null && IsSensitiveName(propertyName))
        {
            writer.WriteStringValue(Replacement);
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray()) Write(writer, item, null);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactString(value.GetString() ?? string.Empty));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveName(string name)
    {
        string normalized = new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized is
            "authorization" or "apikey" or "accesstoken" or "refreshtoken" or "idtoken" or "token" or
            "secret" or "clientsecret" or "password" or "credential" or "cookie" or "setcookie";
    }

    private static string RedactString(string value)
    {
        string redacted = AuthorizationPattern().Replace(value, "$1" + Replacement);
        return CommonSecretPattern().Replace(redacted, Replacement);
    }

    [GeneratedRegex("(?i)\\b(bearer|basic)\\s+[^\\s,;\\\"]+", RegexOptions.CultureInvariant, 100)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex("(?i)\\b(?:sk-[a-z0-9_-]{16,}|gh[pousr]_[a-z0-9]{16,})\\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CommonSecretPattern();
}

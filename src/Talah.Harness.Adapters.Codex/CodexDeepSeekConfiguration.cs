using System.Reflection;
using System.Text;
using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.Codex;

public static class CodexDeepSeekConfiguration
{
    public const string ProviderId = "deepseek";
    public const string ProviderBaseUri = "https://api.deepseek.com/";
    public const string DefaultModel = "deepseek-v4-flash";
    public const string ConfigurationVersion = "1.0.0";
    public const string CatalogResourceName = "Talah.Harness.Adapters.Codex.DeepSeek.deepseek-models-2026-08-15.json";

    private static readonly string[] SupportedModels = ["deepseek-v4-flash", "deepseek-v4-pro"];

    public static string NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return DefaultModel;
        return SupportedModels.FirstOrDefault(candidate => string.Equals(candidate, model, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Codex DeepSeek supports deepseek-v4-flash and deepseek-v4-pro.", nameof(model));
    }

    public static async Task PrepareHomeAsync(
        string codexHome,
        string? model,
        bool enableDeepSeek,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(codexHome);
        if (!enableDeepSeek) return;

        string selectedModel = NormalizeModel(model);
        string catalogPath = Path.Combine(codexHome, "models.json");
        string configPath = Path.Combine(codexHome, "config.toml");

        string catalog = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        await WriteAtomicallyAsync(catalogPath, catalog, cancellationToken).ConfigureAwait(false);

        string config = BuildConfigToml(codexHome, selectedModel);
        await WriteAtomicallyAsync(configPath, config, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildConfigToml(string codexHome, string selectedModel)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Managed by Talah Harness. Do not edit; the host recreates this file.");
        builder.AppendLine("# DeepSeek configuration format verified with Codex CLI 0.147.0 (pinned schema 0.147.0).");
        builder.AppendLine("model_provider = \"deepseek\"");
        builder.AppendLine($"model = \"{selectedModel}\"");
        builder.AppendLine("preferred_auth_method = \"apikey\"");
        builder.AppendLine("forced_login_method = \"api\"");
        builder.AppendLine("model_reasoning_effort = \"high\"");
        builder.AppendLine($"model_catalog_json = '{EscapeTomlLiteral(Path.Combine(codexHome, "models.json"))}'");
        builder.AppendLine();
        builder.AppendLine("[model_providers.deepseek]");
        builder.AppendLine("name = \"deepseek\"");
        builder.AppendLine($"base_url = \"{ProviderBaseUri}\"");
        builder.AppendLine("wire_api = \"responses\"");
        builder.AppendLine($"env_key = \"{KernelCredentialEnvironment.DeepSeekApiKey}\"");
        builder.AppendLine("requires_openai_auth = false");
        return builder.ToString();
    }

    private static string EscapeTomlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static async Task<string> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        await using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(CatalogResourceName)
            ?? throw new InvalidDataException($"Embedded Codex DeepSeek model catalog '{CatalogResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

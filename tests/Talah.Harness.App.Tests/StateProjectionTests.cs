using System.Text.Json;
using System.Xml.Linq;
using Talah.Harness.App.Models;
using Talah.Harness.App.Services;
using Talah.Harness.Adapters.OpenCode;
using Talah.Harness.Application;
using Talah.Harness.Contracts;
using Talah.Harness.Persistence;
using Xunit;

namespace Talah.Harness.App.Tests;

public sealed class StateProjectionTests
{
    [Fact]
    public void ResponsiveLayoutHostUsesTheDpiAwareSizeHandlerWithoutFrameworkAdaptiveTriggers()
    {
        XDocument document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement host = document.Descendants().Single(element =>
            element.Name.LocalName == "UserControl" && (string?)element.Attribute(xaml + "Name") == "LayoutStateHost");
        Assert.Equal("LayoutStateHost_SizeChanged", (string?)host.Attribute("SizeChanged"));
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "AdaptiveTrigger");
    }

    [Theory]
    [InlineData(0, ResponsiveLayoutBand.Narrow)]
    [InlineData(759.99, ResponsiveLayoutBand.Narrow)]
    [InlineData(760, ResponsiveLayoutBand.Medium)]
    [InlineData(1179.99, ResponsiveLayoutBand.Medium)]
    [InlineData(1180, ResponsiveLayoutBand.Wide)]
    [InlineData(4096, ResponsiveLayoutBand.Wide)]
    public void ResponsiveLayoutPolicySelectsContiguousEffectiveWidthBands(double width, ResponsiveLayoutBand expected) =>
        Assert.Equal(expected, ResponsiveLayoutPolicy.SelectBand(width));

    [Fact]
    public void ToolArgumentsRedactSensitivePropertiesAndNestedText()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "provider": "openai",
              "apiKey": "plain-value",
              "nested": { "message": "access_token=another-value" }
            }
            """);

        string rendered = TraceProjection.RenderJson(document.RootElement);

        Assert.Contains("openai", rendered, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-value", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("another-value", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void UserFacingFailureDoesNotExposeExceptionMessage()
    {
        const string sensitiveMessage = "credential=should-not-appear";

        string rendered = HarnessController.SafeFailure(new InvalidOperationException(sensitiveMessage), "Kernel start");

        Assert.Contains(nameof(InvalidOperationException), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveMessage, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticFormatterRedactsHeadersCookiesUrlsAndNestedFailures()
    {
        var nested = new InvalidOperationException(
            "Authorization: Bearer outer-value Cookie: sid=cookie-value https://user:url-password@example.test/?access_token=query-value",
            new IOException("client_secret=inner-value"));

        string rendered = SecureDiagnosticFormatter.FormatException(nested);

        Assert.Contains(nameof(InvalidOperationException), rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(IOException), rendered, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("outer-value", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie-value", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("url-password", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("query-value", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("inner-value", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsContractContainsOnlyNonCredentialPreferences()
    {
        var settings = new HarnessSettings(
            OnboardingComplete: true,
            WorkspacePath: @"C:\work",
            WorkspaceTrusted: true,
            SelectedAdapterId: "codex",
            SelectedModels: new Dictionary<string, string> { ["codex"] = "gpt" });

        string serialized = JsonSerializer.Serialize(settings);

        Assert.DoesNotContain("password", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AbsentOpenCodeRemainsHostedWithoutAnEventStreamFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "talah-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new HarnessDatabase(new HarnessDatabaseOptions(Path.Combine(root, "harness.db")));
            await using var host = new KernelHost([new OpenCodeAdapterFactory()], database, new CanonicalRepository(database));
            await host.InitializeAsync();
            var profile = new KernelProfile("default", "opencode", "OpenCode", Path.Combine(root, "profile"),
                new Dictionary<string, string> { ["OPENCODE_EXECUTABLE"] = Path.Combine(root, "missing-opencode.exe") }, true);

            HostedKernelSnapshot snapshot = await host.StartProfileAsync(profile, "1.0.0", Path.Combine(root, "logs"), Path.Combine(root, "schemas"));

            Assert.Equal(KernelAvailability.NotInstalled, snapshot.Health.Availability);
            Assert.Null(snapshot.EventPumpFailure);
            Assert.Contains("not installed", snapshot.Health.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

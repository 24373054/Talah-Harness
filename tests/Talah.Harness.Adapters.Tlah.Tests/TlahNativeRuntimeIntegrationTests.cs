using Microsoft.Data.Sqlite;
using TLAHStudio.Core.Helpers;

namespace Talah.Harness.Adapters.Tlah.Tests;

public sealed class TlahNativeRuntimeIntegrationTests
{
    [Fact]
    public async Task RealCoreDataGraph_PersistsChatsSettingsAndProtectedSecret_WithoutNetwork()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        const string secret = "sk-native-integration-secret";
        await using var runtime = new TlahNativeRuntime();
        await runtime.InitializeAsync(temp.Path, CancellationToken.None);
        await runtime.ConfigureAsync("openai", secret, null, CancellationToken.None);
        var chat = await runtime.CreateChatAsync("Native graph", temp.Path, CancellationToken.None);
        _ = await runtime.RenameAsync(chat.Id, "Renamed native graph", CancellationToken.None);
        _ = await runtime.SetArchivedAsync(chat.Id, true, CancellationToken.None);

        var chats = await runtime.ListChatsAsync(includeArchived: true, CancellationToken.None);
        Assert.Equal("Renamed native graph", Assert.Single(chats).Title);
        Assert.True(chats[0].IsArchived);
        Assert.True(await runtime.IsConfiguredAsync(CancellationToken.None));

        await using var connection = new SqliteConnection($"Data Source={System.IO.Path.Combine(temp.Path, "tlah.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ApiKey FROM GlobalSettings WHERE Id = 1";
        string stored = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        Assert.True(ProtectedSecret.IsProtected(stored));
    }
}

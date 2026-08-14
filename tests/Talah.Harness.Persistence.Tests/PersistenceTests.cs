using Microsoft.Data.Sqlite;
using System.Text.Json;
using Talah.Harness.Contracts;
using Talah.Harness.Persistence;
using Xunit;

namespace Talah.Harness.Persistence.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task EmptyDatabase_MigratesToCurrentSchemaWithWalAndIntegrity()
    {
        using var temp = new TemporaryDirectory();
        var database = CreateDatabase(temp);
        var result = await database.InitializeAsync();
        Assert.Equal(HarnessDatabase.CurrentSchemaVersion, result.SchemaVersion);
        Assert.Equal("ok", result.IntegrityResult);
        Assert.Null(result.BackupPath);

        await using var connection = await database.OpenConnectionAsync();
        Assert.Equal((long)HarnessDatabase.CurrentSchemaVersion, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal("wal", ((string)(await ScalarAsync(connection, "PRAGMA journal_mode;"))!).ToLowerInvariant());
        var tables = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('sessions','turns','items','canonical_events','adapter_profiles','workspaces','checkpoints','approvals','elicitations','schema_version');"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(10, tables);
    }

    [Fact]
    public async Task DuplicateNativeEvent_IsIdempotentAndKeepsHostSequence()
    {
        using var temp = new TemporaryDirectory();
        var database = CreateDatabase(temp);
        await database.InitializeAsync();
        var repository = new CanonicalRepository(database);
        var first = await repository.AppendEventAsync("evt-1", Event(1));
        var duplicate = await repository.AppendEventAsync("evt-1", Event(999));
        Assert.True(first.Inserted);
        Assert.False(duplicate.Inserted);
        Assert.Equal(first.HostSequence, duplicate.HostSequence);
        var page = await repository.GetEventsAsync(null, null, null, new PageRequest());
        Assert.Single(page.Items);
        Assert.Equal(1, page.Items[0].Event.Sequence);
    }

    [Fact]
    public async Task SessionHistoryAndEvents_ArePaged()
    {
        using var temp = new TemporaryDirectory();
        var database = CreateDatabase(temp);
        await database.InitializeAsync();
        var repository = new CanonicalRepository(database);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 5; index++)
        {
            await repository.UpsertSessionAsync(Session($"s-{index}", now.AddMinutes(index)));
            await repository.AppendEventAsync($"evt-{index}", Event(index));
        }

        var sessions1 = await repository.ListSessionsAsync(new PageRequest(2));
        var sessions2 = await repository.ListSessionsAsync(new PageRequest(2, sessions1.NextCursor));
        var sessions3 = await repository.ListSessionsAsync(new PageRequest(2, sessions2.NextCursor));
        Assert.True(sessions1.HasMore);
        Assert.True(sessions2.HasMore);
        Assert.False(sessions3.HasMore);
        Assert.Equal(5, sessions1.Items.Concat(sessions2.Items).Concat(sessions3.Items).Select(x => x.HostSessionId).Distinct().Count());

        var events1 = await repository.GetEventsAsync("codex", "default", "s", new PageRequest(3));
        var events2 = await repository.GetEventsAsync("codex", "default", "s", new PageRequest(3, events1.NextCursor));
        Assert.Equal(5, events1.Items.Concat(events2.Items).Count());

        var hostSession = sessions1.Items[0].HostSessionId;
        for (var index = 0; index < 3; index++)
        {
            await repository.UpsertItemAsync(hostSession, "turn", new KernelItem($"item-{index}", KernelItemKind.AssistantMessage,
                KernelItemStatus.Completed, null, [new TextContentBlock(index.ToString(System.Globalization.CultureInfo.InvariantCulture))], now, now));
        }
        var history1 = await repository.GetHistoryAsync(hostSession, new PageRequest(2));
        var history2 = await repository.GetHistoryAsync(hostSession, new PageRequest(2, history1.NextCursor));
        Assert.Equal(3, history1.Items.Concat(history2.Items).Count());
    }

    [Fact]
    public async Task ExistingV0Database_IsBackedUpBeforeMigration()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "store.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE legacy_marker(value TEXT); INSERT INTO legacy_marker(value) VALUES ('preserve-me'); PRAGMA user_version=0;";
            await command.ExecuteNonQueryAsync();
        }

        var database = CreateDatabase(temp);
        var result = await database.InitializeAsync();
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        await using var backup = new SqliteConnection($"Data Source={result.BackupPath};Mode=ReadOnly;Pooling=False");
        await backup.OpenAsync();
        Assert.Equal("preserve-me", await ScalarAsync(backup, "SELECT value FROM legacy_marker;"));
    }

    [Fact]
    public async Task ExistingV1Database_IsBackedUpAndMigratedForwardWithoutLosingData()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "store.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_version(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL) STRICT;
                INSERT INTO schema_version(version,applied_at) VALUES(1,'2026-01-01T00:00:00Z');
                CREATE TABLE preserved(value TEXT NOT NULL) STRICT;
                INSERT INTO preserved(value) VALUES('keep-v1');
                PRAGMA user_version=1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var result = await CreateDatabase(temp).InitializeAsync();
        Assert.Equal(2, result.SchemaVersion);
        Assert.NotNull(result.BackupPath);
        await using var migrated = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await migrated.OpenAsync();
        Assert.Equal("keep-v1", await ScalarAsync(migrated, "SELECT value FROM preserved;"));
        Assert.Equal(1L, await ScalarAsync(migrated, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='elicitations';"));
        Assert.Equal(2L, await ScalarAsync(migrated, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task FailedForwardMigrationRollsBackAndLeavesReadableBackup()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "store.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_version(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL) STRICT;
                INSERT INTO schema_version(version,applied_at) VALUES(1,'2026-01-01T00:00:00Z');
                CREATE TABLE elicitations(conflict TEXT);
                CREATE TABLE preserved(value TEXT);
                INSERT INTO preserved(value) VALUES('recoverable');
                PRAGMA user_version=1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        HarnessMigrationException error = await Assert.ThrowsAsync<HarnessMigrationException>(() => CreateDatabase(temp).InitializeAsync());
        Assert.Equal(1, error.SourceVersion);
        Assert.Equal(2, error.TargetVersion);
        string backupPath = Assert.Single(Directory.GetFiles(Path.Combine(temp.Path, "backups"), "*.v1.bak"));
        await using var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False");
        await backup.OpenAsync();
        Assert.Equal("recoverable", await ScalarAsync(backup, "SELECT value FROM preserved;"));
        await using var original = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await original.OpenAsync();
        Assert.Equal(1L, await ScalarAsync(original, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task ConcurrentWriters_CommitAllUniqueEvents()
    {
        using var temp = new TemporaryDirectory();
        var database = CreateDatabase(temp);
        await database.InitializeAsync();
        var repository = new CanonicalRepository(database);
        var writes = Enumerable.Range(0, 32).Select(index => repository.AppendEventAsync($"parallel-{index}", Event(index)));
        var results = await Task.WhenAll(writes);
        Assert.All(results, result => Assert.True(result.Inserted));
        Assert.Equal(32, results.Select(result => result.HostSequence).Distinct().Count());
        var page = await repository.GetEventsAsync(null, null, null, new PageRequest(100));
        Assert.Equal(32, page.Items.Count);
    }

    [Fact]
    public async Task CanonicalCrud_CoversTurnCheckpointApprovalAndSecretFreeProfile()
    {
        using var temp = new TemporaryDirectory();
        var database = CreateDatabase(temp);
        await database.InitializeAsync();
        var repository = new CanonicalRepository(database);
        var now = DateTimeOffset.UtcNow;
        var sessionId = await repository.UpsertSessionAsync(Session("session", now));
        var turnId = await repository.UpsertTurnAsync(sessionId, "turn", TurnStatus.Completed, now, now, "done");
        Assert.True(turnId > 0);
        using var marker = JsonDocument.Parse("{\"cursor\":12}");
        Assert.True(await repository.AddCheckpointAsync("codex", "default", "session", "recovery", marker.RootElement) > 0);
        using var request = JsonDocument.Parse("{\"tool\":\"shell\"}");
        await repository.UpsertApprovalAsync(new StoredApproval("approval", "codex", "default", "session", "turn", "pending", request.RootElement, null, now, null));
        await repository.UpsertAdapterProfileAsync(new StoredAdapterProfile("codex", "default", "Codex", "C:\\data", true, now));

        await using var connection = await database.OpenConnectionAsync();
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT metadata_json FROM adapter_profiles LIMIT 1;"));
        var profileText = Convert.ToString(await ScalarAsync(connection, "SELECT adapter_id || profile_id || display_name || data_root FROM adapter_profiles LIMIT 1;"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.DoesNotContain("secret-value", profileText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceAndSessionBindingRoundTripWithoutBeingClearedByAdapterRefresh()
    {
        using var temp = new TemporaryDirectory();
        var database = CreateDatabase(temp);
        await database.InitializeAsync();
        var repository = new CanonicalRepository(database);
        var workspace = new WorkspaceDescriptor("ws_1", @"C:\work", [@"D:\shared"], true);
        await repository.UpsertWorkspaceAsync(workspace);
        var now = DateTimeOffset.UtcNow;
        var bound = Session("bound", now) with { Session = Session("bound", now).Session with { WorkspaceId = workspace.WorkspaceId } };
        await repository.UpsertSessionAsync(bound);
        await repository.UpsertSessionAsync(Session("bound", now.AddMinutes(1)));

        var storedWorkspace = (await repository.GetWorkspaceAsync(workspace.WorkspaceId))!.Workspace;
        Assert.Equal(workspace.WorkspaceId, storedWorkspace.WorkspaceId);
        Assert.Equal(workspace.RootPath, storedWorkspace.RootPath);
        Assert.Equal(workspace.AdditionalRoots, storedWorkspace.AdditionalRoots);
        Assert.Equal(workspace.IsTrusted, storedWorkspace.IsTrusted);
        Assert.Equal(workspace.WorkspaceId, (await repository.GetSessionAsync(bound.Session))!.Summary.Session.WorkspaceId);
    }

    [Fact]
    public async Task AdapterEventProjectionCannotOverwriteHostOwnedSessionState()
    {
        using var temp = new TemporaryDirectory();
        var database = CreateDatabase(temp);
        await database.InitializeAsync();
        var repository = new CanonicalRepository(database);
        var now = DateTimeOffset.UtcNow;
        KernelSessionSummary host = Session("projected", now) with
        {
            Session = Session("projected", now).Session with { WorkspaceId = "ws_host" },
            Metadata = new Dictionary<string, string>
            {
                [SessionSecurityMetadata.ApprovalMode] = "on-request",
                [SessionSecurityMetadata.SandboxMode] = "workspace-write",
                ["native.stale"] = "old"
            }
        };
        await repository.UpsertSessionAsync(host);

        KernelSessionSummary adapterEvent = Session("projected", now.AddMinutes(1)) with
        {
            Session = Session("projected", now).Session with { WorkspaceId = "ws_adapter" },
            Metadata = new Dictionary<string, string>
            {
                [SessionSecurityMetadata.ApprovalMode] = "never",
                ["host.spoofed"] = "true",
                ["native.current"] = "new"
            }
        };
        await repository.UpsertProjectedSessionAsync(adapterEvent);

        KernelSessionSummary stored = (await repository.GetSessionAsync(host.Session))!.Summary;
        Assert.Equal("ws_host", stored.Session.WorkspaceId);
        Assert.Equal("on-request", stored.Metadata![SessionSecurityMetadata.ApprovalMode]);
        Assert.Equal("workspace-write", stored.Metadata[SessionSecurityMetadata.SandboxMode]);
        Assert.Equal("new", stored.Metadata["native.current"]);
        Assert.DoesNotContain("native.stale", stored.Metadata.Keys);
        Assert.DoesNotContain("host.spoofed", stored.Metadata.Keys);
    }

    [Fact]
    public async Task ResolvedApprovalCannotBeReopenedByEventProjectionReplay()
    {
        using var temp = new TemporaryDirectory();
        var database = CreateDatabase(temp);
        await database.InitializeAsync();
        var repository = new CanonicalRepository(database);
        var now = DateTimeOffset.UtcNow;
        var request = JsonSerializer.SerializeToElement(new { tool = "shell" });
        var response = JsonSerializer.SerializeToElement(new { choice = "deny" });
        var pending = new StoredApproval("approval", "codex", "default", "session", "turn", "pending", request, null, now, null);
        await repository.UpsertApprovalAsync(pending);
        await repository.UpsertApprovalAsync(pending with { Status = "resolved", Response = response, ResolvedAt = now.AddSeconds(1) });
        await repository.UpsertApprovalAsync(pending); // Replay the original request event.

        var stored = await repository.GetApprovalAsync("approval");
        Assert.Equal("resolved", stored!.Status);
        Assert.Equal("deny", stored.Response!.Value.GetProperty("choice").GetString());
        Assert.Empty(await repository.ListPendingApprovalsAsync());
    }

    [Fact]
    public async Task InteractionStateMachinePreventsReplayAndRecoverySweepAbandonsInterruptedWork()
    {
        using var temp = new TemporaryDirectory();
        var database = CreateDatabase(temp);
        await database.InitializeAsync();
        var repository = new CanonicalRepository(database);
        var now = DateTimeOffset.UtcNow;
        var running = Session("running", now) with { Status = SessionStatus.Running };
        long hostSessionId = await repository.UpsertSessionAsync(running);
        await repository.UpsertTurnAsync(hostSessionId, "turn", TurnStatus.WaitingForApproval, now);
        JsonElement permissionRequest = JsonSerializer.SerializeToElement(new { tool = "shell" });
        JsonElement permissionResponse = JsonSerializer.SerializeToElement(new { choice = "deny" });
        var approval = new StoredApproval("approval", "codex", "default", "running", "turn", "pending", permissionRequest, null, now, null);
        await repository.UpsertApprovalAsync(approval);
        await repository.UpsertApprovalAsync(approval with { Status = "responding", Response = permissionResponse });
        await repository.UpsertApprovalAsync(approval); // Original request replay cannot return it to pending.
        Assert.Equal("responding", (await repository.GetApprovalAsync("approval"))!.Status);

        JsonElement elicitationRequest = JsonSerializer.SerializeToElement(new { prompt = "value" });
        var elicitation = new StoredElicitation("question", "codex", "default", "running", "turn", "pending", elicitationRequest, null, now, null);
        await repository.UpsertElicitationAsync(elicitation);
        Assert.Single(await repository.ListPendingElicitationsAsync());

        RecoverySweepResult result = await repository.RecoverInterruptedOperationsAsync(now.AddMinutes(1));
        Assert.Equal(new RecoverySweepResult(1, 1, 1, 1), result);
        Assert.True(result.HadInterruptedWork);
        Assert.Equal("abandoned", (await repository.GetApprovalAsync("approval"))!.Status);
        Assert.Equal("abandoned", (await repository.GetElicitationAsync("question"))!.Status);
        Assert.Empty(await repository.ListPendingApprovalsAsync());
        Assert.Empty(await repository.ListPendingElicitationsAsync());

        await using SqliteConnection connection = await database.OpenConnectionAsync();
        Assert.Equal((long)TurnStatus.Failed, await ScalarAsync(connection, "SELECT status FROM turns WHERE native_turn_id='turn';"));
        Assert.Equal((long)SessionStatus.Paused, await ScalarAsync(connection, "SELECT status FROM sessions WHERE native_session_id='running';"));
    }

    [Fact]
    public async Task EventsAfterSequenceSupportDurableCatchUp()
    {
        using var temp = new TemporaryDirectory();
        var database = CreateDatabase(temp);
        await database.InitializeAsync();
        var repository = new CanonicalRepository(database);
        var first = await repository.AppendEventAsync("first", Event(1));
        var second = await repository.AppendEventAsync("second", Event(2));

        var catchUp = await repository.GetEventsAfterAsync(first.HostSequence);
        var stored = Assert.Single(catchUp);
        Assert.Equal(second.HostSequence, stored.HostSequence);
        Assert.Equal("second", stored.NativeEventId);
    }

    [Fact]
    public async Task NewerSchema_FailsWithActionableVersionInformation()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "store.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=3;";
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<HarnessMigrationException>(() => CreateDatabase(temp).InitializeAsync());
        Assert.Equal(3, error.SourceVersion);
        Assert.Equal(2, error.TargetVersion);
        Assert.Contains("newer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DatabaseOperations_HonorCancellation()
    {
        using var temp = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateDatabase(temp).InitializeAsync(cancellation.Token));
    }

    private static HarnessDatabase CreateDatabase(TemporaryDirectory temp) =>
        new(new HarnessDatabaseOptions(Path.Combine(temp.Path, "store.db"), TimeSpan.FromSeconds(15)));

    private static KernelSessionSummary Session(string nativeId, DateTimeOffset updated) =>
        new(new SessionRef("codex", "default", nativeId), nativeId, SessionStatus.Idle, updated.AddMinutes(-1), updated, null);

    private static KernelEvent Event(long sequence) =>
        new("codex", "default", "s", "t", null, sequence, DateTimeOffset.UtcNow, KernelEventKind.AdapterStatusChanged,
            new StatusEventData(KernelAvailability.Ready, "ready"));

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "talah-persistence-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Talah.Harness.Contracts;

namespace Talah.Harness.Persistence;

public sealed record StoredSession(long HostSessionId, KernelSessionSummary Summary);
public sealed record StoredWorkspace(WorkspaceDescriptor Workspace, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record StoredTurn(long HostTurnId, long HostSessionId, string NativeTurnId, TurnStatus Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Summary);
public sealed record StoredItem(long HostItemId, string? NativeTurnId, KernelItem Item);
public sealed record StoredCanonicalEvent(long HostSequence, string NativeEventId, KernelEvent Event);
public sealed record EventAppendResult(long HostSequence, bool Inserted);
public sealed record StoredAdapterProfile(string AdapterId, string ProfileId, string DisplayName, string DataRoot, bool IsDefault, DateTimeOffset UpdatedAt);
public sealed record StoredCheckpoint(long CheckpointId, string AdapterId, string ProfileId, string? NativeSessionId, string MarkerKind, JsonElement Marker, DateTimeOffset CreatedAt);
public sealed record StoredApproval(string ApprovalId, string AdapterId, string ProfileId, string? NativeSessionId, string? NativeTurnId, string Status, JsonElement Request, JsonElement? Response, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);
public sealed record StoredElicitation(string RequestId, string AdapterId, string ProfileId, string? NativeSessionId, string? NativeTurnId, string Status, JsonElement Request, JsonElement? Response, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);
public sealed record RecoverySweepResult(int TurnsFailed, int SessionsPaused, int ApprovalsAbandoned, int ElicitationsAbandoned)
{
    public bool HadInterruptedWork => TurnsFailed + SessionsPaused + ApprovalsAbandoned + ElicitationsAbandoned > 0;
}

public sealed class CanonicalRepository(HarnessDatabase database)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HarnessDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<long> UpsertSessionAsync(KernelSessionSummary summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using System.Data.Common.DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO sessions(adapter_id, profile_id, native_session_id, parent_native_session_id, workspace_id,
                                 title, status, created_at, updated_at, preview, metadata_json)
            VALUES ($adapter, $profile, $native, $parent, $workspace, $title, $status, $created, $updated, $preview, $metadata)
            ON CONFLICT(adapter_id, profile_id, native_session_id) DO UPDATE SET
                parent_native_session_id=excluded.parent_native_session_id,
                workspace_id=COALESCE(excluded.workspace_id, sessions.workspace_id),
                title=excluded.title,
                status=excluded.status,
                updated_at=excluded.updated_at,
                preview=excluded.preview,
                metadata_json=excluded.metadata_json
            RETURNING session_id;
            """;
        Add(command, "$adapter", summary.Session.AdapterId);
        Add(command, "$profile", summary.Session.ProfileId);
        Add(command, "$native", summary.Session.NativeSessionId);
        Add(command, "$parent", summary.Session.ParentNativeSessionId);
        Add(command, "$workspace", summary.Session.WorkspaceId);
        Add(command, "$title", summary.Title);
        Add(command, "$status", (int)summary.Status);
        Add(command, "$created", Format(summary.CreatedAt));
        Add(command, "$updated", Format(summary.UpdatedAt));
        Add(command, "$preview", summary.Preview);
        Add(command, "$metadata", summary.Metadata is null ? null : JsonSerializer.Serialize(summary.Metadata, JsonOptions));
        long id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task<ResultPage<StoredSession>> ListSessionsAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ValidatePage(page);
        int offset = DecodeCursor(page.Cursor);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, adapter_id, profile_id, native_session_id, parent_native_session_id, workspace_id,
                   title, status, created_at, updated_at, preview, metadata_json
            FROM sessions ORDER BY updated_at DESC, session_id DESC LIMIT $limit OFFSET $offset;
            """;
        Add(command, "$limit", page.PageSize + 1);
        Add(command, "$offset", offset);
        var result = new List<StoredSession>(page.PageSize + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadSession(reader));
        return Page(result, page.PageSize, offset);
    }

    public async Task<StoredSession?> GetSessionAsync(SessionRef session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, adapter_id, profile_id, native_session_id, parent_native_session_id, workspace_id,
                   title, status, created_at, updated_at, preview, metadata_json
            FROM sessions
            WHERE adapter_id=$adapter AND profile_id=$profile AND native_session_id=$native;
            """;
        Add(command, "$adapter", session.AdapterId);
        Add(command, "$profile", session.ProfileId);
        Add(command, "$native", session.NativeSessionId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSession(reader) : null;
    }

    public async Task UpsertWorkspaceAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspaces(workspace_id,root_path,additional_roots_json,is_trusted,created_at,updated_at)
            VALUES($id,$root,$additional,$trusted,$created,$updated)
            ON CONFLICT(workspace_id) DO UPDATE SET root_path=excluded.root_path,
                additional_roots_json=excluded.additional_roots_json,is_trusted=excluded.is_trusted,
                updated_at=excluded.updated_at;
            """;
        Add(command, "$id", workspace.WorkspaceId);
        Add(command, "$root", workspace.RootPath);
        Add(command, "$additional", JsonSerializer.Serialize(workspace.AdditionalRoots, JsonOptions));
        Add(command, "$trusted", workspace.IsTrusted ? 1 : 0);
        Add(command, "$created", Format(now));
        Add(command, "$updated", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredWorkspace?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) throw new ArgumentException("A workspace identity is required.", nameof(workspaceId));
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT workspace_id,root_path,additional_roots_json,is_trusted,created_at,updated_at FROM workspaces WHERE workspace_id=$id;";
        Add(command, "$id", workspaceId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        IReadOnlyList<string> additionalRoots = JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(2), JsonOptions)
            ?? throw new InvalidDataException("Stored workspace roots are invalid.");
        return new StoredWorkspace(
            new WorkspaceDescriptor(reader.GetString(0), reader.GetString(1), additionalRoots, reader.GetInt32(3) == 1),
            Parse(reader.GetString(4)),
            Parse(reader.GetString(5)));
    }

    public async Task UpsertItemAsync(long hostSessionId, string? nativeTurnId, KernelItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO items(session_id, native_turn_id, native_item_id, parent_native_item_id, kind, status, title,
                              content_json, vendor_json, created_at, updated_at)
            VALUES ($session, $turn, $native, $parent, $kind, $status, $title, $content, $vendor, $created, $updated)
            ON CONFLICT(session_id, native_item_id) DO UPDATE SET
                native_turn_id=excluded.native_turn_id, parent_native_item_id=excluded.parent_native_item_id,
                kind=excluded.kind, status=excluded.status, title=excluded.title, content_json=excluded.content_json,
                vendor_json=excluded.vendor_json, updated_at=excluded.updated_at;
            """;
        Add(command, "$session", hostSessionId);
        Add(command, "$turn", nativeTurnId);
        Add(command, "$native", item.NativeItemId);
        Add(command, "$parent", item.ParentNativeItemId);
        Add(command, "$kind", (int)item.Kind);
        Add(command, "$status", (int)item.Status);
        Add(command, "$title", item.Title);
        Add(command, "$content", JsonSerializer.Serialize(item.Content, JsonOptions));
        Add(command, "$vendor", item.VendorData?.GetRawText());
        Add(command, "$created", Format(item.CreatedAt));
        Add(command, "$updated", Format(item.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> UpsertTurnAsync(long hostSessionId, string nativeTurnId, TurnStatus status, DateTimeOffset startedAt,
        DateTimeOffset? completedAt = null, string? summary = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nativeTurnId)) throw new ArgumentException("A native turn identity is required.", nameof(nativeTurnId));
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO turns(session_id,native_turn_id,status,started_at,completed_at,summary)
            VALUES($session,$native,$status,$started,$completed,$summary)
            ON CONFLICT(session_id,native_turn_id) DO UPDATE SET status=excluded.status,completed_at=excluded.completed_at,summary=excluded.summary
            RETURNING turn_id;
            """;
        Add(command, "$session", hostSessionId); Add(command, "$native", nativeTurnId); Add(command, "$status", (int)status);
        Add(command, "$started", Format(startedAt)); Add(command, "$completed", completedAt is null ? null : Format(completedAt.Value)); Add(command, "$summary", summary);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<ResultPage<StoredItem>> GetHistoryAsync(long hostSessionId, PageRequest page, CancellationToken cancellationToken = default)
    {
        ValidatePage(page);
        int offset = DecodeCursor(page.Cursor);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id, native_turn_id, native_item_id, parent_native_item_id, kind, status, title,
                   content_json, vendor_json, created_at, updated_at
            FROM items WHERE session_id=$session ORDER BY item_id LIMIT $limit OFFSET $offset;
            """;
        Add(command, "$session", hostSessionId);
        Add(command, "$limit", page.PageSize + 1);
        Add(command, "$offset", offset);
        var result = new List<StoredItem>(page.PageSize + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            IReadOnlyList<ContentBlock> content = JsonSerializer.Deserialize<IReadOnlyList<ContentBlock>>(reader.GetString(7), JsonOptions)
                ?? throw new InvalidDataException("Stored item content is invalid.");
            JsonElement? vendor = reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(8), JsonOptions);
            var item = new KernelItem(reader.GetString(2), (KernelItemKind)reader.GetInt32(4), (KernelItemStatus)reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), content, Parse(reader.GetString(9)), Parse(reader.GetString(10)),
                reader.IsDBNull(3) ? null : reader.GetString(3), vendor);
            result.Add(new StoredItem(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), item));
        }

        return Page(result, page.PageSize, offset);
    }

    public async Task<EventAppendResult> AppendEventAsync(string nativeEventId, KernelEvent kernelEvent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nativeEventId)) throw new ArgumentException("A native event identity is required.", nameof(nativeEventId));
        ArgumentNullException.ThrowIfNull(kernelEvent);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using System.Data.Common.DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long? insertedSequence;
        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO canonical_events(adapter_id, profile_id, native_event_id, native_session_id, native_turn_id,
                                             native_item_id, native_sequence, timestamp, kind, event_json)
                VALUES ($adapter, $profile, $identity, $session, $turn, $item, $sequence, $timestamp, $kind, $json)
                ON CONFLICT(adapter_id, profile_id, native_event_id) DO NOTHING
                RETURNING host_sequence;
                """;
            Add(insert, "$adapter", kernelEvent.AdapterId);
            Add(insert, "$profile", kernelEvent.ProfileId);
            Add(insert, "$identity", nativeEventId);
            Add(insert, "$session", kernelEvent.NativeSessionId);
            Add(insert, "$turn", kernelEvent.NativeTurnId);
            Add(insert, "$item", kernelEvent.NativeItemId);
            Add(insert, "$sequence", kernelEvent.Sequence);
            Add(insert, "$timestamp", Format(kernelEvent.Timestamp));
            Add(insert, "$kind", (int)kernelEvent.Kind);
            Add(insert, "$json", JsonSerializer.Serialize(kernelEvent, JsonOptions));
            object? scalar = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            insertedSequence = scalar is null ? null : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }

        long hostSequence;
        if (insertedSequence is long inserted)
        {
            hostSequence = inserted;
        }
        else
        {
            await using SqliteCommand existing = connection.CreateCommand();
            existing.Transaction = (SqliteTransaction)transaction;
            existing.CommandText = "SELECT host_sequence FROM canonical_events WHERE adapter_id=$adapter AND profile_id=$profile AND native_event_id=$identity;";
            Add(existing, "$adapter", kernelEvent.AdapterId);
            Add(existing, "$profile", kernelEvent.ProfileId);
            Add(existing, "$identity", nativeEventId);
            hostSequence = Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EventAppendResult(hostSequence, insertedSequence.HasValue);
    }

    public async Task<ResultPage<StoredCanonicalEvent>> GetEventsAsync(
        string? adapterId, string? profileId, string? nativeSessionId, PageRequest page, CancellationToken cancellationToken = default)
    {
        ValidatePage(page);
        int offset = DecodeCursor(page.Cursor);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT host_sequence, native_event_id, event_json FROM canonical_events
            WHERE ($adapter IS NULL OR adapter_id=$adapter)
              AND ($profile IS NULL OR profile_id=$profile)
              AND ($session IS NULL OR native_session_id=$session)
            ORDER BY host_sequence LIMIT $limit OFFSET $offset;
            """;
        Add(command, "$adapter", adapterId);
        Add(command, "$profile", profileId);
        Add(command, "$session", nativeSessionId);
        Add(command, "$limit", page.PageSize + 1);
        Add(command, "$offset", offset);
        var result = new List<StoredCanonicalEvent>(page.PageSize + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            KernelEvent item = JsonSerializer.Deserialize<KernelEvent>(reader.GetString(2), JsonOptions)
                ?? throw new InvalidDataException("Stored canonical event is invalid.");
            result.Add(new StoredCanonicalEvent(reader.GetInt64(0), reader.GetString(1), item));
        }

        return Page(result, page.PageSize, offset);
    }

    public async Task<IReadOnlyList<StoredCanonicalEvent>> GetEventsAfterAsync(
        long afterHostSequence,
        int limit = 256,
        string? adapterId = null,
        string? profileId = null,
        string? nativeSessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterHostSequence);
        if (limit is < 1 or > 2_048) throw new ArgumentOutOfRangeException(nameof(limit));
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT host_sequence, native_event_id, event_json FROM canonical_events
            WHERE host_sequence>$after
              AND ($adapter IS NULL OR adapter_id=$adapter)
              AND ($profile IS NULL OR profile_id=$profile)
              AND ($session IS NULL OR native_session_id=$session)
            ORDER BY host_sequence LIMIT $limit;
            """;
        Add(command, "$after", afterHostSequence);
        Add(command, "$adapter", adapterId);
        Add(command, "$profile", profileId);
        Add(command, "$session", nativeSessionId);
        Add(command, "$limit", limit);
        var result = new List<StoredCanonicalEvent>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            KernelEvent item = JsonSerializer.Deserialize<KernelEvent>(reader.GetString(2), JsonOptions)
                ?? throw new InvalidDataException("Stored canonical event is invalid.");
            result.Add(new StoredCanonicalEvent(reader.GetInt64(0), reader.GetString(1), item));
        }

        return result;
    }

    public async Task UpsertAdapterProfileAsync(StoredAdapterProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO adapter_profiles(adapter_id, profile_id, display_name, data_root, is_default, metadata_json, updated_at)
            VALUES ($adapter,$profile,$display,$root,$default,NULL,$updated)
            ON CONFLICT(adapter_id,profile_id) DO UPDATE SET display_name=excluded.display_name,data_root=excluded.data_root,
                is_default=excluded.is_default,metadata_json=NULL,updated_at=excluded.updated_at;
            """;
        Add(command, "$adapter", profile.AdapterId);
        Add(command, "$profile", profile.ProfileId);
        Add(command, "$display", profile.DisplayName);
        Add(command, "$root", profile.DataRoot);
        Add(command, "$default", profile.IsDefault ? 1 : 0);
        Add(command, "$updated", Format(profile.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> AddCheckpointAsync(string adapterId, string profileId, string? nativeSessionId, string markerKind, JsonElement marker, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO checkpoints(adapter_id,profile_id,native_session_id,marker_kind,marker_json,created_at) VALUES($adapter,$profile,$session,$kind,$json,$created) RETURNING checkpoint_id;";
        Add(command, "$adapter", adapterId); Add(command, "$profile", profileId); Add(command, "$session", nativeSessionId);
        Add(command, "$kind", markerKind); Add(command, "$json", marker.GetRawText()); Add(command, "$created", Format(DateTimeOffset.UtcNow));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task UpsertApprovalAsync(StoredApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO approvals(approval_id,adapter_id,profile_id,native_session_id,native_turn_id,status,request_json,response_json,created_at,resolved_at)
            VALUES($id,$adapter,$profile,$session,$turn,$status,$request,$response,$created,$resolved)
            ON CONFLICT(approval_id) DO UPDATE SET
                status=CASE
                    WHEN approvals.status IN ('resolved','abandoned','indeterminate') THEN approvals.status
                    WHEN approvals.status='responding' AND excluded.status='pending' THEN approvals.status
                    ELSE excluded.status
                END,
                response_json=COALESCE(approvals.response_json, excluded.response_json),
                resolved_at=COALESCE(approvals.resolved_at, excluded.resolved_at);
            """;
        Add(command, "$id", approval.ApprovalId); Add(command, "$adapter", approval.AdapterId); Add(command, "$profile", approval.ProfileId);
        Add(command, "$session", approval.NativeSessionId); Add(command, "$turn", approval.NativeTurnId); Add(command, "$status", approval.Status);
        Add(command, "$request", approval.Request.GetRawText()); Add(command, "$response", approval.Response?.GetRawText());
        Add(command, "$created", Format(approval.CreatedAt)); Add(command, "$resolved", approval.ResolvedAt is null ? null : Format(approval.ResolvedAt.Value));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredApproval?> GetApprovalAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(approvalId)) throw new ArgumentException("An approval identity is required.", nameof(approvalId));
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT approval_id,adapter_id,profile_id,native_session_id,native_turn_id,status,
                   request_json,response_json,created_at,resolved_at
            FROM approvals WHERE approval_id=$id;
            """;
        Add(command, "$id", approvalId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadApproval(reader) : null;
    }

    public async Task<IReadOnlyList<StoredApproval>> ListPendingApprovalsAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT approval_id,adapter_id,profile_id,native_session_id,native_turn_id,status,
                   request_json,response_json,created_at,resolved_at
            FROM approvals WHERE status='pending' ORDER BY created_at;
            """;
        var result = new List<StoredApproval>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadApproval(reader));
        return result;
    }

    public async Task UpsertElicitationAsync(StoredElicitation elicitation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(elicitation);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO elicitations(request_id,adapter_id,profile_id,native_session_id,native_turn_id,status,request_json,response_json,created_at,resolved_at)
            VALUES($id,$adapter,$profile,$session,$turn,$status,$request,$response,$created,$resolved)
            ON CONFLICT(request_id) DO UPDATE SET
                status=CASE
                    WHEN elicitations.status IN ('resolved','abandoned','indeterminate') THEN elicitations.status
                    WHEN elicitations.status='responding' AND excluded.status='pending' THEN elicitations.status
                    ELSE excluded.status
                END,
                response_json=COALESCE(elicitations.response_json, excluded.response_json),
                resolved_at=COALESCE(elicitations.resolved_at, excluded.resolved_at);
            """;
        Add(command, "$id", elicitation.RequestId); Add(command, "$adapter", elicitation.AdapterId); Add(command, "$profile", elicitation.ProfileId);
        Add(command, "$session", elicitation.NativeSessionId); Add(command, "$turn", elicitation.NativeTurnId); Add(command, "$status", elicitation.Status);
        Add(command, "$request", elicitation.Request.GetRawText()); Add(command, "$response", elicitation.Response?.GetRawText());
        Add(command, "$created", Format(elicitation.CreatedAt)); Add(command, "$resolved", elicitation.ResolvedAt is null ? null : Format(elicitation.ResolvedAt.Value));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredElicitation?> GetElicitationAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("An elicitation identity is required.", nameof(requestId));
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_id,adapter_id,profile_id,native_session_id,native_turn_id,status,
                   request_json,response_json,created_at,resolved_at
            FROM elicitations WHERE request_id=$id;
            """;
        Add(command, "$id", requestId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadElicitation(reader) : null;
    }

    public async Task<IReadOnlyList<StoredElicitation>> ListPendingElicitationsAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_id,adapter_id,profile_id,native_session_id,native_turn_id,status,
                   request_json,response_json,created_at,resolved_at
            FROM elicitations WHERE status='pending' ORDER BY created_at;
            """;
        var result = new List<StoredElicitation>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadElicitation(reader));
        return result;
    }

    public async Task<RecoverySweepResult> RecoverInterruptedOperationsAsync(
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using System.Data.Common.DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int turns = await ExecuteRecoveryAsync(
            connection,
            (SqliteTransaction)transaction,
            "UPDATE turns SET status=$failed, completed_at=$at, summary=COALESCE(summary,$summary) WHERE status IN ($running,$waiting);",
            recoveredAt,
            command =>
            {
                Add(command, "$failed", (int)TurnStatus.Failed);
                Add(command, "$running", (int)TurnStatus.Running);
                Add(command, "$waiting", (int)TurnStatus.WaitingForApproval);
                Add(command, "$summary", "Interrupted by a previous host shutdown or crash; resume the session to continue safely.");
            },
            cancellationToken).ConfigureAwait(false);
        int sessions = await ExecuteRecoveryAsync(
            connection,
            (SqliteTransaction)transaction,
            "UPDATE sessions SET status=$paused, updated_at=$at WHERE status IN ($running,$waiting);",
            recoveredAt,
            command =>
            {
                Add(command, "$paused", (int)SessionStatus.Paused);
                Add(command, "$running", (int)SessionStatus.Running);
                Add(command, "$waiting", (int)SessionStatus.WaitingForApproval);
            },
            cancellationToken).ConfigureAwait(false);
        int approvals = await ExecuteRecoveryAsync(
            connection,
            (SqliteTransaction)transaction,
            "UPDATE approvals SET status='abandoned', resolved_at=$at WHERE status IN ('pending','responding');",
            recoveredAt,
            null,
            cancellationToken).ConfigureAwait(false);
        int elicitations = await ExecuteRecoveryAsync(
            connection,
            (SqliteTransaction)transaction,
            "UPDATE elicitations SET status='abandoned', resolved_at=$at WHERE status IN ('pending','responding');",
            recoveredAt,
            null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RecoverySweepResult(turns, sessions, approvals, elicitations);
    }

    private static StoredSession ReadSession(SqliteDataReader reader)
    {
        var sessionRef = new SessionRef(reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
        IReadOnlyDictionary<string, string>? metadata = reader.IsDBNull(11) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(11), JsonOptions);
        return new StoredSession(reader.GetInt64(0), new KernelSessionSummary(sessionRef, reader.GetString(6), (SessionStatus)reader.GetInt32(7),
            Parse(reader.GetString(8)), Parse(reader.GetString(9)), reader.IsDBNull(10) ? null : reader.GetString(10), metadata));
    }

    private static StoredApproval ReadApproval(SqliteDataReader reader)
    {
        JsonElement request = JsonSerializer.Deserialize<JsonElement>(reader.GetString(6), JsonOptions);
        JsonElement? response = reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(7), JsonOptions);
        return new StoredApproval(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            request,
            response,
            Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Parse(reader.GetString(9)));
    }

    private static StoredElicitation ReadElicitation(SqliteDataReader reader)
    {
        JsonElement request = JsonSerializer.Deserialize<JsonElement>(reader.GetString(6), JsonOptions);
        JsonElement? response = reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(7), JsonOptions);
        return new StoredElicitation(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            request,
            response,
            Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Parse(reader.GetString(9)));
    }

    private static async Task<int> ExecuteRecoveryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        DateTimeOffset recoveredAt,
        Action<SqliteCommand>? configure,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Add(command, "$at", Format(recoveredAt));
        configure?.Invoke(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ResultPage<T> Page<T>(List<T> result, int pageSize, int offset)
    {
        bool hasMore = result.Count > pageSize;
        if (hasMore) result.RemoveAt(result.Count - 1);
        return new ResultPage<T>(result, hasMore ? EncodeCursor(checked(offset + pageSize)) : null, hasMore);
    }

    private static void ValidatePage(PageRequest page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.PageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(page), "Page size must be between 1 and 500.");
    }

    private static int DecodeCursor(string? cursor)
    {
        if (cursor is null) return 0;
        try
        {
            string text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int offset) || offset < 0) throw new FormatException();
            return offset;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The page cursor is invalid.", nameof(cursor), exception);
        }
    }

    private static string EncodeCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}

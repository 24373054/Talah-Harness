using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Talah.Harness.Persistence;

public sealed record HarnessDatabaseOptions(
    string DatabasePath,
    TimeSpan? BusyTimeout = null,
    string? BackupDirectory = null);

public sealed record DatabaseInitializationResult(int SchemaVersion, string? BackupPath, string IntegrityResult);

public sealed class HarnessMigrationException(string message, int sourceVersion, int targetVersion, Exception innerException) : Exception(message, innerException)
{
    public int SourceVersion { get; } = sourceVersion;
    public int TargetVersion { get; } = targetVersion;
}

public sealed class HarnessDatabase
{
    public const int CurrentSchemaVersion = 2;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MigrationLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _connectionString;
    private readonly int _busyTimeoutMilliseconds;

    public HarnessDatabase(HarnessDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(options.DatabasePath)) throw new ArgumentException("The database path must be absolute.", nameof(options));
        Options = options with { DatabasePath = Path.GetFullPath(options.DatabasePath) };
        TimeSpan timeout = options.BusyTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(options), "Busy timeout must be positive and fit in milliseconds.");
        _busyTimeoutMilliseconds = (int)timeout.TotalMilliseconds;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds))
        }.ToString();
    }

    public HarnessDatabaseOptions Options { get; }

    public async Task<DatabaseInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        SemaphoreSlim migrationLock = MigrationLocks.GetOrAdd(Options.DatabasePath, static _ => new SemaphoreSlim(1, 1));
        await migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = Path.GetDirectoryName(Options.DatabasePath)!;
            Directory.CreateDirectory(directory);
            bool existed = File.Exists(Options.DatabasePath) && new FileInfo(Options.DatabasePath).Length > 0;
            int version;
            string integrity;
            await using (SqliteConnection inspection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            {
                version = await ScalarIntAsync(inspection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
                integrity = await ScalarStringAsync(inspection, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false);
            }

            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new HarnessMigrationException($"SQLite integrity check failed before migration: {integrity}", version, CurrentSchemaVersion, new InvalidDataException(integrity));
            if (version > CurrentSchemaVersion)
                throw new HarnessMigrationException($"Database schema v{version} is newer than supported v{CurrentSchemaVersion}.", version, CurrentSchemaVersion, new NotSupportedException());

            string? backup = null;
            if (version < CurrentSchemaVersion)
            {
                if (existed)
                {
                    try { backup = CreateBackup(version, cancellationToken); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        throw new HarnessMigrationException("Could not create the required pre-migration database backup; migration was not attempted.", version, CurrentSchemaVersion, exception);
                    }
                }
                try
                {
                    await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await using System.Data.Common.DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    for (int targetVersion = version + 1; targetVersion <= CurrentSchemaVersion; targetVersion++)
                        await ExecuteAsync(connection, transaction, Migration(targetVersion), cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not HarnessMigrationException)
                {
                    throw new HarnessMigrationException(
                        $"Could not migrate the canonical store from schema v{version} to v{CurrentSchemaVersion}. A backup is available at '{backup ?? "not required for a new database"}'.",
                        version, CurrentSchemaVersion, exception);
                }
            }

            await using SqliteConnection validation = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            int finalVersion = await ScalarIntAsync(validation, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
            string finalIntegrity = await ScalarStringAsync(validation, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false);
            if (finalVersion != CurrentSchemaVersion || !string.Equals(finalIntegrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new HarnessMigrationException("Database validation failed after migration.", version, CurrentSchemaVersion, new InvalidDataException(finalIntegrity));
            return new DatabaseInitializationResult(finalVersion, backup, finalIntegrity);
        }
        finally
        {
            migrationLock.Release();
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={_busyTimeoutMilliseconds}; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string CreateBackup(int sourceVersion, CancellationToken cancellationToken)
    {
        string directory = Options.BackupDirectory is null
            ? Path.Combine(Path.GetDirectoryName(Options.DatabasePath)!, "backups")
            : Path.GetFullPath(Options.BackupDirectory);
        Directory.CreateDirectory(directory);
        string baseName = Path.GetFileName(Options.DatabasePath);
        string path = Path.Combine(directory, $"{baseName}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.v{sourceVersion}.bak");
        cancellationToken.ThrowIfCancellationRequested();
        using var source = new SqliteConnection(_connectionString);
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        cancellationToken.ThrowIfCancellationRequested();
        return path;
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Migration(int targetVersion) => targetVersion switch
    {
        1 => SchemaV1,
        2 => SchemaV2,
        _ => throw new InvalidOperationException($"No database migration is registered for schema v{targetVersion}.")
    };

    private const string SchemaV1 = """
        CREATE TABLE schema_version (
            version INTEGER PRIMARY KEY,
            applied_at TEXT NOT NULL
        ) STRICT;
        INSERT INTO schema_version(version, applied_at) VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));

        CREATE TABLE adapter_profiles (
            adapter_id TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            data_root TEXT NOT NULL,
            is_default INTEGER NOT NULL CHECK(is_default IN (0,1)),
            metadata_json TEXT,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(adapter_id, profile_id)
        ) STRICT;

        CREATE TABLE workspaces (
            workspace_id TEXT PRIMARY KEY,
            root_path TEXT NOT NULL,
            additional_roots_json TEXT NOT NULL,
            is_trusted INTEGER NOT NULL CHECK(is_trusted IN (0,1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        ) STRICT;

        CREATE TABLE sessions (
            session_id INTEGER PRIMARY KEY AUTOINCREMENT,
            adapter_id TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            native_session_id TEXT NOT NULL,
            parent_native_session_id TEXT,
            workspace_id TEXT,
            title TEXT NOT NULL,
            status INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            preview TEXT,
            metadata_json TEXT,
            UNIQUE(adapter_id, profile_id, native_session_id)
        ) STRICT;
        CREATE INDEX ix_sessions_updated ON sessions(updated_at DESC, session_id DESC);

        CREATE TABLE turns (
            turn_id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id INTEGER NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
            native_turn_id TEXT NOT NULL,
            status INTEGER NOT NULL,
            started_at TEXT NOT NULL,
            completed_at TEXT,
            summary TEXT,
            UNIQUE(session_id, native_turn_id)
        ) STRICT;

        CREATE TABLE items (
            item_id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id INTEGER NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
            native_turn_id TEXT,
            native_item_id TEXT NOT NULL,
            parent_native_item_id TEXT,
            kind INTEGER NOT NULL,
            status INTEGER NOT NULL,
            title TEXT,
            content_json TEXT NOT NULL,
            vendor_json TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(session_id, native_item_id)
        ) STRICT;
        CREATE INDEX ix_items_history ON items(session_id, item_id);

        CREATE TABLE canonical_events (
            host_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            adapter_id TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            native_event_id TEXT NOT NULL,
            native_session_id TEXT,
            native_turn_id TEXT,
            native_item_id TEXT,
            native_sequence INTEGER NOT NULL,
            timestamp TEXT NOT NULL,
            kind INTEGER NOT NULL,
            event_json TEXT NOT NULL,
            UNIQUE(adapter_id, profile_id, native_event_id)
        ) STRICT;
        CREATE INDEX ix_events_session ON canonical_events(adapter_id, profile_id, native_session_id, host_sequence);

        CREATE TABLE checkpoints (
            checkpoint_id INTEGER PRIMARY KEY AUTOINCREMENT,
            adapter_id TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            native_session_id TEXT,
            marker_kind TEXT NOT NULL,
            marker_json TEXT NOT NULL,
            created_at TEXT NOT NULL
        ) STRICT;

        CREATE TABLE approvals (
            approval_id TEXT PRIMARY KEY,
            adapter_id TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            native_session_id TEXT,
            native_turn_id TEXT,
            status TEXT NOT NULL,
            request_json TEXT NOT NULL,
            response_json TEXT,
            created_at TEXT NOT NULL,
            resolved_at TEXT
        ) STRICT;
        CREATE INDEX ix_approvals_pending ON approvals(status, created_at);

        PRAGMA user_version=1;
        """;

    private const string SchemaV2 = """
        CREATE TABLE elicitations (
            request_id TEXT PRIMARY KEY,
            adapter_id TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            native_session_id TEXT,
            native_turn_id TEXT,
            status TEXT NOT NULL,
            request_json TEXT NOT NULL,
            response_json TEXT,
            created_at TEXT NOT NULL,
            resolved_at TEXT
        ) STRICT;
        CREATE INDEX ix_elicitations_pending ON elicitations(status, created_at);

        INSERT INTO schema_version(version, applied_at) VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
        PRAGMA user_version=2;
        """;
}

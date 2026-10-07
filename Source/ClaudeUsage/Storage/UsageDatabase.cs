namespace ClaudeUsage.Storage;

/// <summary>
/// 	Opens the SQLite usage database and creates its schema.
/// </summary>
internal static class UsageDatabase
{
	private const string Schema = """
		CREATE TABLE IF NOT EXISTS sessions (
			session_id           TEXT PRIMARY KEY,
			project_name         TEXT,
			first_timestamp      TEXT,
			last_timestamp       TEXT,
			git_branch           TEXT,
			total_input_tokens   INTEGER DEFAULT 0,
			total_output_tokens  INTEGER DEFAULT 0,
			total_cache_read     INTEGER DEFAULT 0,
			total_cache_creation INTEGER DEFAULT 0,
			model                TEXT,
			turn_count           INTEGER DEFAULT 0,
			topic                TEXT
		);

		CREATE TABLE IF NOT EXISTS turns (
			id                    INTEGER PRIMARY KEY AUTOINCREMENT,
			session_id            TEXT,
			timestamp             TEXT,
			model                 TEXT,
			input_tokens          INTEGER DEFAULT 0,
			output_tokens         INTEGER DEFAULT 0,
			cache_read_tokens     INTEGER DEFAULT 0,
			cache_creation_tokens INTEGER DEFAULT 0,
			tool_name             TEXT,
			cwd                   TEXT,
			message_id            TEXT,
			is_subagent           INTEGER DEFAULT 0,
			agent_id              TEXT
		);

		CREATE TABLE IF NOT EXISTS processed_files (
			path        TEXT PRIMARY KEY,
			mtime       REAL,
			byte_offset INTEGER
		);

		CREATE TABLE IF NOT EXISTS agents (
			agent_id              TEXT PRIMARY KEY,
			agent_type            TEXT,
			dispatched_in_session TEXT,
			completed_at          TEXT,
			status                TEXT,
			total_tokens          INTEGER,
			total_duration_ms     INTEGER,
			tool_use_count        INTEGER
		);

		CREATE INDEX IF NOT EXISTS idx_turns_session ON turns(session_id);
		CREATE INDEX IF NOT EXISTS idx_turns_timestamp ON turns(timestamp);
		CREATE INDEX IF NOT EXISTS idx_turns_subagent ON turns(is_subagent);
		CREATE INDEX IF NOT EXISTS idx_turns_agent_id ON turns(agent_id);
		CREATE INDEX IF NOT EXISTS idx_sessions_first ON sessions(first_timestamp);
		CREATE INDEX IF NOT EXISTS idx_agents_type ON agents(agent_type);
		CREATE UNIQUE INDEX IF NOT EXISTS idx_turns_message_id
			ON turns(message_id) WHERE message_id IS NOT NULL AND message_id != '';
		""";

	/// <summary>
	/// 	Opens the database for writing, and creates the file and schema if necessary.
	/// </summary>
	/// <param name="databasePath">
	/// 	The path of the database file.
	/// </param>
	/// <returns>
	/// 	An open connection.
	/// </returns>
	public static SqliteConnection OpenForWrite(string databasePath)
	{
		string? directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
		if (directory is not null)
		{
			Directory.CreateDirectory(directory);
		}

		SqliteConnection connection = Open(databasePath, SqliteOpenMode.ReadWriteCreate);

		// Write-ahead logging lets the dashboard read while a scan writes.
		Execute(connection, "PRAGMA journal_mode = WAL;");
		Execute(connection, Schema);
		return connection;
	}

	/// <summary>
	/// 	Opens an existing database for reading.
	/// </summary>
	/// <param name="databasePath">
	/// 	The path of the database file.
	/// </param>
	/// <returns>
	/// 	An open connection.
	/// </returns>
	public static SqliteConnection OpenForRead(string databasePath)
	{
		return Open(databasePath, SqliteOpenMode.ReadOnly);
	}

	/// <summary>
	/// 	Runs SQL that returns no rows.
	/// </summary>
	/// <param name="connection">
	/// 	The open connection.
	/// </param>
	/// <param name="sql">
	/// 	The SQL text.
	/// </param>
	/// <param name="transaction">
	/// 	The active transaction, if there is one.
	/// </param>
	/// <returns>
	/// 	The number of changed rows.
	/// </returns>
	public static int Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;
		command.Transaction = transaction;
		return command.ExecuteNonQuery();
	}

	private static SqliteConnection Open(string databasePath, SqliteOpenMode mode)
	{
		SqliteConnectionStringBuilder builder = new()
		{
			DataSource = databasePath,
			Mode = mode,

			// Wait for a lock instead of a "database is locked" error.
			DefaultTimeout = 30,

			// No pool, so the file is released when the connection closes.
			Pooling = false,
		};

		SqliteConnection connection = new(builder.ConnectionString);
		connection.Open();
		return connection;
	}
}

using ClaudeUsage.Storage;
using Microsoft.Data.Sqlite;

namespace ClaudeUsage.Scanning;

/// <summary>
/// 	The counts from one scan.
/// </summary>
internal sealed record ScanSummary(int New, int Updated, int Skipped, int Turns, int Sessions);

/// <summary>
/// 	Scans Claude Code transcripts and stores their usage in the database.
/// </summary>
/// <remarks>
/// 	The scan is incremental.
/// 	It keeps the modification time and the processed byte offset of each file.
/// 	An unchanged file is skipped, and a file that grew is read only from its stored offset.
/// 	The database is append-only: it keeps history after Claude Code deletes old transcripts.
/// </remarks>
internal static class UsageScanner
{
	// Only one scan at a time (ex. the background scan at start and a Rescan click).
	private static readonly SemaphoreSlim _scanGate = new(1, 1);

	private static readonly Dictionary<string, int> _modelPriority = new(StringComparer.Ordinal)
	{
		["fable"] = 5,
		["mythos"] = 5,
		["opus"] = 3,
		["sonnet"] = 2,
		["haiku"] = 1,
	};

	/// <summary>
	/// 	Scans all JSONL files in the directories and updates the database.
	/// </summary>
	/// <param name="databasePath">
	/// 	The path of the database file.
	/// </param>
	/// <param name="projectDirectories">
	/// 	The directories to scan, recursively.
	/// 	A directory that does not exist is skipped.
	/// </param>
	/// <param name="log">
	/// 	The writer for progress messages, or null for no messages.
	/// </param>
	/// <returns>
	/// 	The scan counts.
	/// </returns>
	public static ScanSummary Scan(string databasePath, IEnumerable<string> projectDirectories, TextWriter? log = null)
	{
		_scanGate.Wait();
		try
		{
			return ScanCore(databasePath, projectDirectories, log);
		}
		finally
		{
			_scanGate.Release();
		}
	}

	private static ScanSummary ScanCore(string databasePath, IEnumerable<string> projectDirectories, TextWriter? log)
	{
		using SqliteConnection connection = UsageDatabase.OpenForWrite(databasePath);

		List<string> files = [];
		EnumerationOptions enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
		foreach (string directory in projectDirectories)
		{
			if (Directory.Exists(directory) is false)
			{
				continue;
			}

			log?.WriteLine($"Scanning {directory} ...");
			files.AddRange(Directory.EnumerateFiles(directory, "*.jsonl", enumeration));
		}

		files.Sort(StringComparer.Ordinal);

		int newFiles = 0;
		int updatedFiles = 0;
		int skippedFiles = 0;
		int totalTurns = 0;
		HashSet<string> sessionsSeen = new(StringComparer.Ordinal);

		foreach (string file in files)
		{
			double modifiedTime;
			long length;
			try
			{
				FileInfo info = new(file);
				modifiedTime = (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds;
				length = info.Length;
			}
			catch (IOException)
			{
				continue;
			}

			(double StoredTime, long StoredOffset)? stored = ReadProcessedFile(connection, file);
			if (stored is { } previous && Math.Abs(previous.StoredTime - modifiedTime) < 0.01)
			{
				skippedFiles++;
				continue;
			}

			bool isNew = stored is null;

			// A file that is shorter than the stored offset was rewritten, so read it again from the start.
			long startOffset = stored is { } known && known.StoredOffset <= length ? known.StoredOffset : 0;
			log?.WriteLine($"  [{(isNew ? "NEW" : "UPD")}] {file}");

			ParseResult result;
			try
			{
				result = TranscriptParser.ParseFile(file, startOffset);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				log?.WriteLine($"  Warning: error reading {file}: {exception.Message}");
				continue;
			}

			using SqliteTransaction transaction = connection.BeginTransaction();
			if (isNew is false && result.EndOffset == startOffset)
			{
				// The time changed, but the file has no new complete lines.
				SaveProcessedFile(connection, transaction, file, modifiedTime, startOffset);
				transaction.Commit();
				skippedFiles++;
				continue;
			}

			UpsertAgents(connection, transaction, result.Agents);
			if (TranscriptParser.ReadSubagentMeta(file) is AgentDispatch metaDispatch)
			{
				InsertAgentTypeIfMissing(connection, transaction, metaDispatch);
			}

			UpsertSessions(connection, transaction, result.Sessions);
			InsertTurns(connection, transaction, result.Turns);
			SaveProcessedFile(connection, transaction, file, modifiedTime, result.EndOffset);
			transaction.Commit();

			foreach (SessionInfo session in result.Sessions)
			{
				sessionsSeen.Add(session.SessionId);
			}

			totalTurns += result.Turns.Count;
			if (isNew)
			{
				newFiles++;
			}
			else
			{
				updatedFiles++;
			}
		}

		if (newFiles + updatedFiles > 0)
		{
			RecomputeSessionTotals(connection);
		}

		log?.WriteLine();
		log?.WriteLine("Scan complete:");
		log?.WriteLine($"  New files:     {newFiles}");
		log?.WriteLine($"  Updated files: {updatedFiles}");
		log?.WriteLine($"  Skipped files: {skippedFiles}");
		log?.WriteLine($"  Turns added:   {totalTurns}");
		log?.WriteLine($"  Sessions seen: {sessionsSeen.Count}");

		return new ScanSummary(newFiles, updatedFiles, skippedFiles, totalTurns, sessionsSeen.Count);
	}

	/// <summary>
	/// 	Gets the priority of a model family, used to pick the main model of a session.
	/// </summary>
	/// <param name="model">
	/// 	The model id.
	/// </param>
	/// <returns>
	/// 	A higher number for a more capable family, or 0 for an unknown model.
	/// </returns>
	public static int ModelPriority(string? model)
	{
		if (string.IsNullOrEmpty(model))
		{
			return 0;
		}

		foreach ((string keyword, int priority) in _modelPriority)
		{
			if (model.Contains(keyword, StringComparison.OrdinalIgnoreCase))
			{
				return priority;
			}
		}

		return 0;
	}

	private static (double, long)? ReadProcessedFile(SqliteConnection connection, string file)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT mtime, byte_offset FROM processed_files WHERE path = $path";
		command.Parameters.AddWithValue("$path", file);
		using SqliteDataReader reader = command.ExecuteReader();
		if (reader.Read() is false)
		{
			return null;
		}

		return (reader.GetDouble(0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1));
	}

	private static void SaveProcessedFile(SqliteConnection connection, SqliteTransaction transaction, string file, double modifiedTime, long offset)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "INSERT OR REPLACE INTO processed_files (path, mtime, byte_offset) VALUES ($path, $mtime, $offset)";
		command.Parameters.AddWithValue("$path", file);
		command.Parameters.AddWithValue("$mtime", modifiedTime);
		command.Parameters.AddWithValue("$offset", offset);
		command.ExecuteNonQuery();
	}

	private static void UpsertAgents(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<AgentDispatch> agents)
	{
		if (agents.Count == 0)
		{
			return;
		}

		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO agents
				(agent_id, agent_type, dispatched_in_session, completed_at,
				 status, total_tokens, total_duration_ms, tool_use_count)
			VALUES ($id, $type, $session, $completed, $status, $tokens, $duration, $tools)
			ON CONFLICT(agent_id) DO UPDATE SET
				agent_type            = excluded.agent_type,
				dispatched_in_session = excluded.dispatched_in_session,
				completed_at          = excluded.completed_at,
				status                = excluded.status,
				total_tokens          = excluded.total_tokens,
				total_duration_ms     = excluded.total_duration_ms,
				tool_use_count        = excluded.tool_use_count
			""";
		SqliteParameter id = command.Parameters.Add("$id", SqliteType.Text);
		SqliteParameter type = command.Parameters.Add("$type", SqliteType.Text);
		SqliteParameter session = command.Parameters.Add("$session", SqliteType.Text);
		SqliteParameter completed = command.Parameters.Add("$completed", SqliteType.Text);
		SqliteParameter status = command.Parameters.Add("$status", SqliteType.Text);
		SqliteParameter tokens = command.Parameters.Add("$tokens", SqliteType.Integer);
		SqliteParameter duration = command.Parameters.Add("$duration", SqliteType.Integer);
		SqliteParameter tools = command.Parameters.Add("$tools", SqliteType.Integer);

		foreach (AgentDispatch agent in agents)
		{
			id.Value = agent.AgentId;
			type.Value = agent.AgentType;
			session.Value = (object?)agent.DispatchedInSession ?? DBNull.Value;
			completed.Value = agent.CompletedAt;
			status.Value = (object?)agent.Status ?? DBNull.Value;
			tokens.Value = (object?)agent.TotalTokens ?? DBNull.Value;
			duration.Value = (object?)agent.TotalDurationMs ?? DBNull.Value;
			tools.Value = (object?)agent.ToolUseCount ?? DBNull.Value;
			command.ExecuteNonQuery();
		}
	}

	private static void InsertAgentTypeIfMissing(SqliteConnection connection, SqliteTransaction transaction, AgentDispatch agent)
	{
		// The tool result of the parent has more data (status, duration, tool uses), so it is not replaced here.
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO agents (agent_id, agent_type, dispatched_in_session)
			VALUES ($id, $type, $session)
			ON CONFLICT(agent_id) DO UPDATE SET
				agent_type            = COALESCE(agents.agent_type, excluded.agent_type),
				dispatched_in_session = COALESCE(agents.dispatched_in_session, excluded.dispatched_in_session)
			""";
		command.Parameters.AddWithValue("$id", agent.AgentId);
		command.Parameters.AddWithValue("$type", agent.AgentType);
		command.Parameters.AddWithValue("$session", (object?)agent.DispatchedInSession ?? DBNull.Value);
		command.ExecuteNonQuery();
	}

	private static void UpsertSessions(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<SessionInfo> sessions)
	{
		foreach (SessionInfo session in sessions)
		{
			(string? Model, string? Topic)? existing = ReadSession(connection, transaction, session.SessionId);
			if (existing is null)
			{
				// A session seen only through a title record has no content, so it gets no row.
				if (session.FirstTimestamp.Length == 0)
				{
					continue;
				}

				InsertSession(connection, transaction, session);
			}
			else
			{
				UpdateSession(connection, transaction, session, existing.Value.Model, existing.Value.Topic);
			}
		}
	}

	private static (string?, string?)? ReadSession(SqliteConnection connection, SqliteTransaction transaction, string sessionId)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT model, topic FROM sessions WHERE session_id = $id";
		command.Parameters.AddWithValue("$id", sessionId);
		using SqliteDataReader reader = command.ExecuteReader();
		if (reader.Read() is false)
		{
			return null;
		}

		return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
	}

	private static void InsertSession(SqliteConnection connection, SqliteTransaction transaction, SessionInfo session)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO sessions
				(session_id, project_name, first_timestamp, last_timestamp, git_branch,
				 total_input_tokens, total_output_tokens, total_cache_read, total_cache_creation,
				 model, turn_count, topic)
			VALUES ($id, $project, $first, $last, $branch, $input, $output, $cacheRead, $cacheCreation,
				$model, $turns, $topic)
			""";
		command.Parameters.AddWithValue("$id", session.SessionId);
		command.Parameters.AddWithValue("$project", session.ProjectName);
		command.Parameters.AddWithValue("$first", session.FirstTimestamp);
		command.Parameters.AddWithValue("$last", session.LastTimestamp);
		command.Parameters.AddWithValue("$branch", session.GitBranch);
		command.Parameters.AddWithValue("$input", session.TotalInputTokens);
		command.Parameters.AddWithValue("$output", session.TotalOutputTokens);
		command.Parameters.AddWithValue("$cacheRead", session.TotalCacheRead);
		command.Parameters.AddWithValue("$cacheCreation", session.TotalCacheCreation);
		command.Parameters.AddWithValue("$model", (object?)session.Model ?? DBNull.Value);
		command.Parameters.AddWithValue("$turns", session.TurnCount);
		command.Parameters.AddWithValue("$topic", (object?)session.Topic ?? DBNull.Value);
		command.ExecuteNonQuery();
	}

	private static void UpdateSession(SqliteConnection connection, SqliteTransaction transaction, SessionInfo session, string? existingModel, string? existingTopic)
	{
		// Keep the most capable model (ex. Opus over the Haiku turns of a subagent).
		string? model = ModelPriority(session.Model) > ModelPriority(existingModel) ? session.Model : existingModel;
		string? topic = string.IsNullOrEmpty(session.Topic) ? existingTopic : session.Topic;

		// Token totals are not added here: RecomputeSessionTotals sets them from the turns table.
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			UPDATE sessions SET
				first_timestamp = CASE
					WHEN $first <> '' AND (first_timestamp IS NULL OR first_timestamp = '' OR $first < first_timestamp)
					THEN $first ELSE first_timestamp END,
				last_timestamp = CASE
					WHEN $last <> '' AND (last_timestamp IS NULL OR $last > last_timestamp)
					THEN $last ELSE last_timestamp END,
				project_name = CASE
					WHEN (project_name IS NULL OR project_name = 'unknown') AND $project <> 'unknown'
					THEN $project ELSE project_name END,
				git_branch = CASE
					WHEN (git_branch IS NULL OR git_branch = '') THEN $branch ELSE git_branch END,
				model = $model,
				topic = $topic
			WHERE session_id = $id
			""";
		command.Parameters.AddWithValue("$id", session.SessionId);
		command.Parameters.AddWithValue("$first", session.FirstTimestamp);
		command.Parameters.AddWithValue("$last", session.LastTimestamp);
		command.Parameters.AddWithValue("$project", session.ProjectName);
		command.Parameters.AddWithValue("$branch", session.GitBranch);
		command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
		command.Parameters.AddWithValue("$topic", (object?)topic ?? DBNull.Value);
		command.ExecuteNonQuery();
	}

	private static void InsertTurns(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<TurnRecord> turns)
	{
		if (turns.Count == 0)
		{
			return;
		}

		// INSERT OR IGNORE plus the unique message_id index prevents duplicate turns when a file is read again.
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT OR IGNORE INTO turns
				(session_id, timestamp, model, input_tokens, output_tokens,
				 cache_read_tokens, cache_creation_tokens, tool_name, cwd, message_id,
				 is_subagent, agent_id)
			VALUES ($session, $timestamp, $model, $input, $output, $cacheRead, $cacheCreation,
				$tool, $cwd, $messageId, $subagent, $agentId)
			""";
		SqliteParameter session = command.Parameters.Add("$session", SqliteType.Text);
		SqliteParameter timestamp = command.Parameters.Add("$timestamp", SqliteType.Text);
		SqliteParameter model = command.Parameters.Add("$model", SqliteType.Text);
		SqliteParameter input = command.Parameters.Add("$input", SqliteType.Integer);
		SqliteParameter output = command.Parameters.Add("$output", SqliteType.Integer);
		SqliteParameter cacheRead = command.Parameters.Add("$cacheRead", SqliteType.Integer);
		SqliteParameter cacheCreation = command.Parameters.Add("$cacheCreation", SqliteType.Integer);
		SqliteParameter tool = command.Parameters.Add("$tool", SqliteType.Text);
		SqliteParameter cwd = command.Parameters.Add("$cwd", SqliteType.Text);
		SqliteParameter messageId = command.Parameters.Add("$messageId", SqliteType.Text);
		SqliteParameter subagent = command.Parameters.Add("$subagent", SqliteType.Integer);
		SqliteParameter agentId = command.Parameters.Add("$agentId", SqliteType.Text);

		foreach (TurnRecord turn in turns)
		{
			session.Value = turn.SessionId;
			timestamp.Value = turn.Timestamp;
			model.Value = turn.Model;
			input.Value = turn.InputTokens;
			output.Value = turn.OutputTokens;
			cacheRead.Value = turn.CacheReadTokens;
			cacheCreation.Value = turn.CacheCreationTokens;
			tool.Value = (object?)turn.ToolName ?? DBNull.Value;
			cwd.Value = turn.Cwd;
			messageId.Value = turn.MessageId;
			subagent.Value = turn.IsSubagent ? 1 : 0;
			agentId.Value = (object?)turn.AgentId ?? DBNull.Value;
			command.ExecuteNonQuery();
		}
	}

	private static void RecomputeSessionTotals(SqliteConnection connection)
	{
		// Session totals always come from the stored turns, so a duplicate turn can never count twice.
		UsageDatabase.Execute(connection, """
			UPDATE sessions SET
				total_input_tokens   = totals.input,
				total_output_tokens  = totals.output,
				total_cache_read     = totals.cache_read,
				total_cache_creation = totals.cache_creation,
				turn_count           = totals.turns
			FROM (
				SELECT session_id,
					SUM(input_tokens)          AS input,
					SUM(output_tokens)         AS output,
					SUM(cache_read_tokens)     AS cache_read,
					SUM(cache_creation_tokens) AS cache_creation,
					COUNT(*)                   AS turns
				FROM turns
				GROUP BY session_id
			) AS totals
			WHERE sessions.session_id = totals.session_id
			""");
	}
}

using System.Globalization;
using ClaudeUsage.Storage;
using Microsoft.Data.Sqlite;

namespace ClaudeUsage.Dashboard;

// The property names become snake_case JSON keys that index.html reads (ex. CacheRead -> cache_read).

internal sealed record DailyModelRow(string Day, string Model, long Input, long Output, long CacheRead, long CacheCreation, long Turns);

internal sealed record HourlyModelRow(string Day, int Hour, string Model, long Output, long Turns);

internal sealed record SessionRow(
	string SessionId,
	string Project,
	string Branch,
	string Topic,
	string Last,
	string LastDate,
	double DurationMin,
	string Model,
	long Turns,
	long Input,
	long Output,
	long CacheRead,
	long CacheCreation);

internal sealed record SubagentTypeRow(
	string Day,
	string AgentType,
	string Model,
	long Input,
	long Output,
	long CacheRead,
	long CacheCreation,
	long Dispatches,
	long Turns);

internal sealed record DispatchRow(
	string AgentId,
	string AgentType,
	string Model,
	string Start,
	string StartDate,
	long Input,
	long Output,
	long CacheRead,
	long CacheCreation,
	long Turns,
	long? DurationMs,
	long? ToolUses,
	string? Status);

/// <summary>
/// 	All data for the dashboard page.
/// 	The page filters it by date range and model in the browser.
/// </summary>
internal sealed record DashboardPayload(
	IReadOnlyList<string> AllModels,
	IReadOnlyList<DailyModelRow> DailyByModel,
	IReadOnlyList<HourlyModelRow> HourlyByModel,
	IReadOnlyList<SessionRow> SessionsAll,
	IReadOnlyList<SubagentTypeRow> SubagentByType,
	IReadOnlyList<DispatchRow> TopDispatches,
	string GeneratedAt);

/// <summary>
/// 	The response when the dashboard has no data yet.
/// 	The page shows the message and tries again.
/// </summary>
internal sealed record DashboardError(string Error);

/// <summary>
/// 	Reads the dashboard data from the usage database.
/// </summary>
/// <remarks>
/// 	Day values use the local calendar day, so the page ranges (ex. Today) match this computer.
/// 	The hourly rows keep UTC day and hour, because the page shifts them to local time or UTC itself.
/// </remarks>
internal static class DashboardData
{
	private const string ModelExpression = "COALESCE(NULLIF(model, ''), 'unknown')";

	// Auto-compaction subagents (acompact-*) have no dispatch record.
	private const string AgentTypeExpression = """
		COALESCE(a.agent_type, CASE WHEN t.agent_id LIKE 'acompact-%' THEN 'auto-compact' ELSE 'unknown' END)
		""";

	/// <summary>
	/// 	Loads the dashboard data.
	/// </summary>
	/// <param name="databasePath">
	/// 	The path of the database file.
	/// </param>
	/// <returns>
	/// 	A <see cref="DashboardPayload"/>, or a <see cref="DashboardError"/> when the database is not ready.
	/// </returns>
	public static object Load(string databasePath)
	{
		if (File.Exists(databasePath) is false)
		{
			return new DashboardError("Database not found. The first scan is running");
		}

		try
		{
			using SqliteConnection connection = UsageDatabase.OpenForRead(databasePath);
			return new DashboardPayload(
				ReadAllModels(connection),
				ReadDaily(connection),
				ReadHourly(connection),
				ReadSessions(connection),
				ReadSubagentTypes(connection),
				ReadDispatches(connection),
				DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
		}
		catch (SqliteException exception)
		{
			// The first scan can still be creating the schema.
			return new DashboardError($"Database not ready ({exception.Message})");
		}
	}

	private static List<string> ReadAllModels(SqliteConnection connection)
	{
		return Query(connection, $"""
			SELECT {ModelExpression} FROM turns
			GROUP BY {ModelExpression}
			ORDER BY SUM(input_tokens + output_tokens) DESC
			""", reader => reader.GetString(0));
	}

	private static List<DailyModelRow> ReadDaily(SqliteConnection connection)
	{
		return Query(connection, $"""
			SELECT date(timestamp, 'localtime') AS day, {ModelExpression} AS model,
				SUM(input_tokens), SUM(output_tokens), SUM(cache_read_tokens), SUM(cache_creation_tokens), COUNT(*)
			FROM turns
			WHERE date(timestamp, 'localtime') IS NOT NULL
			GROUP BY day, {ModelExpression}
			ORDER BY day, model
			""", reader => new DailyModelRow(
				reader.GetString(0), reader.GetString(1),
				reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
	}

	private static List<HourlyModelRow> ReadHourly(SqliteConnection connection)
	{
		// Timestamps are ISO 8601 UTC (ex. "2026-04-08T09:30:00.000Z"): characters 12-13 are the hour.
		return Query(connection, $"""
			SELECT substr(timestamp, 1, 10) AS day, CAST(substr(timestamp, 12, 2) AS INTEGER) AS hour,
				{ModelExpression} AS model, SUM(output_tokens), COUNT(*)
			FROM turns
			WHERE timestamp IS NOT NULL AND length(timestamp) >= 13
			GROUP BY day, hour, {ModelExpression}
			ORDER BY day, hour, model
			""", reader => new HourlyModelRow(
				reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
	}

	private static List<SessionRow> ReadSessions(SqliteConnection connection)
	{
		return Query(connection, """
			SELECT session_id,
				COALESCE(project_name, 'unknown'),
				COALESCE(git_branch, ''),
				COALESCE(topic, ''),
				COALESCE(strftime('%Y-%m-%d %H:%M', last_timestamp, 'localtime'), ''),
				COALESCE(date(last_timestamp, 'localtime'), ''),
				COALESCE(ROUND((julianday(last_timestamp) - julianday(first_timestamp)) * 1440, 1), 0),
				COALESCE(NULLIF(model, ''), 'unknown'),
				COALESCE(turn_count, 0),
				COALESCE(total_input_tokens, 0),
				COALESCE(total_output_tokens, 0),
				COALESCE(total_cache_read, 0),
				COALESCE(total_cache_creation, 0)
			FROM sessions
			ORDER BY last_timestamp DESC
			""", reader => new SessionRow(
				reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
				reader.GetString(4), reader.GetString(5), reader.GetDouble(6), reader.GetString(7),
				reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12)));
	}

	private static List<SubagentTypeRow> ReadSubagentTypes(SqliteConnection connection)
	{
		return Query(connection, $"""
			SELECT date(t.timestamp, 'localtime') AS day,
				{AgentTypeExpression} AS agent_type,
				COALESCE(NULLIF(t.model, ''), 'unknown') AS model,
				SUM(t.input_tokens), SUM(t.output_tokens), SUM(t.cache_read_tokens), SUM(t.cache_creation_tokens),
				COUNT(DISTINCT t.agent_id), COUNT(*)
			FROM turns t
			LEFT JOIN agents a ON t.agent_id = a.agent_id
			WHERE t.is_subagent = 1 AND date(t.timestamp, 'localtime') IS NOT NULL
			GROUP BY day, agent_type, model
			ORDER BY day, agent_type
			""", reader => new SubagentTypeRow(
				reader.GetString(0), reader.GetString(1), reader.GetString(2),
				reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
				reader.GetInt64(7), reader.GetInt64(8)));
	}

	private static List<DispatchRow> ReadDispatches(SqliteConnection connection)
	{
		return Query(connection, $"""
			SELECT t.agent_id,
				{AgentTypeExpression} AS agent_type,
				COALESCE(NULLIF(t.model, ''), 'unknown') AS model,
				COALESCE(strftime('%Y-%m-%d %H:%M', MIN(t.timestamp), 'localtime'), ''),
				COALESCE(date(MIN(t.timestamp), 'localtime'), ''),
				SUM(t.input_tokens), SUM(t.output_tokens), SUM(t.cache_read_tokens), SUM(t.cache_creation_tokens),
				COUNT(*),
				a.total_duration_ms, a.tool_use_count, a.status
			FROM turns t
			LEFT JOIN agents a ON t.agent_id = a.agent_id
			WHERE t.is_subagent = 1 AND t.agent_id IS NOT NULL
			GROUP BY t.agent_id
			ORDER BY SUM(t.input_tokens) + SUM(t.output_tokens) + SUM(t.cache_read_tokens) + SUM(t.cache_creation_tokens) DESC
			""", reader => new DispatchRow(
				reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
				reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9),
				reader.IsDBNull(10) ? null : reader.GetInt64(10),
				reader.IsDBNull(11) ? null : reader.GetInt64(11),
				reader.IsDBNull(12) ? null : reader.GetString(12)));
	}

	private static List<T> Query<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;
		using SqliteDataReader reader = command.ExecuteReader();
		List<T> rows = [];
		while (reader.Read())
		{
			rows.Add(map(reader));
		}

		return rows;
	}
}

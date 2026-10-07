using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ClaudeUsage.Pricing;
using ClaudeUsage.Storage;
using Microsoft.Data.Sqlite;

namespace ClaudeUsage.Reports;

/// <summary>
/// 	Token usage of one model (or one model on one day).
/// </summary>
internal sealed record ModelUsage(string Model, long Input, long Output, long CacheRead, long CacheCreation, long Turns, long Sessions)
{
	public double Cost(ModelPricing pricing)
	{
		return pricing.CalculateCost(Model, Input, Output, CacheRead, CacheCreation);
	}
}

/// <summary>
/// 	Writes the <c>today</c>, <c>week</c> and <c>stats</c> reports as text tables.
/// </summary>
/// <remarks>
/// 	Transcript timestamps are UTC.
/// 	The reports group them by local calendar day, so "today" means today on this computer.
/// </remarks>
/// <param name="databasePath">
/// 	The path of the database file.
/// </param>
/// <param name="pricing">
/// 	The prices for cost estimates.
/// </param>
/// <param name="output">
/// 	The writer for the report text.
/// </param>
internal sealed class ConsoleReports(string databasePath, ModelPricing pricing, TextWriter output)
{
	private const string LocalDay = "date(timestamp, 'localtime')";
	private const int RuleWidth = 78;

	private static readonly CultureInfo _invariant = CultureInfo.InvariantCulture;

	/// <summary>
	/// 	Writes the usage of today, by model.
	/// </summary>
	/// <returns>
	/// 	The process exit code.
	/// </returns>
	public int Today()
	{
		if (TryOpen(out SqliteConnection? connection) is false)
		{
			return 1;
		}

		using (connection)
		{
			string today = DateTime.Now.ToString("yyyy-MM-dd", _invariant);
			string filter = $"{LocalDay} = $start";
			List<ModelUsage> byModel = QueryByModel(connection, filter, today, today);
			(long subagentTurns, long subagentTokens) = QuerySubagentTotals(connection, filter, today, today);

			output.WriteLine();
			Rule();
			output.WriteLine($"  Today's Usage  ({today})");
			Rule();

			if (byModel.Count == 0)
			{
				output.WriteLine("  No usage recorded today.");
				output.WriteLine();
				return 0;
			}

			WriteModelTable(byModel, "  ", includeSessions: false);
			output.WriteLine();
			output.WriteLine($"  Sessions today:   {QueryDistinctSessions(connection, filter, today, today)}");
			output.WriteLine($"  Subagent tokens:  {FormatTokens(subagentTokens)}  ({FormatTokens(subagentTurns)} turns)");
			output.WriteLine($"  Cache read:       {FormatTokens(byModel.Sum(row => row.CacheRead))}");
			output.WriteLine($"  Cache creation:   {FormatTokens(byModel.Sum(row => row.CacheCreation))}");
			Rule();
			output.WriteLine();
			return 0;
		}
	}

	/// <summary>
	/// 	Writes the usage of the last 7 days, by day and by model.
	/// </summary>
	/// <returns>
	/// 	The process exit code.
	/// </returns>
	public int Week()
	{
		if (TryOpen(out SqliteConnection? connection) is false)
		{
			return 1;
		}

		using (connection)
		{
			DateTime endDay = DateTime.Today;
			DateTime startDay = endDay.AddDays(-6);
			string start = startDay.ToString("yyyy-MM-dd", _invariant);
			string end = endDay.ToString("yyyy-MM-dd", _invariant);
			string filter = $"{LocalDay} BETWEEN $start AND $end";
			List<ModelUsage> byModel = QueryByModel(connection, filter, start, end);

			output.WriteLine();
			Rule();
			output.WriteLine($"  Weekly Usage  ({start} to {end})");
			Rule();

			if (byModel.Count == 0)
			{
				output.WriteLine("  No usage recorded in the last 7 days.");
				output.WriteLine();
				return 0;
			}

			// Cost per day is the sum of per-model costs, because each model has its own price.
			Dictionary<string, (long Turns, long Input, long Output, double Cost)> perDay = new(StringComparer.Ordinal);
			foreach ((string day, ModelUsage usage) in QueryByDayAndModel(connection, filter, start, end))
			{
				(long turns, long input, long outputTokens, double cost) = perDay.GetValueOrDefault(day);
				perDay[day] = (turns + usage.Turns, input + usage.Input, outputTokens + usage.Output, cost + usage.Cost(pricing));
			}

			output.WriteLine("  By Day:");
			for (DateTime day = startDay; day <= endDay; day = day.AddDays(1))
			{
				string key = day.ToString("yyyy-MM-dd", _invariant);
				(long turns, long input, long outputTokens, double cost) = perDay.GetValueOrDefault(key);
				output.WriteLine($"    {key} {day:ddd}  turns={turns,-5}  in={FormatTokens(input),-8}  out={FormatTokens(outputTokens),-8}  cost={FormatCost(cost)}");
			}

			Rule();
			output.WriteLine("  By Model:");
			WriteModelTable(byModel, "    ", includeSessions: false);
			output.WriteLine();
			output.WriteLine($"  Sessions this week:  {QueryDistinctSessions(connection, filter, start, end)}");
			output.WriteLine($"  Cache read:          {FormatTokens(byModel.Sum(row => row.CacheRead))}");
			output.WriteLine($"  Cache creation:      {FormatTokens(byModel.Sum(row => row.CacheCreation))}");
			Rule();
			output.WriteLine();
			return 0;
		}
	}

	/// <summary>
	/// 	Writes all-time statistics: totals, by model, top projects and the daily average.
	/// </summary>
	/// <returns>
	/// 	The process exit code.
	/// </returns>
	public int Stats()
	{
		if (TryOpen(out SqliteConnection? connection) is false)
		{
			return 1;
		}

		using (connection)
		{
			List<ModelUsage> byModel = QueryByModel(connection, "1 = 1", null, null);
			(long subagentTurns, long subagentTokens) = QuerySubagentTotals(connection, "1 = 1", null, null);

			long sessionCount = 0;
			string first = "";
			string last = "";
			using (SqliteCommand command = connection.CreateCommand())
			{
				command.CommandText = "SELECT COUNT(*), MIN(first_timestamp), MAX(last_timestamp) FROM sessions";
				using SqliteDataReader reader = command.ExecuteReader();
				if (reader.Read())
				{
					sessionCount = reader.GetInt64(0);
					first = reader.IsDBNull(1) ? "" : ToLocalDate(reader.GetString(1));
					last = reader.IsDBNull(2) ? "" : ToLocalDate(reader.GetString(2));
				}
			}

			long totalInput = byModel.Sum(row => row.Input);
			long totalOutput = byModel.Sum(row => row.Output);
			long totalCacheRead = byModel.Sum(row => row.CacheRead);
			long totalCacheCreation = byModel.Sum(row => row.CacheCreation);
			long totalTurns = byModel.Sum(row => row.Turns);
			double totalCost = byModel.Sum(row => row.Cost(pricing));

			output.WriteLine();
			Rule('=');
			output.WriteLine("  Claude Code Usage - All-Time Statistics");
			Rule('=');
			output.WriteLine($"  Period:           {first} to {last}");
			output.WriteLine($"  Total sessions:   {sessionCount.ToString("N0", _invariant)}");
			output.WriteLine($"  Total turns:      {FormatTokens(totalTurns)}");
			output.WriteLine($"  Subagent turns:   {FormatTokens(subagentTurns)}");
			output.WriteLine();
			output.WriteLine($"  Input tokens:     {FormatTokens(totalInput),-12}  (raw prompt tokens)");
			output.WriteLine($"  Output tokens:    {FormatTokens(totalOutput),-12}  (generated tokens)");
			output.WriteLine($"  Cache read:       {FormatTokens(totalCacheRead),-12}  (90% cheaper than input)");
			output.WriteLine($"  Cache creation:   {FormatTokens(totalCacheCreation),-12}  (25% premium on input)");
			output.WriteLine($"  Subagent tokens:  {FormatTokens(subagentTokens),-12}  (included in totals)");
			output.WriteLine();
			output.WriteLine($"  Est. total cost:  {FormatCost(totalCost)}");
			Rule();

			output.WriteLine("  By Model:");
			WriteModelTable(byModel, "    ", includeSessions: true);

			Rule();
			output.WriteLine("  Top Projects:");
			using (SqliteCommand command = connection.CreateCommand())
			{
				command.CommandText = """
					SELECT COALESCE(s.project_name, 'unknown') AS project,
						SUM(t.input_tokens) AS input, SUM(t.output_tokens) AS output,
						COUNT(*) AS turns, COUNT(DISTINCT t.session_id) AS sessions
					FROM turns t
					LEFT JOIN sessions s ON t.session_id = s.session_id
					GROUP BY COALESCE(s.project_name, 'unknown')
					ORDER BY input + output DESC
					LIMIT 5
					""";
				using SqliteDataReader reader = command.ExecuteReader();
				while (reader.Read())
				{
					output.WriteLine($"    {reader.GetString(0),-40}  sessions={reader.GetInt64(4),-4}  turns={FormatTokens(reader.GetInt64(3)),-6}  tokens={FormatTokens(reader.GetInt64(1) + reader.GetInt64(2))}");
				}
			}

			using (SqliteCommand command = connection.CreateCommand())
			{
				command.CommandText = $"""
					SELECT AVG(daily_input), AVG(daily_output)
					FROM (
						SELECT {LocalDay} AS day, SUM(input_tokens) AS daily_input, SUM(output_tokens) AS daily_output
						FROM turns
						WHERE {LocalDay} >= date('now', 'localtime', '-30 days')
						GROUP BY day
					)
					""";
				using SqliteDataReader reader = command.ExecuteReader();
				if (reader.Read() && reader.IsDBNull(0) is false)
				{
					Rule();
					output.WriteLine("  Daily Average (last 30 days):");
					output.WriteLine($"    Input:   {FormatTokens((long)reader.GetDouble(0))}");
					output.WriteLine($"    Output:  {FormatTokens((long)reader.GetDouble(1))}");
				}
			}

			Rule('=');
			output.WriteLine();
			return 0;
		}
	}

	/// <summary>
	/// 	Formats a token count with a K, M or B suffix.
	/// </summary>
	/// <param name="count">
	/// 	The token count.
	/// </param>
	/// <returns>
	/// 	The formatted count (ex. <c>1.25M</c>).
	/// </returns>
	public static string FormatTokens(long count)
	{
		return count switch
		{
			>= 1_000_000_000 => (count / 1e9).ToString("0.00", _invariant) + "B",
			>= 1_000_000 => (count / 1e6).ToString("0.00", _invariant) + "M",
			>= 1_000 => (count / 1e3).ToString("0.0", _invariant) + "K",
			_ => count.ToString(_invariant),
		};
	}

	/// <summary>
	/// 	Formats a cost in US dollars with four decimals.
	/// </summary>
	/// <param name="cost">
	/// 	The cost.
	/// </param>
	/// <returns>
	/// 	The formatted cost (ex. <c>$12.3456</c>).
	/// </returns>
	public static string FormatCost(double cost)
	{
		return "$" + cost.ToString("N4", _invariant);
	}

	private void WriteModelTable(List<ModelUsage> rows, string indent, bool includeSessions)
	{
		double totalCost = rows.Sum(row => row.Cost(pricing));
		foreach (ModelUsage row in rows)
		{
			double cost = row.Cost(pricing);
			string costText = ModelPricing.IsBillable(row.Model) ? FormatCost(cost) : "n/a";
			string share = totalCost > 0 ? (cost / totalCost).ToString("0%", _invariant) : "-";
			string sessions = includeSessions ? $"sessions={row.Sessions,-5}  " : "";
			output.WriteLine($"{indent}{row.Model,-30}  {sessions}turns={FormatTokens(row.Turns),-6}  in={FormatTokens(row.Input),-8}  out={FormatTokens(row.Output),-8}  cost={costText,-12}  share={share,4}");
		}

		Rule();
		string totalSessions = includeSessions ? $"{"",-15}" : "";
		output.WriteLine($"{indent}{"TOTAL",-30}  {totalSessions}turns={FormatTokens(rows.Sum(row => row.Turns)),-6}  in={FormatTokens(rows.Sum(row => row.Input)),-8}  out={FormatTokens(rows.Sum(row => row.Output)),-8}  cost={FormatCost(totalCost)}");
	}

	private List<ModelUsage> QueryByModel(SqliteConnection connection, string filter, string? start, string? end)
	{
		using SqliteCommand command = CreateFilteredCommand(connection, $"""
			SELECT COALESCE(NULLIF(model, ''), 'unknown') AS model,
				SUM(input_tokens), SUM(output_tokens), SUM(cache_read_tokens), SUM(cache_creation_tokens),
				COUNT(*), COUNT(DISTINCT session_id)
			FROM turns
			WHERE {filter}
			GROUP BY COALESCE(NULLIF(model, ''), 'unknown')
			""", start, end);
		using SqliteDataReader reader = command.ExecuteReader();
		List<ModelUsage> rows = [];
		while (reader.Read())
		{
			rows.Add(new ModelUsage(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
		}

		// Most expensive first; models with no cost are sorted by tokens after them.
		return [.. rows.OrderByDescending(row => row.Cost(pricing)).ThenByDescending(row => row.Input + row.Output)];
	}

	private static List<(string Day, ModelUsage Usage)> QueryByDayAndModel(SqliteConnection connection, string filter, string start, string end)
	{
		using SqliteCommand command = CreateFilteredCommand(connection, $"""
			SELECT {LocalDay} AS day, COALESCE(NULLIF(model, ''), 'unknown') AS model,
				SUM(input_tokens), SUM(output_tokens), SUM(cache_read_tokens), SUM(cache_creation_tokens), COUNT(*)
			FROM turns
			WHERE {filter}
			GROUP BY day, COALESCE(NULLIF(model, ''), 'unknown')
			""", start, end);
		using SqliteDataReader reader = command.ExecuteReader();
		List<(string, ModelUsage)> rows = [];
		while (reader.Read())
		{
			rows.Add((reader.GetString(0), new ModelUsage(reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), 0)));
		}

		return rows;
	}

	private static (long Turns, long Tokens) QuerySubagentTotals(SqliteConnection connection, string filter, string? start, string? end)
	{
		using SqliteCommand command = CreateFilteredCommand(connection, $"""
			SELECT COUNT(*), COALESCE(SUM(input_tokens + output_tokens + cache_read_tokens + cache_creation_tokens), 0)
			FROM turns
			WHERE {filter} AND COALESCE(is_subagent, 0) = 1
			""", start, end);
		using SqliteDataReader reader = command.ExecuteReader();
		reader.Read();
		return (reader.GetInt64(0), reader.GetInt64(1));
	}

	private static long QueryDistinctSessions(SqliteConnection connection, string filter, string start, string end)
	{
		using SqliteCommand command = CreateFilteredCommand(connection, $"SELECT COUNT(DISTINCT session_id) FROM turns WHERE {filter}", start, end);
		return (long)(command.ExecuteScalar() ?? 0L);
	}

	private static SqliteCommand CreateFilteredCommand(SqliteConnection connection, string sql, string? start, string? end)
	{
		SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;
		if (start is not null)
		{
			command.Parameters.AddWithValue("$start", start);
		}

		if (end is not null)
		{
			command.Parameters.AddWithValue("$end", end);
		}

		return command;
	}

	private static string ToLocalDate(string timestamp)
	{
		return DateTimeOffset.TryParse(timestamp, _invariant, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
			? parsed.ToLocalTime().ToString("yyyy-MM-dd", _invariant)
			: timestamp.Length >= 10 ? timestamp[..10] : timestamp;
	}

	private bool TryOpen([NotNullWhen(true)] out SqliteConnection? connection)
	{
		connection = null;
		if (File.Exists(databasePath) is false)
		{
			output.WriteLine("Database not found. Run: claude-usage scan");
			return false;
		}

		connection = UsageDatabase.OpenForRead(databasePath);
		return true;
	}

	private void Rule(char character = '-')
	{
		output.WriteLine(new string(character, RuleWidth));
	}
}

using System.Text;
using ClaudeUsage.Scanning;
using ClaudeUsage.Storage;
using Microsoft.Data.Sqlite;

namespace ClaudeUsage.Tests;

public sealed class UsageScannerTests : IDisposable
{
	private readonly TemporaryDirectory _directory = new();
	private readonly string _projects;
	private readonly string _database;

	public UsageScannerTests()
	{
		_projects = _directory.Combine("projects");
		_database = _directory.Combine("usage.db");
		Directory.CreateDirectory(Path.Combine(_projects, "demo"));
	}

	public void Dispose()
	{
		_directory.Dispose();
	}

	private string Transcript(string name = "session.jsonl")
	{
		return Path.Combine(_projects, "demo", name);
	}

	private ScanSummary Scan()
	{
		return UsageScanner.Scan(_database, [_projects]);
	}

	private long Scalar(string sql)
	{
		using SqliteConnection connection = UsageDatabase.OpenForRead(_database);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;
		return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
	}

	private static void Append(string path, string text)
	{
		// Bump the time explicitly: two writes in one test can share a timestamp.
		DateTime before = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
		File.AppendAllText(path, text, new UTF8Encoding(false));
		if (File.GetLastWriteTimeUtc(path) <= before.AddSeconds(1))
		{
			File.SetLastWriteTimeUtc(path, before.AddSeconds(5));
		}
	}

	[Fact]
	public void Scan_StoresTurnsAndSessionTotals()
	{
		Append(Transcript(), Records.User("s1") + "\n" + Records.Assistant("s1", "m1", output: 100) + "\n" + Records.Assistant("s1", "m2", output: 50) + "\n");

		ScanSummary summary = Scan();

		Assert.Equal(new ScanSummary(1, 0, 0, 2, 1), summary);
		Assert.Equal(2, Scalar("SELECT COUNT(*) FROM turns"));
		Assert.Equal(150, Scalar("SELECT total_output_tokens FROM sessions WHERE session_id = 's1'"));
		Assert.Equal(2, Scalar("SELECT turn_count FROM sessions WHERE session_id = 's1'"));
	}

	[Fact]
	public void Scan_SkipsUnchangedFiles()
	{
		Append(Transcript(), Records.Assistant("s1", "m1") + "\n");
		Scan();

		ScanSummary second = Scan();

		Assert.Equal(new ScanSummary(0, 0, 1, 0, 0), second);
	}

	[Fact]
	public void Scan_ReadsOnlyNewLinesOfAFileThatGrew()
	{
		Append(Transcript(), Records.Assistant("s1", "m1", output: 100) + "\n");
		Scan();

		Append(Transcript(), Records.Assistant("s1", "m2", output: 25) + "\n");
		ScanSummary second = Scan();

		Assert.Equal(1, second.Updated);
		Assert.Equal(1, second.Turns);
		Assert.Equal(125, Scalar("SELECT total_output_tokens FROM sessions"));
	}

	[Fact]
	public void Scan_LeavesAHalfWrittenLineForTheNextScan()
	{
		string complete = Records.Assistant("s1", "m1", output: 100) + "\n";
		string partial = Records.Assistant("s1", "m2", output: 25);
		Append(Transcript(), complete + partial[..40]);

		Scan();
		Assert.Equal(1, Scalar("SELECT COUNT(*) FROM turns"));

		Append(Transcript(), partial[40..] + "\n");
		Scan();
		Assert.Equal(2, Scalar("SELECT COUNT(*) FROM turns"));
		Assert.Equal(125, Scalar("SELECT total_output_tokens FROM sessions"));
	}

	[Fact]
	public void Scan_UsesACompleteLastLineWithNoLineBreak()
	{
		Append(Transcript(), Records.Assistant("s1", "m1"));

		Scan();

		Assert.Equal(1, Scalar("SELECT COUNT(*) FROM turns"));
	}

	[Fact]
	public void Scan_DoesNotCountATurnTwiceWhenAFileIsRewritten()
	{
		Append(Transcript(), Records.Assistant("s1", "m1", output: 100) + "\n" + Records.Assistant("s1", "m2", output: 1) + "\n");
		Scan();

		// The file becomes shorter than the stored offset, so the scanner reads it again from the start.
		File.WriteAllText(Transcript(), Records.Assistant("s1", "m1", output: 100) + "\n");
		File.SetLastWriteTimeUtc(Transcript(), DateTime.UtcNow.AddMinutes(1));
		Scan();

		Assert.Equal(2, Scalar("SELECT COUNT(*) FROM turns"));
		Assert.Equal(101, Scalar("SELECT total_output_tokens FROM sessions"));
	}

	[Fact]
	public void Scan_KeepsTheMostCapableModelForASession()
	{
		Append(Transcript(), Records.Assistant("s1", "m1", model: "claude-opus-5-5") + "\n");
		Scan();

		Append(Transcript(), Records.Assistant("s1", "m2", model: "claude-haiku-4-5") + "\n" + Records.Assistant("s1", "m3", model: "claude-haiku-4-5") + "\n");
		Scan();

		using SqliteConnection connection = UsageDatabase.OpenForRead(_database);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT model FROM sessions";
		Assert.Equal("claude-opus-5-5", command.ExecuteScalar());
	}

	[Fact]
	public void Scan_TitleOnlySession_GetsNoRow()
	{
		Append(Transcript(), Records.Title("s9", "ai-title", "Only a title") + "\n");

		Scan();

		Assert.Equal(0, Scalar("SELECT COUNT(*) FROM sessions"));
	}

	[Fact]
	public void Scan_SubagentMetaFile_GivesTypeWithoutReplacingDispatchData()
	{
		string sessionDirectory = Path.Combine(_projects, "demo", "s1", "subagents");
		Directory.CreateDirectory(sessionDirectory);
		Append(Path.Combine(sessionDirectory, "agent-a1.jsonl"), Records.Assistant("s1", "m1", agentId: "a1", isSidechain: true) + "\n");
		File.WriteAllText(Path.Combine(sessionDirectory, "agent-a1.meta.json"), """{"agentType":"Explore"}""");
		Append(Path.Combine(sessionDirectory, "agent-a2.jsonl"), Records.Assistant("s1", "m2", agentId: "a2", isSidechain: true) + "\n");
		File.WriteAllText(Path.Combine(sessionDirectory, "agent-a2.meta.json"), """{"agentType":"fork"}""");
		Append(Transcript(), Records.Dispatch("s1", "a1", "Explore") + "\n");

		Scan();

		Assert.Equal(7, Scalar("SELECT tool_use_count FROM agents WHERE agent_id = 'a1'"));
		Assert.Equal(1, Scalar("SELECT COUNT(*) FROM agents WHERE agent_id = 'a2' AND agent_type = 'fork'"));
		Assert.Equal(2, Scalar("SELECT COUNT(*) FROM turns WHERE is_subagent = 1"));
	}
}

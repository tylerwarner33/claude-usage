using ClaudeUsage.Scanning;

namespace ClaudeUsage.Tests;

public sealed class TranscriptParserTests
{
	private static ParseResult Parse(string sourcePath, params string[] lines)
	{
		TranscriptParser parser = new(sourcePath);
		foreach (string line in lines)
		{
			parser.ProcessLine(line);
		}

		return parser.Build();
	}

	[Fact]
	public void StreamingRecordsWithSameMessageId_KeepOnlyTheLastRecord()
	{
		ParseResult result = Parse(
			"session.jsonl",
			Records.Assistant("s1", "msg_1", output: 5),
			Records.Assistant("s1", "msg_1", output: 500),
			Records.Assistant("s1", "msg_2", output: 7));

		Assert.Equal(2, result.Turns.Count);
		Assert.Equal(500, result.Turns.Single(turn => turn.MessageId == "msg_1").OutputTokens);
		Assert.Equal(507, result.Sessions.Single().TotalOutputTokens);
	}

	[Fact]
	public void RecordWithNoTokenUsage_IsNotATurn()
	{
		ParseResult result = Parse("session.jsonl", Records.Assistant("s1", "msg_1", input: 0, output: 0, cacheRead: 0, cacheCreation: 0));

		Assert.Empty(result.Turns);
	}

	[Fact]
	public void LinesThatAreNotValid_AreIgnored()
	{
		ParseResult result = Parse("session.jsonl", "", "   ", "{broken", "[1,2,3]", "\"text\"", Records.Assistant("s1", "msg_1"));

		Assert.Single(result.Turns);
	}

	[Fact]
	public void SessionMetadata_ComesFromAllRecords()
	{
		ParseResult result = Parse(
			"session.jsonl",
			Records.User("s1", timestamp: "2026-10-01T10:00:00.000Z"),
			Records.Assistant("s1", "msg_1", timestamp: "2026-10-01T12:00:00.000Z", toolName: "Bash"));

		SessionInfo session = result.Sessions.Single();
		Assert.Equal("Demo/App", session.ProjectName);
		Assert.Equal("2026-10-01T10:00:00.000Z", session.FirstTimestamp);
		Assert.Equal("2026-10-01T12:00:00.000Z", session.LastTimestamp);
		Assert.Equal("main", session.GitBranch);
		Assert.Equal("claude-opus-5-5", session.Model);
		Assert.Equal("Bash", result.Turns.Single().ToolName);
	}

	[Fact]
	public void SessionModel_IsTheModelOfMostTurns()
	{
		ParseResult result = Parse(
			"session.jsonl",
			Records.Assistant("s1", "msg_1", model: "claude-opus-5-5"),
			Records.Assistant("s1", "msg_2", model: "claude-haiku-4-5"),
			Records.Assistant("s1", "msg_3", model: "claude-haiku-4-5"));

		Assert.Equal("claude-haiku-4-5", result.Sessions.Single().Model);
	}

	[Fact]
	public void CustomTitle_WinsOverAiTitle()
	{
		ParseResult result = Parse(
			"session.jsonl",
			Records.Title("s1", "ai-title", "AI title"),
			Records.Title("s1", "custom-title", "My title"),
			Records.Title("s1", "ai-title", "Later AI title"),
			Records.Assistant("s1", "msg_1"));

		Assert.Equal("My title", result.Sessions.Single().Topic);
	}

	[Fact]
	public void TitleBeforeContent_StillGetsTheProjectName()
	{
		ParseResult result = Parse("session.jsonl", Records.Title("s1", "ai-title", "Title"), Records.Assistant("s1", "msg_1"));

		Assert.Equal("Demo/App", result.Sessions.Single().ProjectName);
	}

	[Theory]
	[InlineData(true, null, "session.jsonl")]
	[InlineData(false, "a123", "session.jsonl")]
	[InlineData(false, null, @"C:\x\s1\subagents\agent-a1.jsonl")]
	public void SubagentTurns_AreDetected(bool isSidechain, string? agentId, string path)
	{
		ParseResult result = Parse(path, Records.Assistant("s1", "msg_1", agentId: agentId, isSidechain: isSidechain));

		Assert.True(result.Turns.Single().IsSubagent);
		Assert.Equal(agentId, result.Turns.Single().AgentId);
	}

	[Fact]
	public void MainSessionTurn_IsNotASubagentTurn()
	{
		ParseResult result = Parse("session.jsonl", Records.Assistant("s1", "msg_1"));

		Assert.False(result.Turns.Single().IsSubagent);
	}

	[Fact]
	public void DispatchToolResult_GivesAgentMetadata()
	{
		ParseResult result = Parse("session.jsonl", Records.Dispatch("s1", "a123", "Explore"));

		AgentDispatch agent = result.Agents.Single();
		Assert.Equal(("a123", "Explore", "s1", "completed"), (agent.AgentId, agent.AgentType, agent.DispatchedInSession, agent.Status));
		Assert.Equal((1234L, 5000L, 7L), (agent.TotalTokens, agent.TotalDurationMs, agent.ToolUseCount));
	}

	[Fact]
	public void ReadSubagentMeta_ReadsTypeFromTheFileNextToTheTranscript()
	{
		using TemporaryDirectory directory = new();
		string subagents = directory.Combine("session-1", "subagents");
		Directory.CreateDirectory(subagents);
		string transcript = Path.Combine(subagents, "agent-abc.jsonl");
		File.WriteAllText(transcript, "");
		File.WriteAllText(Path.Combine(subagents, "agent-abc.meta.json"), """{"agentType":"fork","requestShape":"background"}""");

		AgentDispatch? agent = TranscriptParser.ReadSubagentMeta(transcript);

		Assert.NotNull(agent);
		Assert.Equal(("abc", "fork", "session-1"), (agent.AgentId, agent.AgentType, agent.DispatchedInSession));
	}

	[Theory]
	[InlineData(null, "unknown")]
	[InlineData("", "unknown")]
	[InlineData(@"C:\repos\Demo\App\", "Demo/App")]
	[InlineData("/home/user/project", "user/project")]
	[InlineData("single", "single")]
	public void ProjectNameFromCwd_UsesTheLastTwoParts(string? cwd, string expected)
	{
		Assert.Equal(expected, TranscriptParser.ProjectNameFromCwd(cwd));
	}
}

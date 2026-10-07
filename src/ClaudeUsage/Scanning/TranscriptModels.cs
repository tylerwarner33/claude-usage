namespace ClaudeUsage.Scanning;

/// <summary>
/// 	One assistant API response with token usage.
/// </summary>
internal sealed record TurnRecord(
	string SessionId,
	string Timestamp,
	string Model,
	long InputTokens,
	long OutputTokens,
	long CacheReadTokens,
	long CacheCreationTokens,
	string? ToolName,
	string Cwd,
	string MessageId,
	bool IsSubagent,
	string? AgentId)
{
	/// <summary>
	/// 	Gets the sum of all four token types.
	/// </summary>
	public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>
/// 	Metadata of a subagent dispatch, from the tool result that closes the Agent or Task tool call.
/// </summary>
internal sealed record AgentDispatch(
	string AgentId,
	string AgentType,
	string? DispatchedInSession,
	string CompletedAt,
	string? Status,
	long? TotalTokens,
	long? TotalDurationMs,
	long? ToolUseCount);

/// <summary>
/// 	Session data collected from the lines of one transcript file.
/// </summary>
internal sealed class SessionInfo(string sessionId)
{
	public string SessionId { get; } = sessionId;

	public string ProjectName { get; set; } = "unknown";

	public string FirstTimestamp { get; set; } = "";

	public string LastTimestamp { get; set; } = "";

	public string GitBranch { get; set; } = "";

	/// <summary>
	/// 	Gets or sets the model used by most turns of this batch, or null when the batch has no turns.
	/// </summary>
	public string? Model { get; set; }

	/// <summary>
	/// 	Gets or sets the session title, from a <c>custom-title</c> or <c>ai-title</c> record.
	/// </summary>
	public string? Topic { get; set; }

	public bool HasCustomTitle { get; set; }

	public long TotalInputTokens { get; set; }

	public long TotalOutputTokens { get; set; }

	public long TotalCacheRead { get; set; }

	public long TotalCacheCreation { get; set; }

	public int TurnCount { get; set; }
}

/// <summary>
/// 	The result of a parse of one transcript file, from a start offset to the last complete line.
/// </summary>
internal sealed record ParseResult(
	IReadOnlyList<SessionInfo> Sessions,
	IReadOnlyList<TurnRecord> Turns,
	IReadOnlyList<AgentDispatch> Agents,
	long EndOffset);

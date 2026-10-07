using System.Text;

namespace ClaudeUsage.Scanning;

/// <summary>
/// 	Parses Claude Code JSONL transcript lines into turns, sessions and subagent dispatches.
/// </summary>
/// <remarks>
/// 	Use one instance for one file (or one part of a file).
/// 	Call <see cref="ProcessLine"/> for each line, then <see cref="Build"/>.
/// </remarks>
/// <param name="sourcePath">
/// 	The path of the transcript file.
/// 	A path under a <c>subagents</c> directory marks its turns as subagent turns.
/// </param>
internal sealed class TranscriptParser(string sourcePath)
{
	private static readonly HashSet<string> _relevantTypes = new(StringComparer.Ordinal) { "assistant", "user", "custom-title", "ai-title" };

	private readonly bool _isSubagentPath = sourcePath.Replace('\\', '/').Contains("/subagents/", StringComparison.OrdinalIgnoreCase);
	private readonly Dictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);
	private readonly Dictionary<string, TurnRecord> _turnsByMessageId = new(StringComparer.Ordinal);
	private readonly List<TurnRecord> _turnsWithoutMessageId = [];
	private readonly Dictionary<string, AgentDispatch> _agents = new(StringComparer.Ordinal);

	/// <summary>
	/// 	Parses the complete lines of a transcript file, from a byte offset.
	/// </summary>
	/// <param name="path">
	/// 	The path of the transcript file.
	/// </param>
	/// <param name="startOffset">
	/// 	The byte offset where the parse starts.
	/// 	Use 0 for a new file, or the end offset of the previous parse for an incremental scan.
	/// </param>
	/// <returns>
	/// 	The parsed data and the byte offset after the last line that was used.
	/// </returns>
	public static ParseResult ParseFile(string path, long startOffset)
	{
		TranscriptParser parser = new(path);
		long endOffset = startOffset;
		foreach ((string line, long lineEndOffset) in JsonLineReader.ReadLines(path, startOffset))
		{
			parser.ProcessLine(line);
			endOffset = lineEndOffset;
		}

		return parser.Build(endOffset);
	}

	/// <summary>
	/// 	Reads the subagent type from the <c>.meta.json</c> file next to a subagent transcript.
	/// </summary>
	/// <remarks>
	/// 	Claude Code writes <c>subagents/agent-ID.jsonl</c> and <c>subagents/agent-ID.meta.json</c> for each subagent.
	/// 	A background (async) subagent has no <c>agentType</c> in the tool result of the parent.
	/// 	For such a subagent, the meta file is the only source of its type.
	/// </remarks>
	/// <param name="transcriptPath">
	/// 	The path of the subagent transcript.
	/// </param>
	/// <returns>
	/// 	A dispatch with the agent id, type and parent session, or null when there is no usable meta file.
	/// </returns>
	public static AgentDispatch? ReadSubagentMeta(string transcriptPath)
	{
		string fileName = Path.GetFileNameWithoutExtension(transcriptPath);
		if (fileName.StartsWith("agent-", StringComparison.Ordinal) is false)
		{
			return null;
		}

		string metaPath = Path.Combine(Path.GetDirectoryName(transcriptPath) ?? "", fileName + ".meta.json");
		if (File.Exists(metaPath) is false)
		{
			return null;
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(File.ReadAllText(metaPath));
			string agentType = GetString(document.RootElement, "agentType");
			if (agentType.Length == 0)
			{
				return null;
			}

			// The layout is <session id>/subagents/agent-<id>.jsonl.
			string? parentSession = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(transcriptPath)));
			return new AgentDispatch(fileName["agent-".Length..], agentType, parentSession, "", null, null, null, null);
		}
		catch (Exception exception) when (exception is JsonException or IOException)
		{
			return null;
		}
	}

	/// <summary>
	/// 	Processes one JSONL line.
	/// 	Lines that are empty, not valid JSON, or not relevant are ignored.
	/// </summary>
	/// <param name="line">
	/// 	The text of the line.
	/// </param>
	public void ProcessLine(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return;
		}

		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(line);
		}
		catch (JsonException)
		{
			return;
		}

		using (document)
		{
			ProcessRecord(document.RootElement);
		}
	}

	/// <summary>
	/// 	Builds the result from all processed lines.
	/// </summary>
	/// <remarks>
	/// 	Each session gets the token totals of its turns in this batch.
	/// 	The session model is the model that most turns used.
	/// </remarks>
	/// <param name="endOffset">
	/// 	The byte offset after the last line that was processed.
	/// </param>
	/// <returns>
	/// 	The parse result.
	/// </returns>
	public ParseResult Build(long endOffset = 0)
	{
		List<TurnRecord> turns = [.. _turnsWithoutMessageId, .. _turnsByMessageId.Values];

		Dictionary<string, Dictionary<string, int>> modelCounts = new(StringComparer.Ordinal);
		foreach (SessionInfo session in _sessions.Values)
		{
			session.TotalInputTokens = 0;
			session.TotalOutputTokens = 0;
			session.TotalCacheRead = 0;
			session.TotalCacheCreation = 0;
			session.TurnCount = 0;
			session.Model = null;
		}

		foreach (TurnRecord turn in turns)
		{
			if (_sessions.TryGetValue(turn.SessionId, out SessionInfo? session) is false)
			{
				continue;
			}

			session.TotalInputTokens += turn.InputTokens;
			session.TotalOutputTokens += turn.OutputTokens;
			session.TotalCacheRead += turn.CacheReadTokens;
			session.TotalCacheCreation += turn.CacheCreationTokens;
			session.TurnCount++;
			if (turn.Model.Length > 0)
			{
				Dictionary<string, int> counts = modelCounts.TryGetValue(turn.SessionId, out Dictionary<string, int>? existing)
					? existing
					: modelCounts[turn.SessionId] = new Dictionary<string, int>(StringComparer.Ordinal);
				counts[turn.Model] = counts.GetValueOrDefault(turn.Model) + 1;
			}
		}

		foreach ((string sessionId, Dictionary<string, int> counts) in modelCounts)
		{
			// Dictionary keeps insertion order here (no removals), so a tie goes to the model seen first.
			_sessions[sessionId].Model = counts.MaxBy(pair => pair.Value).Key;
		}

		return new ParseResult([.. _sessions.Values], turns, [.. _agents.Values], endOffset);
	}

	private void ProcessRecord(JsonElement record)
	{
		if (record.ValueKind is not JsonValueKind.Object)
		{
			return;
		}

		string type = GetString(record, "type");
		if (_relevantTypes.Contains(type) is false)
		{
			return;
		}

		string sessionId = GetString(record, "sessionId");
		if (sessionId.Length == 0)
		{
			return;
		}

		if (type is "custom-title" or "ai-title")
		{
			ProcessTitle(record, type, sessionId);
			return;
		}

		if (type == "user" && ExtractAgentDispatch(record) is AgentDispatch dispatch)
		{
			_agents[dispatch.AgentId] = dispatch;
		}

		string timestamp = GetString(record, "timestamp");
		string cwd = GetString(record, "cwd");
		string gitBranch = GetString(record, "gitBranch");
		SessionInfo session = UpdateSession(sessionId, timestamp, cwd, gitBranch);

		if (type == "assistant")
		{
			ProcessAssistant(record, session, timestamp, cwd);
		}
	}

	private void ProcessTitle(JsonElement record, string type, string sessionId)
	{
		string title = type == "custom-title" ? GetString(record, "customTitle") : GetString(record, "aiTitle");
		if (title.Length == 0)
		{
			return;
		}

		SessionInfo session = GetOrAddSession(sessionId);

		// A custom title (set by the user) always wins over an AI title.
		if (type == "custom-title")
		{
			session.Topic = title;
			session.HasCustomTitle = true;
		}
		else if (session.HasCustomTitle is false && string.IsNullOrEmpty(session.Topic))
		{
			session.Topic = title;
		}
	}

	private SessionInfo UpdateSession(string sessionId, string timestamp, string cwd, string gitBranch)
	{
		SessionInfo session = GetOrAddSession(sessionId);

		// A title record can create the session before any content record, so fill an unknown project later.
		if (session.ProjectName == "unknown" && cwd.Length > 0)
		{
			session.ProjectName = ProjectNameFromCwd(cwd);
		}

		if (timestamp.Length > 0)
		{
			if (session.FirstTimestamp.Length == 0 || string.CompareOrdinal(timestamp, session.FirstTimestamp) < 0)
			{
				session.FirstTimestamp = timestamp;
			}

			if (session.LastTimestamp.Length == 0 || string.CompareOrdinal(timestamp, session.LastTimestamp) > 0)
			{
				session.LastTimestamp = timestamp;
			}
		}

		if (session.GitBranch.Length == 0 && gitBranch.Length > 0)
		{
			session.GitBranch = gitBranch;
		}

		return session;
	}

	private void ProcessAssistant(JsonElement record, SessionInfo session, string timestamp, string cwd)
	{
		if (record.TryGetProperty("message", out JsonElement message) is false || message.ValueKind is not JsonValueKind.Object)
		{
			return;
		}

		message.TryGetProperty("usage", out JsonElement usage);
		long inputTokens = GetInt64(usage, "input_tokens") ?? 0;
		long outputTokens = GetInt64(usage, "output_tokens") ?? 0;
		long cacheReadTokens = GetInt64(usage, "cache_read_input_tokens") ?? 0;
		long cacheCreationTokens = GetInt64(usage, "cache_creation_input_tokens") ?? 0;

		// Only responses with token usage count as turns.
		if (inputTokens + outputTokens + cacheReadTokens + cacheCreationTokens == 0)
		{
			return;
		}

		string model = GetString(message, "model");
		string messageId = GetString(message, "id");

		TurnRecord turn = new(
			session.SessionId,
			timestamp,
			model,
			inputTokens,
			outputTokens,
			cacheReadTokens,
			cacheCreationTokens,
			FindToolName(message),
			cwd,
			messageId,
			IsSubagentRecord(record),
			RecordAgentId(record));

		// Claude Code writes several records per streamed response, all with the same message id.
		// The last record has the final usage counts, so it replaces the earlier ones.
		if (messageId.Length > 0)
		{
			_turnsByMessageId[messageId] = turn;
		}
		else
		{
			_turnsWithoutMessageId.Add(turn);
		}
	}

	private SessionInfo GetOrAddSession(string sessionId)
	{
		if (_sessions.TryGetValue(sessionId, out SessionInfo? session) is false)
		{
			session = new SessionInfo(sessionId);
			_sessions[sessionId] = session;
		}

		return session;
	}

	private bool IsSubagentRecord(JsonElement record)
	{
		if (record.TryGetProperty("isSidechain", out JsonElement sidechain) && sidechain.ValueKind is JsonValueKind.True)
		{
			return true;
		}

		return RecordAgentId(record) is not null || _isSubagentPath;
	}

	/// <summary>
	/// 	Derives a short project name from a working directory: the last two path parts.
	/// </summary>
	/// <param name="cwd">
	/// 	The working directory of the record.
	/// </param>
	/// <returns>
	/// 	A name such as <c>Demo/App</c>, or <c>unknown</c>.
	/// </returns>
	public static string ProjectNameFromCwd(string? cwd)
	{
		if (string.IsNullOrEmpty(cwd))
		{
			return "unknown";
		}

		string[] parts = cwd.Replace('\\', '/').TrimEnd('/').Split('/');
		if (parts.Length >= 2)
		{
			return string.Join('/', parts[^2..]);
		}

		return parts.Length == 1 && parts[0].Length > 0 ? parts[0] : "unknown";
	}

	private static string? RecordAgentId(JsonElement record)
	{
		string agentId = GetString(record, "agentId");
		if (agentId.Length == 0 && record.TryGetProperty("data", out JsonElement data))
		{
			agentId = GetString(data, "agentId");
		}

		return agentId.Length > 0 ? agentId : null;
	}

	private static AgentDispatch? ExtractAgentDispatch(JsonElement record)
	{
		if (record.TryGetProperty("toolUseResult", out JsonElement result) is false || result.ValueKind is not JsonValueKind.Object)
		{
			return null;
		}

		string agentId = GetString(result, "agentId");
		string agentType = GetString(result, "agentType");
		if (agentId.Length == 0 || agentType.Length == 0)
		{
			return null;
		}

		string status = GetString(result, "status");
		return new AgentDispatch(
			agentId,
			agentType,
			GetString(record, "sessionId"),
			GetString(record, "timestamp"),
			status.Length > 0 ? status : null,
			GetInt64(result, "totalTokens"),
			GetInt64(result, "totalDurationMs"),
			GetInt64(result, "totalToolUseCount"));
	}

	private static string? FindToolName(JsonElement message)
	{
		if (message.TryGetProperty("content", out JsonElement content) is false || content.ValueKind is not JsonValueKind.Array)
		{
			return null;
		}

		foreach (JsonElement item in content.EnumerateArray())
		{
			if (item.ValueKind is JsonValueKind.Object && GetString(item, "type") == "tool_use")
			{
				string name = GetString(item, "name");
				return name.Length > 0 ? name : null;
			}
		}

		return null;
	}

	private static string GetString(JsonElement element, string propertyName)
	{
		if (element.ValueKind is JsonValueKind.Object
			&& element.TryGetProperty(propertyName, out JsonElement value)
			&& value.ValueKind is JsonValueKind.String)
		{
			return value.GetString() ?? "";
		}

		return "";
	}

	private static long? GetInt64(JsonElement element, string propertyName)
	{
		if (element.ValueKind is not JsonValueKind.Object
			|| element.TryGetProperty(propertyName, out JsonElement value) is false
			|| value.ValueKind is not JsonValueKind.Number)
		{
			return null;
		}

		return value.TryGetInt64(out long whole) ? whole : (long)value.GetDouble();
	}
}

/// <summary>
/// 	Reads the lines of a JSONL file from a byte offset.
/// </summary>
internal static class JsonLineReader
{
	private const int BufferSize = 64 * 1024;

	/// <summary>
	/// 	Reads lines from a byte offset and gives the byte offset after each line.
	/// </summary>
	/// <remarks>
	/// 	Claude Code can write to the file during a scan.
	/// 	A last line with no line break is used only when it is a complete JSON value.
	/// 	Otherwise the reader leaves it for the next scan, so a half-written record is never lost.
	/// </remarks>
	/// <param name="path">
	/// 	The path of the file.
	/// </param>
	/// <param name="startOffset">
	/// 	The byte offset where reading starts.
	/// </param>
	/// <returns>
	/// 	Each line (without the line break) and the byte offset after it.
	/// </returns>
	public static IEnumerable<(string Line, long EndOffset)> ReadLines(string path, long startOffset)
	{
		using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
		stream.Seek(startOffset, SeekOrigin.Begin);

		byte[] buffer = new byte[BufferSize];
		using MemoryStream pending = new();
		long bufferStartOffset = startOffset;
		int bytesRead;
		while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
		{
			int lineStart = 0;
			for (int index = 0; index < bytesRead; index++)
			{
				if (buffer[index] != (byte)'\n')
				{
					continue;
				}

				pending.Write(buffer, lineStart, index - lineStart);
				yield return (Decode(pending), bufferStartOffset + index + 1);
				pending.SetLength(0);
				lineStart = index + 1;
			}

			pending.Write(buffer, lineStart, bytesRead - lineStart);
			bufferStartOffset += bytesRead;
		}

		if (pending.Length > 0)
		{
			string tail = Decode(pending);
			if (IsCompleteJson(tail))
			{
				yield return (tail, bufferStartOffset);
			}
		}
	}

	private static string Decode(MemoryStream bytes)
	{
		// Invalid UTF-8 becomes U+FFFD.
		return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length).TrimEnd('\r');
	}

	private static bool IsCompleteJson(string text)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(text);
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}
}

using System.Text.Json;

namespace ClaudeUsage.Tests;

/// <summary>
/// 	A temporary directory that is deleted after the test.
/// </summary>
internal sealed class TemporaryDirectory : IDisposable
{
	public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claude-usage-tests", Guid.NewGuid().ToString("N"));

	public TemporaryDirectory()
	{
		Directory.CreateDirectory(Path);
	}

	public string Combine(params string[] parts)
	{
		return System.IO.Path.Combine([Path, .. parts]);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(Path, recursive: true);
		}
		catch (IOException)
		{
			// A locked file must not fail the test.
		}
	}
}

/// <summary>
/// 	Builds transcript JSONL records like the ones Claude Code writes.
/// </summary>
internal static class Records
{
	public static string Assistant(
		string sessionId,
		string messageId,
		string model = "claude-opus-5-5",
		string timestamp = "2026-10-01T15:00:00.000Z",
		long input = 10,
		long output = 100,
		long cacheRead = 1000,
		long cacheCreation = 50,
		string cwd = @"C:\repos\Demo\App",
		string? toolName = null,
		string? agentId = null,
		bool isSidechain = false)
	{
		Dictionary<string, object?> record = new()
		{
			["type"] = "assistant",
			["sessionId"] = sessionId,
			["timestamp"] = timestamp,
			["cwd"] = cwd,
			["gitBranch"] = "main",
			["isSidechain"] = isSidechain,
			["message"] = new Dictionary<string, object?>
			{
				["id"] = messageId,
				["model"] = model,
				["content"] = toolName is null
					? new object[] { new { type = "text", text = "hi" } }
					: new object[] { new { type = "tool_use", name = toolName } },
				["usage"] = new Dictionary<string, long>
				{
					["input_tokens"] = input,
					["output_tokens"] = output,
					["cache_read_input_tokens"] = cacheRead,
					["cache_creation_input_tokens"] = cacheCreation,
				},
			},
		};
		if (agentId is not null)
		{
			record["agentId"] = agentId;
		}

		return JsonSerializer.Serialize(record);
	}

	public static string User(string sessionId, string timestamp = "2026-10-01T14:59:00.000Z", string cwd = @"C:\repos\Demo\App")
	{
		return JsonSerializer.Serialize(new { type = "user", sessionId, timestamp, cwd, gitBranch = "main" });
	}

	public static string Title(string sessionId, string type, string title)
	{
		return type == "custom-title"
			? JsonSerializer.Serialize(new { type, sessionId, customTitle = title })
			: JsonSerializer.Serialize(new { type, sessionId, aiTitle = title });
	}

	public static string Dispatch(string sessionId, string agentId, string agentType)
	{
		return JsonSerializer.Serialize(new
		{
			type = "user",
			sessionId,
			timestamp = "2026-10-01T15:10:00.000Z",
			toolUseResult = new { agentId, agentType, status = "completed", totalTokens = 1234, totalDurationMs = 5000, totalToolUseCount = 7 },
		});
	}
}

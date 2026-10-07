using System.Text.Json;
using ClaudeUsage.Dashboard;
using ClaudeUsage.Pricing;
using ClaudeUsage.Scanning;

namespace ClaudeUsage.Tests;

public sealed class DashboardTests
{
	[Fact]
	public void Load_GivesTheJsonShapeThatThePageReads()
	{
		using TemporaryDirectory directory = new();
		string projects = directory.Combine("projects");
		Directory.CreateDirectory(projects);
		File.WriteAllText(Path.Combine(projects, "s.jsonl"), string.Join('\n',
			Records.User("s1"),
			Records.Assistant("s1", "m1"),
			Records.Assistant("s1", "m2", agentId: "acompact-1", isSidechain: true),
			Records.Title("s1", "custom-title", "Demo")) + "\n");
		string database = directory.Combine("usage.db");
		UsageScanner.Scan(database, [projects]);

		string json = JsonSerializer.Serialize(DashboardData.Load(database), DashboardServer.JsonOptions);
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement root = document.RootElement;

		foreach (string key in new[] { "all_models", "daily_by_model", "hourly_by_model", "sessions_all", "subagent_by_type", "top_dispatches", "generated_at" })
		{
			Assert.True(root.TryGetProperty(key, out _), $"Missing key '{key}'.");
		}

		JsonElement session = root.GetProperty("sessions_all")[0];
		Assert.Equal("Demo", session.GetProperty("topic").GetString());
		Assert.Equal(2, session.GetProperty("turns").GetInt64());
		Assert.Equal(1.0, session.GetProperty("duration_min").GetDouble());
		Assert.Equal(10, session.GetProperty("last_date").GetString()!.Length);

		JsonElement dispatch = root.GetProperty("top_dispatches")[0];
		Assert.Equal("auto-compact", dispatch.GetProperty("agent_type").GetString());
		Assert.Equal(JsonValueKind.Null, dispatch.GetProperty("duration_ms").ValueKind);

		JsonElement hourly = root.GetProperty("hourly_by_model")[0];
		Assert.Equal(15, hourly.GetProperty("hour").GetInt32());
	}

	[Fact]
	public void Load_GivesAnErrorWhenTheDatabaseDoesNotExist()
	{
		Assert.IsType<DashboardError>(DashboardData.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db")));
	}

	[Fact]
	public void BuildPage_InjectsVersionAndPrices()
	{
		string page = DashboardServer.BuildPage(ModelPricing.BuiltIn);

		Assert.DoesNotContain("__APP_CONFIG_JSON__", page);
		Assert.Contains("\"claude-opus-5-5\":{\"input\":4,\"output\":20,\"cache_write\":5,\"cache_read\":0.2}", page);
		Assert.Contains("\"family_fallbacks\":[[\"fable\",\"claude-fable-5-1\"]", page);
		Assert.Contains("\"billable_keywords\":[\"fable\"", page);
	}
}

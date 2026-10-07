namespace ClaudeUsage;

/// <summary>
/// 	Resolves the file system locations that the tool reads and writes.
/// </summary>
internal static class AppPaths
{
	/// <summary>
	/// 	Gets the Claude Code configuration directory.
	/// </summary>
	/// <remarks>
	/// 	Claude Code uses <c>CLAUDE_CONFIG_DIR</c> when it is set.
	/// 	Otherwise it uses <c>~/.claude</c>.
	/// </remarks>
	public static string ClaudeDirectory =>
		Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
			? configured
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

	/// <summary>
	/// 	Gets the path of the SQLite usage database.
	/// </summary>
	/// <remarks>
	/// 	Set <c>CLAUDE_USAGE_DB</c> to use a different file.
	/// </remarks>
	public static string DatabasePath =>
		Environment.GetEnvironmentVariable("CLAUDE_USAGE_DB") is { Length: > 0 } configured
			? configured
			: Path.Combine(ClaudeDirectory, "claude-usage.db");

	/// <summary>
	/// 	Gets the path of the optional file that overrides or adds model prices.
	/// </summary>
	public static string PricingOverridePath => Path.Combine(ClaudeDirectory, "claude-usage-pricing.json");

	/// <summary>
	/// 	Gets the directories that contain Claude Code JSONL transcripts.
	/// </summary>
	/// <remarks>
	/// 	The scanner skips a directory that does not exist.
	/// 	The Xcode directory applies only to macOS.
	/// </remarks>
	public static IReadOnlyList<string> DefaultProjectDirectories =>
	[
		Path.Combine(ClaudeDirectory, "projects"),
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			"Library", "Developer", "Xcode", "CodingAssistant", "ClaudeAgentConfig", "projects"),
	];
}

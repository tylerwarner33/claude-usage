using ClaudeUsage;
using ClaudeUsage.Dashboard;
using ClaudeUsage.Reports;

const string Usage = """

	Claude Code Usage Dashboard (C#)

	Usage:
	  claude-usage scan [--projects-dir PATH]   Scan JSONL files and update the database
	  claude-usage today                        Show today's usage by model
	  claude-usage week                         Show the last 7 days (per day + by model)
	  claude-usage stats                        Show all-time statistics
	  claude-usage dashboard [--projects-dir PATH] [--host HOST] [--port PORT] [--no-browser]
	                                            Start the dashboard and scan in the background
	  claude-usage --version                    Show the version

	Environment variables:
	  CLAUDE_USAGE_DB     Database path (default: ~/.claude/claude-usage.db)
	  CLAUDE_CONFIG_DIR   Claude Code configuration directory (default: ~/.claude)
	  HOST, PORT          Dashboard address (default: localhost, 8080)

	""";

if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
{
	Console.WriteLine(Usage);
	return 0;
}

if (args[0] is "--version" or "-V" or "version")
{
	Console.WriteLine(AppInfo.Version);
	return 0;
}

string[] options = args[1..];
string? projectsDirectory = NamedOption(options, "--projects-dir");
IReadOnlyList<string> projectDirectories = projectsDirectory is not null ? [projectsDirectory] : AppPaths.DefaultProjectDirectories;
string databasePath = AppPaths.DatabasePath;
ConsoleReports reports = new(databasePath, ModelPricing.Current, Console.Out);

switch (args[0])
{
	case "scan":
		UsageScanner.Scan(databasePath, projectDirectories, Console.Out);
		return 0;

	case "today":
		return reports.Today();

	case "week":
		return reports.Week();

	case "stats":
		return reports.Stats();

	case "dashboard":
		string host = NamedOption(options, "--host") ?? Environment.GetEnvironmentVariable("HOST") ?? "localhost";
		string portText = NamedOption(options, "--port") ?? Environment.GetEnvironmentVariable("PORT") ?? "8080";
		if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) is false || port is < 1 or > 65535)
		{
			Console.Error.WriteLine($"Port '{portText}' is not valid.");
			return 1;
		}

		DashboardOptions dashboard = new(host, port, options.Contains("--no-browser") is false, databasePath, projectDirectories);
		return await DashboardServer.RunAsync(dashboard);

	default:
		Console.Error.WriteLine($"Unknown command '{args[0]}'.");
		Console.WriteLine(Usage);
		return 1;
}

static string? NamedOption(string[] options, string name)
{
	int index = Array.IndexOf(options, name);
	return index >= 0 && index + 1 < options.Length ? options[index + 1] : null;
}

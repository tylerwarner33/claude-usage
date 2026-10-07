using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;

namespace ClaudeUsage.Dashboard;

/// <summary>
/// 	Settings for the dashboard web server.
/// </summary>
internal sealed record DashboardOptions(
	string Host,
	int Port,
	bool OpenBrowser,
	string DatabasePath,
	IReadOnlyList<string> ProjectDirectories);

/// <summary>
/// 	Serves the dashboard page and its JSON API on localhost.
/// </summary>
/// <remarks>
/// 	Routes:
/// 	<c>GET /</c> gives the page.
/// 	<c>GET /api/data</c> gives all usage data.
/// 	<c>POST /api/rescan</c> runs an incremental scan.
/// </remarks>
internal static class DashboardServer
{
	/// <summary>
	/// 	Gets the JSON options for all API responses: snake_case keys, as the page expects.
	/// </summary>
	public static JsonSerializerOptions JsonOptions { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
	};

	/// <summary>
	/// 	Starts the server, runs a scan in the background, and serves until Ctrl+C.
	/// </summary>
	/// <param name="options">
	/// 	The server settings.
	/// </param>
	/// <returns>
	/// 	The process exit code.
	/// </returns>
	public static async Task<int> RunAsync(DashboardOptions options)
	{
		string url = $"http://{options.Host}:{options.Port}";

		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls(url);
		WebApplication app = builder.Build();

		string page = BuildPage(ModelPricing.Current);
		byte[] icon = ReadResourceBytes("ClaudeUsage.icon.svg");

		app.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));
		app.MapGet("/index.html", () => Results.Content(page, "text/html; charset=utf-8"));
		app.MapGet("/icon.svg", (HttpContext context) =>
		{
			context.Response.Headers.CacheControl = "max-age=86400";
			return Results.Bytes(icon, "image/svg+xml");
		});
		app.MapGet("/api/data", () => Results.Json(DashboardData.Load(options.DatabasePath), JsonOptions));
		app.MapPost("/api/rescan", async () =>
		{
			ScanSummary summary = await Task.Run(() => UsageScanner.Scan(options.DatabasePath, options.ProjectDirectories));
			return Results.Json(summary, JsonOptions);
		});

		// Serve first, then scan: the page shows the current data at once and refreshes when the scan is done.
		app.Lifetime.ApplicationStarted.Register(() =>
		{
			Console.WriteLine($"Dashboard running at {url}");
			Console.WriteLine("Press Ctrl+C to stop.");
			_ = Task.Run(() => RunBackgroundScan(options));
			if (options.OpenBrowser)
			{
				OpenBrowser(url);
			}
		});

		try
		{
			await app.RunAsync();
			return 0;
		}
		catch (IOException exception) when (exception.InnerException is SocketException || exception.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
		{
			Console.Error.WriteLine($"Cannot start the dashboard on {url}: {exception.Message}");
			Console.Error.WriteLine("Use --port to select a different port.");
			return 1;
		}
	}

	/// <summary>
	/// 	Builds the page HTML with the runtime configuration (version and prices).
	/// </summary>
	/// <param name="pricing">
	/// 	The prices to send to the page.
	/// </param>
	/// <returns>
	/// 	The page HTML.
	/// </returns>
	public static string BuildPage(ModelPricing pricing)
	{
		// Declared as object: System.Text.Json then serializes the runtime (anonymous) type.
		object config = new
		{
			Version = AppInfo.Version,
			Pricing = new
			{
				pricing.Prices,
				FamilyFallbacks = ModelPricing.FamilyFallbacks.Select(fallback => new[] { fallback.Keyword, fallback.PriceKey }),
				BillableKeywords = ModelPricing.BillableKeywords,
			},
		};

		// The default encoder escapes '<' and '>', so the JSON cannot close the script element.
		string configJson = JsonSerializer.Serialize(config, JsonOptions);
		return ReadResourceText("ClaudeUsage.index.html").Replace("__APP_CONFIG_JSON__", configJson, StringComparison.Ordinal);
	}

	private static void RunBackgroundScan(DashboardOptions options)
	{
		try
		{
			Console.WriteLine("Scanning in the background...");
			ScanSummary summary = UsageScanner.Scan(options.DatabasePath, options.ProjectDirectories);
			Console.WriteLine($"Background scan complete: {summary.New} new, {summary.Updated} updated, {summary.Turns} turns.");
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"Background scan failed: {exception.Message}");
		}
	}

	private static void OpenBrowser(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"Cannot open a browser ({exception.Message}). Open {url} manually.");
		}
	}

	private static string ReadResourceText(string name)
	{
		using Stream stream = OpenResource(name);
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}

	private static byte[] ReadResourceBytes(string name)
	{
		using Stream stream = OpenResource(name);
		using MemoryStream copy = new();
		stream.CopyTo(copy);
		return copy.ToArray();
	}

	private static Stream OpenResource(string name)
	{
		return Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
			?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
	}
}

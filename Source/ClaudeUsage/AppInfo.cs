using System.Reflection;

namespace ClaudeUsage;

/// <summary>
/// 	Information about this application.
/// </summary>
internal static class AppInfo
{
	/// <summary>
	/// 	Gets the version from <c>Directory.Build.props</c>, without build metadata.
	/// </summary>
	public static string Version { get; } =
		(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
			.Split('+')[0];
}

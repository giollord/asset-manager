using System.Reflection;

namespace AssetManager.Core.Settings;

/// <summary>
/// The app's version, from the git tags at build time (MinVer, design D9): <c>1.2.0</c> for a build of the tag
/// <c>v1.2.0</c>, <c>1.2.1-alpha.0.5</c> five commits later.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Read(Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly);

    /// <summary>The informational version without the "+commit" build metadata.</summary>
    public static string Read(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus < 0 ? info : info[..plus];
    }
}

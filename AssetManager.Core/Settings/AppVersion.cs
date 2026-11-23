using System.Reflection;

namespace AssetManager.Core.Settings;

/// <summary>The app's version: set at publish (<c>-p:Version=1.2.0</c>, from the release tag), else the project default.</summary>
public static class AppVersion
{
    public static string Current { get; } = Read(Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly);

    /// <summary>The informational version without the "+commit" suffix the SDK appends.</summary>
    public static string Read(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus < 0 ? info : info[..plus];
    }
}

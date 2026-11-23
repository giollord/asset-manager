using System.Runtime.CompilerServices;
using AssetManager.Core.Settings;

namespace AssetManager.Tests;

internal static class TestDataRoot
{
    /// <summary>Keeps app data written by tests (temp, cache, models) out of the developer's real data folder.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        AppPaths.Use(new AppDataLocation(Path.Combine(Path.GetTempPath(), "am-tests", "appdata"), DataMode.Override));
}

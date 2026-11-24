using System.Reflection;
using AssetManager.Core.Settings;

namespace AssetManager.Tests.Core;

/// <summary>Spec "Versioned releases": the version comes from the git tags (MinVer), without the commit metadata.</summary>
public class AppVersionTests
{
    [Fact]
    public void Version_is_the_tag_version_without_the_commit()
    {
        var assembly = typeof(AppVersion).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var version = AppVersion.Read(assembly);

        Assert.DoesNotContain('+', version);
        Assert.StartsWith(version, info);
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", version);
        // Not the SDK's default: the build saw the repository's tags.
        Assert.NotEqual("1.0.0", version);
        Assert.NotEqual("0.0.0-alpha.0", version);
    }
}

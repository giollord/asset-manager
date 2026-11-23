using AssetManager.Processing.Locations;

namespace AssetManager.Tests.Processing;

public class WindowsVolumeServiceTests
{
    [Fact]
    public void Lists_mounted_volumes_by_serial()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        using var svc = new WindowsVolumeService();
        var volumes = svc.GetVolumes();
        Assert.NotEmpty(volumes);
        Assert.All(volumes, v => Assert.Matches("^[0-9A-F]{8}$", v.Id));

        var system = Path.GetPathRoot(Environment.SystemDirectory)!;
        var v = svc.GetVolumeForPath(Path.Combine(system, "Windows", "notepad.exe"));
        Assert.NotNull(v);
        Assert.Equal(system, v.RootPath, ignoreCase: true);
        Assert.Equal(v.RootPath, svc.GetRoot(v.Id));
        Assert.Null(svc.GetRoot("00000000"));
        TestContext.Current.SendDiagnosticMessage(string.Join(", ", volumes.Select(x => $"{x.RootPath}={x.Id} '{x.Label}'")));
    }
}

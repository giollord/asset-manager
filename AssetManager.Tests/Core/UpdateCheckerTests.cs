using System.Net;
using System.Text;
using AssetManager.Core;
using AssetManager.Core.Settings;
using AssetManager.Core.Updates;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests.Core;

/// <summary>Spec "Update check" and "Update notification" (design D13).</summary>
public sealed class UpdateCheckerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>Answers every request with the given response, or throws, and records the requests.</summary>
    public sealed class FakeGitHub(Func<HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public static HttpResponseMessage Release(string tag, string url = "https://github.com/giollord/asset-manager/releases/tag/v1.3.0") =>
            new(HttpStatusCode.OK) { Content = new StringContent($$"""{"tag_name":"{{tag}}","html_url":"{{url}}","name":"Release"}""", Encoding.UTF8, "application/json") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return respond is null
                ? throw new HttpRequestException("No such host is known. (api.github.com:443)")
                : Task.FromResult(respond());
        }
    }

    private JsonAppSettingsStore Settings(bool enabled = true)
    {
        var settings = new JsonAppSettingsStore(Path.Combine(_dir.Path, "settings.json"));
        if (enabled) settings.Update(s => s.UpdateCheckEnabled = true);
        return settings;
    }

    [Fact]
    public async Task Newer_release()
    {
        var settings = Settings();
        var github = new FakeGitHub(() => FakeGitHub.Release("v1.3.0"));
        var now = new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc);
        var checker = new UpdateChecker(settings, github) { CurrentVersion = "1.2.0", Clock = () => now, SkipAtStartup = false };
        var found = 0;
        checker.NewVersionFound += () => found++;

        var update = await checker.CheckAtStartupAsync(Ct);

        Assert.Equal(new UpdateInfo("1.3.0", "https://github.com/giollord/asset-manager/releases/tag/v1.3.0"), update);
        Assert.Equal(update, checker.Available);
        Assert.Equal(1, found);
        Assert.Equal(update, checker.TakeNotice());
        Assert.Null(checker.TakeNotice()); // one notification

        // The request: the project's latest release, with only a plain User-Agent.
        var request = Assert.Single(github.Requests);
        Assert.Equal(UpdateChecker.LatestReleaseApi, request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("AssetManager/1.2.0", request.Headers.UserAgent.ToString());
        Assert.Equal(["User-Agent"], request.Headers.Select(h => h.Key));
        Assert.Null(request.Content);

        // Within a day the startup check makes no request; a later check of the same version announces nothing.
        now = now.AddHours(23);
        Assert.Equal(update, await checker.CheckAtStartupAsync(Ct));
        Assert.Single(github.Requests);
        now = now.AddHours(2);
        await checker.CheckAtStartupAsync(Ct);
        Assert.Equal(2, github.Requests.Count);
        Assert.Equal(1, found);

        // Settings remember it across restarts.
        var reloaded = new UpdateChecker(new JsonAppSettingsStore(Path.Combine(_dir.Path, "settings.json")), github) { CurrentVersion = "1.2.0" };
        Assert.Equal(update, reloaded.Available);
        Assert.False(settings.Current.LastCheckFailed);
        Assert.Equal("1.3.0", settings.Current.LastSeenVersion);
    }

    [Fact]
    public async Task Offline()
    {
        var settings = Settings();
        var checker = new UpdateChecker(settings, new FakeGitHub()) { CurrentVersion = "1.2.0", SkipAtStartup = false };

        var update = await checker.CheckAtStartupAsync(Ct); // no exception

        Assert.Null(update);
        Assert.True(settings.Current.LastCheckFailed);
        Assert.NotNull(settings.Current.LastUpdateCheck);
        Assert.Null(checker.TakeNotice());
    }

    [Fact]
    public async Task Server_error_and_bad_data_count_as_failed()
    {
        var settings = Settings();
        var error = new UpdateChecker(settings, new FakeGitHub(() => new HttpResponseMessage(HttpStatusCode.Forbidden))) { CurrentVersion = "1.2.0" };
        Assert.Null(await error.CheckAsync(Ct));
        Assert.True(settings.Current.LastCheckFailed);

        var bad = new UpdateChecker(settings, new FakeGitHub(() => FakeGitHub.Release("nightly", "javascript:alert(1)"))) { CurrentVersion = "1.2.0" };
        Assert.Null(await bad.CheckAsync(Ct));
        Assert.True(settings.Current.LastCheckFailed);
    }

    [Fact]
    public async Task Default_off()
    {
        var settings = Settings(enabled: false);
        var github = new FakeGitHub(() => FakeGitHub.Release("v9.0.0"));
        var checker = new UpdateChecker(settings, github) { CurrentVersion = "1.2.0", SkipAtStartup = false };

        Assert.False(new AppSettings().UpdateCheckEnabled);
        await new UpdateStartupTask(checker).RunAsync(Ct);
        await checker.CheckAtStartupAsync(Ct);
        await checker.CheckAsync(Ct);

        Assert.Empty(github.Requests);
        Assert.Null(settings.Current.LastUpdateCheck);
    }

    [Fact]
    public async Task Debug_builds_skip_the_startup_check()
    {
        var github = new FakeGitHub(() => FakeGitHub.Release("v1.3.0"));
        var checker = new UpdateChecker(Settings(), github) { CurrentVersion = "1.2.0", SkipAtStartup = true };
        await checker.CheckAtStartupAsync(Ct);
        Assert.Empty(github.Requests);
        await checker.CheckAsync(Ct); // "Check now" still works
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task Same_or_older_release_is_not_available()
    {
        var checker = new UpdateChecker(Settings(), new FakeGitHub(() => FakeGitHub.Release("v1.2.0"))) { CurrentVersion = "1.2.0" };
        Assert.Null(await checker.CheckAsync(Ct));
        Assert.Null(checker.TakeNotice());
    }

    [Theory]
    [InlineData("1.3.0", "1.2.0", true)]
    [InlineData("v1.10.0", "1.9.3", true)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.2.0", "1.2.0-alpha.0.12", true)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.10", false)]
    [InlineData("1.2.0-rc.1", "1.2.0-beta.3", true)]
    [InlineData("1.1.9", "1.2.0", false)]
    [InlineData("garbage", "1.2.0", false)]
    public void Version_comparison(string version, string than, bool newer) => Assert.Equal(newer, UpdateChecker.IsNewer(version, than));
}

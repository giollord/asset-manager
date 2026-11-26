using System.Net.Http.Headers;
using System.Text.Json;
using AssetManager.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Core.Updates;

/// <summary>A release newer than the running app.</summary>
public sealed record UpdateInfo(string Version, string Url);

/// <summary>
/// The opt-in update check (design D13, spec "Update check"): one GET of the project's latest GitHub release with only
/// a <c>User-Agent: AssetManager/&lt;version&gt;</c> header and a 10 s timeout, at most once a day at startup when turned
/// on, or on "Check now". It never downloads or installs anything. A failure is remembered quietly for About.
/// </summary>
public sealed class UpdateChecker(IAppSettingsStore settings, HttpMessageHandler? handler = null, ILogger<UpdateChecker>? logger = null)
{
    public const string LatestReleaseApi = "https://api.github.com/repos/giollord/asset-manager/releases/latest";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>The running version; replaceable in tests.</summary>
    public string CurrentVersion { get; init; } = AppVersion.Current;

    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    /// <summary>Debug builds never check at startup (design D13); "Check now" still works.</summary>
    public bool SkipAtStartup { get; init; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Raised once per newer version, the first time a check finds it; the notice waits in <see cref="TakeNotice"/>.</summary>
    public event Action? NewVersionFound;

    private UpdateInfo? _notice;

    /// <summary>
    /// The newer version to announce, once: a check at startup may finish before the UI listens, so the notice waits
    /// here until the notification takes it.
    /// </summary>
    public UpdateInfo? TakeNotice() => Interlocked.Exchange(ref _notice, null);

    /// <summary>The newer release the last successful check found, if it is newer than this app.</summary>
    public UpdateInfo? Available =>
        settings.Current is { LatestVersion: { } v, LatestReleaseUrl: { } url } && IsNewer(v, CurrentVersion) ? new UpdateInfo(v, url) : null;

    /// <summary>The startup check: only when turned on, not in Debug builds, and at most once a day.</summary>
    public async Task<UpdateInfo?> CheckAtStartupAsync(CancellationToken ct = default)
    {
        var s = settings.Current;
        if (!s.UpdateCheckEnabled || SkipAtStartup) return null;
        if (s.LastUpdateCheck is { } last && Clock() - last < Interval) return Available;
        return await CheckAsync(ct);
    }

    /// <summary>Asks GitHub for the latest release. Never throws for network or format errors.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (!settings.Current.UpdateCheckEnabled) return null;
        string version, url;
        try
        {
            using var client = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is null) { Timeout = Timeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AssetManager", CurrentVersion));
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            version = json.RootElement.GetProperty("tag_name").GetString()!.Trim().TrimStart('v', 'V');
            url = json.RootElement.GetProperty("html_url").GetString()!;
            if (Parse(version) is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                throw new FormatException($"Unexpected release data: {version} {url}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or FormatException
                                      or KeyNotFoundException or InvalidOperationException or NullReferenceException)
        {
            if (ct.IsCancellationRequested) throw;
            _log.LogInformation("Update check failed: {Error}", e.Message);
            settings.Update(s => (s.LastUpdateCheck, s.LastCheckFailed) = (Clock(), true));
            return null;
        }

        var announce = IsNewer(version, CurrentVersion) && (settings.Current.LastSeenVersion is not { } seen || IsNewer(version, seen));
        settings.Update(s =>
        {
            (s.LastUpdateCheck, s.LastCheckFailed, s.LatestVersion, s.LatestReleaseUrl) = (Clock(), false, version, url);
            if (announce) s.LastSeenVersion = version;
        });
        var available = Available;
        if (announce && available is not null)
        {
            _notice = available;
            NewVersionFound?.Invoke();
        }
        return available;
    }

    /// <summary>Whether <paramref name="version"/> is newer than <paramref name="than"/>; a release beats its pre-releases.</summary>
    public static bool IsNewer(string version, string than)
    {
        if (Parse(version) is not { } a || Parse(than) is not { } b) return false;
        var c = a.Core.CompareTo(b.Core);
        if (c != 0) return c > 0;
        if (a.Pre is null) return b.Pre is not null;
        return b.Pre is not null && ComparePre(a.Pre, b.Pre) > 0;
    }

    private static (Version Core, string? Pre)? Parse(string text)
    {
        var t = text.Trim().TrimStart('v', 'V');
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        var dash = t.IndexOf('-');
        var core = dash < 0 ? t : t[..dash];
        if (!Version.TryParse(core.Count(ch => ch == '.') == 0 ? core + ".0" : core, out var v)) return null;
        return (new Version(v.Major, v.Minor, Math.Max(0, v.Build)), dash < 0 ? null : t[(dash + 1)..]);
    }

    /// <summary>SemVer pre-release order: dot-separated, numeric parts numerically, others ordinally.</summary>
    private static int ComparePre(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xn = int.TryParse(x[i], out var xi);
            var yn = int.TryParse(y[i], out var yi);
            var c = xn && yn ? xi.CompareTo(yi) : xn ? -1 : yn ? 1 : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }
}

/// <summary>The daily update check at startup, in the background, when the user turned it on.</summary>
public sealed class UpdateStartupTask(UpdateChecker checker) : IStartupTask
{
    public int Order => 70;

    public Task RunAsync(CancellationToken ct)
    {
        _ = Task.Run(() => checker.CheckAtStartupAsync(ct), ct);
        return Task.CompletedTask;
    }
}

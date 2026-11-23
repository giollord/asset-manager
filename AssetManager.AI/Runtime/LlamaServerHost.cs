using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AssetManager.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.AI.Runtime;

public enum AiServerState { NotConfigured, Starting, Ready, Failed, Stopped }

/// <summary>Input modalities the loaded model supports (from <c>/props</c>).</summary>
public sealed record AiModalities(bool Vision, bool Audio)
{
    public static readonly AiModalities None = new(false, false);
}

/// <param name="ContextSize">Context per request.</param>
/// <param name="Parallel">Server slots (requests handled at once); the server gets <c>ContextSize * Parallel</c> context.</param>
public sealed record LlamaServerOptions(string ModelPath, string? MmprojPath, AiAcceleration Acceleration, bool Embedding, int ContextSize, int Parallel = 1);

/// <summary>
/// Runs one bundled <c>llama-server</c> process (design D7): a free port on 127.0.0.1 only, tied to the app's lifetime
/// by a Job object, readiness via <c>/health</c>, modalities via <c>/props</c>, and automatic restarts after crashes.
/// GPU (Vulkan) is tried first unless CPU is chosen; if it does not come up, the CPU build is used.
/// </summary>
public sealed class LlamaServerHost : IAsyncDisposable
{
    private const int MaxRestarts = 3;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);

    private readonly ILogger _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Queue<string> _tail = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private LlamaServerOptions? _options;
    private bool _stopping;
    private int _restarts;
    private DateTime _restartWindow = DateTime.UtcNow;

    public LlamaServerHost(string name, ILogger? logger = null, string? binRoot = null)
    {
        Name = name;
        _log = logger ?? NullLogger.Instance;
        BinRoot = binRoot ?? Path.Combine(AppContext.BaseDirectory, "llama");
    }

    public string Name { get; }
    public string BinRoot { get; }
    public AiServerState State { get; private set; } = AiServerState.NotConfigured;
    public string? Error { get; private set; }
    public Uri? Endpoint { get; private set; }
    public string? Backend { get; private set; }
    public AiModalities Modalities { get; private set; } = AiModalities.None;

    /// <summary>Slots of the running server: how many requests it handles at once.</summary>
    public int Parallel { get; private set; } = 1;

    public event Action? StateChanged;

    /// <summary>Stops a running server and starts one with these options; returns when it is ready or has failed.</summary>
    public async Task StartAsync(LlamaServerOptions options, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await StopCoreAsync();
            _options = options;
            _restarts = 0;
            await LaunchAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            Set(AiServerState.Stopped, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LaunchAsync(CancellationToken ct)
    {
        var o = _options!;
        if (!File.Exists(o.ModelPath))
        {
            Set(AiServerState.Failed, $"Model file not found: {o.ModelPath}");
            return;
        }
        Set(AiServerState.Starting, null);
        var backends = (o.Acceleration switch
        {
            AiAcceleration.Gpu => ["vulkan"],
            AiAcceleration.Cpu => ["cpu"],
            _ => new[] { "vulkan", "cpu" },
        }).Where(b => File.Exists(ServerExe(b))).ToList();
        if (backends.Count == 0)
        {
            Set(AiServerState.Failed, $"llama-server was not found under {BinRoot}.");
            return;
        }

        foreach (var backend in backends)
        {
            // Several slots need more memory: on failure try one slot on the same backend before the next backend.
            foreach (var slots in o.Parallel > 1 ? [o.Parallel, 1] : new[] { 1 })
            {
                ct.ThrowIfCancellationRequested();
                var error = await TryBackendAsync(backend, o with { Parallel = slots }, ct);
                if (error is null)
                {
                    Parallel = slots;
                    Set(AiServerState.Ready, null);
                    return;
                }
                _log.LogWarning("AI server {Name} ({Backend}, {Slots} slots) did not start: {Error}", Name, backend, slots, error);
                Error = error;
            }
        }
        Set(AiServerState.Failed, Error);
    }

    /// <summary>Starts the server on one backend; returns null when healthy, else the reason.</summary>
    private async Task<string?> TryBackendAsync(string backend, LlamaServerOptions o, CancellationToken ct)
    {
        var port = FreePort();
        var args = new List<string> { "-m", o.ModelPath, "--host", "127.0.0.1", "--port", port.ToString(), "-c", (o.ContextSize * Math.Max(1, o.Parallel)).ToString(),
            "--no-webui", "--offline", "-np", Math.Max(1, o.Parallel).ToString(), "--no-kv-unified", "-ngl", backend == "cpu" ? "0" : "999" };
        if (o.Embedding) args.AddRange(["--embedding", "--pooling", "mean"]);
        else
        {
            if (!string.IsNullOrWhiteSpace(o.MmprojPath) && File.Exists(o.MmprojPath)) args.AddRange(["--mmproj", o.MmprojPath]);
            args.AddRange(["--jinja", "--reasoning", "off"]); // direct structured answers, no thinking
        }

        var psi = new ProcessStartInfo(ServerExe(backend))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(ServerExe(backend))!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        lock (_tail) _tail.Clear();
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.Start();
        JobObject.Assign(process);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        _log.LogInformation("Starting AI server {Name} ({Backend}) on port {Port}: {Model}", Name, backend, port, Path.GetFileName(o.ModelPath));

        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        var started = Stopwatch.StartNew();
        while (started.Elapsed < StartTimeout)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) return $"exited with code {process.ExitCode}: {Tail()}";
            try
            {
                using var health = await _http.GetAsync(new Uri(endpoint, "health"), ct);
                if (health.StatusCode == HttpStatusCode.OK) break;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(250, ct);
        }
        if (started.Elapsed >= StartTimeout)
        {
            Kill(process);
            return "timed out while loading the model";
        }

        _process = process;
        Endpoint = endpoint;
        Backend = backend;
        Modalities = o.Embedding ? AiModalities.None : await ReadModalitiesAsync(endpoint, ct);
        process.Exited += OnExited;
        _log.LogInformation("AI server {Name} ready ({Backend}) in {Seconds:0.0} s; modalities: vision={Vision}, audio={Audio}",
            Name, backend, started.Elapsed.TotalSeconds, Modalities.Vision, Modalities.Audio);
        return null;
    }

    private async Task<AiModalities> ReadModalitiesAsync(Uri endpoint, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(new Uri(endpoint, "props"), ct));
            if (doc.RootElement.TryGetProperty("modalities", out var m))
                return new AiModalities(
                    m.TryGetProperty("vision", out var v) && v.ValueKind == JsonValueKind.True,
                    m.TryGetProperty("audio", out var a) && a.ValueKind == JsonValueKind.True);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            _log.LogWarning("Could not read /props of {Name}: {Error}", Name, e.Message);
        }
        return AiModalities.None;
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (_stopping || sender != _process) return;
        var code = _process?.ExitCode;
        _log.LogError("AI server {Name} exited unexpectedly (code {Code}): {Tail}", Name, code, Tail());
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                if (_stopping || _options is null) return;
                if (DateTime.UtcNow - _restartWindow > TimeSpan.FromMinutes(10))
                {
                    _restartWindow = DateTime.UtcNow;
                    _restarts = 0;
                }
                if (++_restarts > MaxRestarts)
                {
                    Set(AiServerState.Failed, $"The AI server keeps crashing (code {code}).");
                    return;
                }
                _process = null;
                await LaunchAsync(CancellationToken.None);
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private async Task StopCoreAsync()
    {
        var p = _process;
        _process = null;
        Endpoint = null;
        Modalities = AiModalities.None;
        if (p is null) return;
        _stopping = true;
        try
        {
            Kill(p);
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException) { }
        finally
        {
            p.Dispose();
            _stopping = false;
        }
        _log.LogInformation("AI server {Name} stopped", Name);
    }

    private void Set(AiServerState state, string? error)
    {
        State = state;
        Error = error;
        StateChanged?.Invoke();
    }

    private void Capture(string? line)
    {
        if (line is null) return;
        lock (_tail)
        {
            _tail.Enqueue(line);
            while (_tail.Count > 40) _tail.Dequeue();
        }
        _log.LogDebug("[{Name}] {Line}", Name, line);
    }

    private string Tail()
    {
        lock (_tail) return string.Join(" | ", _tail.TakeLast(6));
    }

    private string ServerExe(string backend) => Path.Combine(BinRoot, backend, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}

using System.Diagnostics;
using System.Text;
using FFMpegCore;

namespace AssetManager.Processing.Media;

/// <summary>Locates the bundled FFmpeg (output folder <c>ffmpeg/</c>, else PATH) and runs it.</summary>
public static class Ffmpeg
{
    private static readonly Lazy<string?> Folder = new(() =>
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        return File.Exists(Path.Combine(bundled, "ffprobe.exe")) || File.Exists(Path.Combine(bundled, "ffprobe")) ? bundled : null;
    });

    /// <summary>FFMpegCore options pointing at the bundled binaries.</summary>
    public static FFOptions Options => new() { BinaryFolder = Folder.Value ?? "" };

    public static string FfmpegPath => Folder.Value is { } f ? Path.Combine(f, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg") : "ffmpeg";

    public static Task<IMediaAnalysis> ProbeAsync(string path, CancellationToken ct) =>
        FFProbe.AnalyseAsync(path, Options, null, ct);

    /// <summary>Runs ffmpeg with the given arguments; throws with the tail of stderr on failure.</summary>
    public static async Task RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        var stderr = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None)) is not null)
                if (stderr.Length < 4000) stderr.AppendLine(line);
        }, CancellationToken.None);
        var outTask = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        await Task.WhenAll(errTask, outTask);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}): {stderr.ToString().Trim()}");
    }

    /// <summary>A temp folder for ffmpeg outputs, deleted on dispose.</summary>
    public static TempFolder CreateTemp() => new();

    public sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AssetManager", "ffmpeg", Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

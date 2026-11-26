using System.Runtime.InteropServices;

namespace AssetManager.Core.Jobs;

/// <summary>
/// Runs background work at low CPU, memory and I/O priority (design D11), so a busy job does not starve the window's
/// own disk reads. <see cref="Enter"/> sets an ambient flag that flows with the execution context into everything the
/// work starts (awaits, <c>Task.Run</c>, <c>Parallel.ForEachAsync</c>). Whenever a thread starts or stops running such
/// a context, it is switched into or out of Windows background mode. Threads with a <see cref="SynchronizationContext"/>
/// (the UI thread) are never switched. Off Windows nothing changes.
/// </summary>
public static class BackgroundIo
{
    private static readonly AsyncLocal<bool> Ambient = new(OnChanged);

    /// <summary>Whether this mechanism has put the current thread into background mode.</summary>
    [ThreadStatic] private static bool _threadInBackground;

    /// <summary>True when the current thread was switched to background mode by <see cref="Enter"/>.</summary>
    public static bool IsCurrentThreadBackground => _threadInBackground;

    /// <summary>True when the current code runs inside an <see cref="Enter"/> scope.</summary>
    public static bool IsActive => Ambient.Value;

    /// <summary>Marks the current flow, and everything it starts, as background work until the scope is disposed.</summary>
    public static Scope Enter()
    {
        var previous = Ambient.Value;
        Ambient.Value = true;
        return new Scope(previous);
    }

    public readonly struct Scope(bool previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }

    private static void OnChanged(AsyncLocalValueChangedArgs<bool> args)
    {
        if (args.CurrentValue) Begin();
        else End();
    }

    private static void Begin()
    {
        if (_threadInBackground || SynchronizationContext.Current is not null || !OperatingSystem.IsWindows()) return;
        _threadInBackground = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
    }

    private static void End()
    {
        if (!_threadInBackground) return;
        _threadInBackground = false;
        SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
    }

    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);
}

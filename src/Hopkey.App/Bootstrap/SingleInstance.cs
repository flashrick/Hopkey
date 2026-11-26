using Hopkey.Core.Util;

namespace Hopkey.App.Bootstrap;

/// <summary>Named mutex + named event: the second instance signals the first (which opens the launcher) and exits.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Hopkey.Instance";
    private const string EventName = @"Local\Hopkey.Activate";
    private Mutex? _mutex;
    private EventWaitHandle? _event;
    private Thread? _listener;
    private volatile bool _disposed;

    public bool TryAcquire(int waitPid)
    {
        if (waitPid > 0)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(waitPid);
                p.WaitForExit(10_000);
            }
            catch { }
        }
        _mutex = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            try
            {
                if (!_mutex.WaitOne(waitPid > 0 ? 3000 : 0)) { _mutex.Dispose(); _mutex = null; return false; }
            }
            catch (AbandonedMutexException) { }
        }
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        return true;
    }

    public static void SignalExisting()
    {
        try
        {
            // We were just started by the user, so we hold foreground rights: pass them to the running instance
            // so its launcher window can actually take the foreground.
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("Hopkey"))
            {
                using (p)
                    if (p.Id != Environment.ProcessId) Core.Native.User32.AllowSetForegroundWindow(p.Id);
            }
            using var ev = EventWaitHandle.OpenExisting(EventName);
            ev.Set();
            Thread.Sleep(300); // stay alive briefly so focus isn't handed back when we exit before the launcher activates
        }
        catch (Exception ex) { Logger.Warn("Could not signal the running instance (elevation mismatch?)", ex); }
    }

    public void Listen(Action onActivate)
    {
        _listener = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    if (_event!.WaitOne(1000) && !_disposed) onActivate();
                }
                catch (ObjectDisposedException) { break; }
            }
        }) { IsBackground = true, Name = "CL-Instance" };
        _listener.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _event?.Dispose();
    }
}

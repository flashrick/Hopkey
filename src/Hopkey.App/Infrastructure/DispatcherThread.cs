using System.Windows.Threading;
using Hopkey.Core.Util;

namespace Hopkey.App.Infrastructure;

/// <summary>A background STA thread running its own WPF Dispatcher (message pump).</summary>
public sealed class DispatcherThread : IDisposable
{
    private readonly Thread _thread;
    public Dispatcher Dispatcher { get; private set; } = null!;
    public string Name { get; }
    public int ManagedThreadId => _thread.ManagedThreadId;

    public DispatcherThread(string name, ThreadPriority priority = ThreadPriority.Normal)
    {
        Name = name;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Dispatcher = Dispatcher.CurrentDispatcher;
            Dispatcher.UnhandledException += (_, e) =>
            {
                Logger.Error($"Unhandled exception on {name}", e.Exception);
                e.Handled = true;
            };
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = name, Priority = priority };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
    }

    public void Post(Action action) => Dispatcher.BeginInvoke(action);

    public Task<T> InvokeAsync<T>(Func<T> func) => Dispatcher.InvokeAsync(func).Task;

    public Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

    /// <summary>Invoke with timeout. Throws TimeoutException if the thread doesn't respond in time.</summary>
    public async Task<T> InvokeAsync<T>(Func<T> func, TimeSpan timeout, CancellationToken ct = default)
    {
        var op = Dispatcher.InvokeAsync(func, DispatcherPriority.Normal, ct);
        var task = op.Task;
        var done = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        if (done != task)
        {
            op.Abort();
            throw new TimeoutException($"{Name} did not respond within {timeout.TotalMilliseconds}ms");
        }
        return await task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); } catch { }
        _thread.Join(1000);
    }
}

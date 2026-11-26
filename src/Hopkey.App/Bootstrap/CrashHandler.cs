using System.Windows;
using System.Windows.Threading;
using Hopkey.Core.Util;

namespace Hopkey.App.Bootstrap;

public static class CrashHandler
{
    private static readonly Queue<DateTime> s_recent = new();

    public static void Install(Application app)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var text = $"Fatal unhandled exception (terminating={e.IsTerminating}):\n{e.ExceptionObject}";
            Logger.Error(text);
            Logger.WriteCrash(text);
            Logger.Flush();
        };
        app.DispatcherUnhandledException += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("UI thread exception", e.Exception);
        e.Handled = true;
        var now = DateTime.UtcNow;
        s_recent.Enqueue(now);
        while (s_recent.Count > 0 && now - s_recent.Peek() > TimeSpan.FromMinutes(1)) s_recent.Dequeue();
        if (s_recent.Count > 5)
        {
            Logger.WriteCrash("Too many UI exceptions within a minute:\n" + e.Exception);
            MessageBox.Show("Hopkey encountered repeated errors and will exit.\n\n" + e.Exception.Message, "Hopkey",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown(1);
        }
    }
}

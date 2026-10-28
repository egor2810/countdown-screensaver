using System;
using System.Threading.Tasks;
using System.Windows;

namespace CountdownSaver;

// Windows запускает заставку с аргументами:
//   /s        — показать заставку
//   /c[:hwnd] — окно настроек (также без аргументов)
//   /p hwnd   — маленькое превью в окне «Параметры заставки» (не поддерживается)
// Для отладки: /w — заставка в обычном окне, не закрывается от мыши и показывает ошибки.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "/c";

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("Unhandled: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Task", e.Exception);
            e.SetObserved();
        };

        var app = new Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("UI", e.Exception);
            e.Handled = true;
        };

        if (mode.StartsWith("/p"))
            return 0;

        if (mode.StartsWith("/s") || mode.StartsWith("/w"))
        {
            bool debug = mode.StartsWith("/w");
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var settings = Settings.Load();
            var media = new MediaWatcher();
            var system = settings.ShowSystem ? new SystemMonitor() : null;

            if (debug)
            {
                var window = new SaverWindow(null, settings, media, system, true);
                window.Closed += (_, _) => app.Shutdown();
                window.Show();
            }
            else
            {
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    bool full = screen.Primary || settings.AllMonitors;
                    new SaverWindow(screen.Bounds, settings, full ? media : null, full ? system : null, full).Show();
                }
            }

            if (settings.ShowMedia)
                media.Start();
            system?.Start();
            return app.Run();
        }

        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        return app.Run(new ConfigWindow());
    }
}

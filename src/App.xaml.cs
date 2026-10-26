using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WeatherPanel;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.W("EntryPointReached", string.Join(" ", e.Args));

        // 三类异常都记录，但不设 Handled=true：记录不等于掩盖，危险状态照常退出
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Ex("AppDomain.UnhandledException",
                   ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject?.ToString() ?? "unknown"));
        DispatcherUnhandledException += (_, ex) => Log.Ex("DispatcherUnhandledException", ex.Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => Log.Ex("UnobservedTaskException", ex.Exception);

        // 单实例只用命名 Mutex（不依赖 PID 文件，避免 PID 复用误判）
        _mutex = new Mutex(true, @"Local\WeatherPanel_SingleInstance", out bool created);
        if (!created)
        {
            Log.W("SingleInstanceBlocked", "another instance owns the mutex -> exit");
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Log.W("ApplicationCreated", "ShutdownMode=" + ShutdownMode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.W("ApplicationExit", "code=" + e.ApplicationExitCode);
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

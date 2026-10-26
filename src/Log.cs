using System;
using System.IO;
using System.Text;
using System.Threading;

namespace WeatherPanel;

/// <summary>
/// 生命周期日志：每次事件立即落盘（不缓冲），用于定位「窗口没出现」「进程消失」这类
/// 进程来不及走正常退出路径的问题。写到 %LOCALAPPDATA%\WeatherPanel\Logs\。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WeatherPanel", "Logs");
    private static string _file = "";
    private static bool _initTried;

    private static void Init()
    {
        if (_initTried) return;
        _initTried = true;
        try
        {
            Directory.CreateDirectory(Dir);
            // 单文件滚动：超过 512KB 就把旧的改名，避免日志无限增长
            _file = Path.Combine(Dir, "lifecycle.log");
            var fi = new FileInfo(_file);
            if (fi.Exists && fi.Length > 512 * 1024)
            {
                var old = Path.Combine(Dir, "lifecycle.old.log");
                if (File.Exists(old)) File.Delete(old);
                File.Move(_file, old);
            }
        }
        catch
        {
            _file = "";
        }
    }

    public static void W(string ev, string detail = "")
    {
        Init();
        string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} pid={1} tid={2} {3}{4}",
            DateTime.Now, Environment.ProcessId, Environment.CurrentManagedThreadId,
            ev, string.IsNullOrEmpty(detail) ? "" : " | " + detail);
        if (string.IsNullOrEmpty(_file))
        {
            Console.Error.WriteLine(line);   // 日志不可用时退到 stderr，不静默丢弃
            return;
        }
        try
        {
            lock (Gate)
                File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    public static void Ex(string ev, Exception ex)
    {
        W(ev, ex.GetType().FullName + ": " + ex.Message);
        try
        {
            Directory.CreateDirectory(Dir);
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(Dir, "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " pid=" + Environment.ProcessId +
                    " " + ev + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch { }
    }
}

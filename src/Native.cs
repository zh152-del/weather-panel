using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WeatherPanel;

/// <summary>窗口层级与状态查询。所有 Z 序操作都检查返回值并把 Win32 错误码写进日志，
/// 避免"调用了 API 就以为生效"。</summary>
internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                            int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private static readonly IntPtr HwndTop = new(0);
    private const uint SwpNoActivate = 0x0010, SwpNoMove = 0x0002, SwpNoSize = 0x0001;

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;

    // ---------- 只读查询（日志取证用）----------
    public static string Rect(IntPtr h)
    {
        if (h == IntPtr.Zero) return "none";
        return GetWindowRect(h, out var r) ? $"({r.Left},{r.Top},{r.Right},{r.Bottom})" : "fail";
    }

    public static bool IsWin(IntPtr h) => h != IntPtr.Zero && IsWindow(h);
    public static bool IsVis(IntPtr h) => h != IntPtr.Zero && IsWindowVisible(h);
    public static int ExStyle(IntPtr h) => h == IntPtr.Zero ? 0 : GetWindowLongPtr(h, GwlExStyle).ToInt32();

    /// <summary>DWM cloaked：被系统隐藏（如虚拟桌面切换）。1=cloaked，-1=查询失败。</summary>
    public static int Cloaked(IntPtr h)
    {
        if (h == IntPtr.Zero) return -1;
        try { return DwmGetWindowAttribute(h, 14, out int v, sizeof(int)) == 0 ? v : -1; }
        catch { return -1; }
    }

    // ---------- Z 序操作：带返回值与错误码 ----------
    private static bool Apply(Window w, IntPtr after, string tag)
    {
        IntPtr h = Handle(w);
        if (!IsWin(h)) { Log.W("ZOrderChangeFailed", tag + " hwnd invalid"); return false; }
        bool ok = SetWindowPos(h, after, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        Log.W(ok ? "ZOrderChangeSucceeded" : "ZOrderChangeFailed",
              $"{tag} after={after.ToInt64()} err={err} rect={Rect(h)} ex=0x{ExStyle(h):X}");
        return ok;
    }

    public static bool SetTopmost(Window w) => Apply(w, HwndTopmost, "topmost");
    public static bool ClearTopmost(Window w) => Apply(w, HwndNoTopMost, "notopmost");
    /// <summary>提到普通窗口层顶部（不进入置顶层，后续程序仍能盖住它）。</summary>
    public static bool RaiseInNormalLayer(Window w) => Apply(w, HwndTop, "raise-normal");

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;

    /// <summary>常驻面板样式：工具窗口 + 不抢焦点，可选择鼠标穿透。不改 Z 序。</summary>
    public static void ApplyPanelStyle(Window w, bool clickThrough)
    {
        IntPtr h = Handle(w);
        if (!IsWin(h)) { Log.W("StyleFailed", "hwnd invalid"); return; }
        long ex = GetWindowLongPtr(h, GwlExStyle).ToInt64();
        long merged = ex | WsExNoActivate | WsExToolWindow;
        merged = clickThrough ? merged | WsExTransparent : merged & ~(long)WsExTransparent;
        SetWindowLongPtr(h, GwlExStyle, new IntPtr(merged));
        Log.W("StyleApplied", $"clickThrough={clickThrough} ex=0x{ExStyle(h):X}");
    }
}

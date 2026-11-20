namespace LiquidGlass.Desktop.Platform;

/// <summary>
/// 把别的应用窗口切到前台。
///
/// 这里有个容易被忽略的前提：Dock 窗口是 <c>WS_EX_NOACTIVATE</c>，点击它并不会
/// 让本进程变成前台进程。而 Windows 默认禁止后台进程调用 SetForegroundWindow
/// （否则任何程序都能随便抢焦点、弹窗骗点击）。
///
/// 所以激活必须走三级降级，每一级都是系统允许的合法路径，不做 hook、不注入：
/// 1. 直连 —— 刚收到鼠标点击的进程通常已获得"最后输入事件"授权，这一步就能成；
/// 2. 共享输入状态 —— 和当前前台窗口的线程 AttachThreadInput，这是被文档化的做法；
/// 3. SwitchToThisWindow —— Alt+Tab 走的同一条路径，前两步被拒时的最后退路。
/// </summary>
internal static class WindowActivator
{
    /// <summary>把窗口切到前台。返回是否真的切过去了（不靠返回值猜，读回前台窗口核对）。</summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;

        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        if (Native.GetForegroundWindow() == hwnd) return true;

        // 第 1 级：直连
        Native.SetForegroundWindow(hwnd);
        if (Native.GetForegroundWindow() == hwnd) return true;

        // 第 2 级：和当前前台线程共享输入状态
        IntPtr fg = Native.GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? Native.GetWindowThreadProcessId(fg, out _) : 0;
        uint selfThread = Native.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != selfThread &&
                        Native.AttachThreadInput(selfThread, fgThread, true);
        try
        {
            Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(selfThread, fgThread, false);
        }

        if (Native.GetForegroundWindow() == hwnd) return true;

        // 第 3 级：Alt+Tab 同款
        Native.SwitchToThisWindow(hwnd, true);
        return Native.GetForegroundWindow() == hwnd;
    }

    /// <summary>最小化（与 Windows 任务栏"再点一次收起来"的行为一致）。</summary>
    public static void Minimize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
        Native.ShowWindow(hwnd, Native.SW_MINIMIZE);
    }

    /// <summary>这个窗口此刻是否是前台窗口（按根窗口比较，UWP 的宿主子窗口也能认出来）。</summary>
    public static bool IsForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;

        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == hwnd) return true;

        return Native.GetAncestor(fg, Native.GA_ROOT) == Native.GetAncestor(hwnd, Native.GA_ROOT);
    }
}

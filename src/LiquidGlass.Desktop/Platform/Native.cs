using System.Runtime.InteropServices;
using System.Text;

namespace LiquidGlass.Desktop.Platform;

/// <summary>
/// 全部 Win32 互操作集中在这里。只使用公开文档化的 API，
/// 不触碰 explorer.exe、不替换 Shell、不需要管理员权限。
/// </summary>
internal static class Native
{
    // ---- 窗口样式 ----
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_HIDE = 0;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    // ---- 消息 ----
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_TIMER = 0x0113;

    // ---- 电源 ----
    //
    // 合盖 / 睡眠是这台机器上最容易被忽略的耗电源头：进程不会被挂起，
    // 只要有定时器在跑，每一拍都会把 GPU 和 CPU 从低功耗里叫醒一次，
    // 而 Modern Standby（现代待机）下这一叫就是整晚。
    // 所以睡眠时必须把渲染循环彻底停掉，只留一个不设超时的消息等待。
    public const uint WM_POWERBROADCAST = 0x0218;

    // 进程间「请打开某个面板」的自定义消息。
    // 为什么需要它：程序是单实例的，桌面上的「Dock 设置」快捷方式再启动一次
    // 只会撞上互斥锁然后静默退出，设置面板永远打不开。所以第二个进程
    // 不启动自己，而是把这条消息投给正在运行的那个实例，由它弹面板。
    public const uint WM_LIQUIDGLASS_OPEN_SETTINGS = 0x8000 + 42;
    public const uint WM_LIQUIDGLASS_OPEN_LAUNCHPAD = 0x8000 + 43;
    public const int PBT_APMSUSPEND = 0x0004;
    public const int PBT_APMRESUMESUSPEND = 0x0007;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;

    // ---- 登录会话 ----
    //
    // 「开机 / 唤醒后重回桌面，Dock 变成一块蓝灰色静止板子」的根因就在这条消息。
    //
    // 锁屏期间桌面在**安全桌面**上，DXGI Desktop Duplication 一律返回
    // E_ACCESSDENIED（0x80070005）；解锁后要等 DWM 把用户桌面重新合成出来才能采。
    // 过去没有订阅会话通知，只能靠 1.5 秒一次的退避重试去撞时机 ——
    // 撞上之前用户看到的就是降级色块，撞不上就一直蓝着。
    //
    // WTSRegisterSessionNotification 需要先注册才会收到 WM_WTSSESSION_CHANGE。
    public const uint WM_WTSSESSION_CHANGE = 0x02B1;
    public const int WTS_SESSION_LOCK = 0x7;
    public const int WTS_SESSION_UNLOCK = 0x8;
    public const int NOTIFY_FOR_THIS_SESSION = 0;
    /// <summary>RegisterSuspendResumeNotification 的 Flags：hRecipient 是窗口句柄。</summary>
    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    public const uint WM_APP = 0x8000;
    /// <summary>外部请求放弃任务栏托管（--restore-taskbar 用），收到后立即恢复并停止守护。</summary>
    public const uint WM_APP_RELEASE_TASKBAR = WM_APP + 10;
    /// <summary>外部请求在运行时切换任务栏显隐（--switch-taskbar 用），Dock 继续运行。</summary>
    public const uint WM_APP_TOGGLE_TASKBAR = WM_APP + 11;
    /// <summary>本进程内部请求显示/隐藏 Dock（全局快捷键 + 鼠标钩子都投这条）。</summary>
    public const uint WM_LIQUIDGLASS_TOGGLE_DOCK = WM_APP + 44;
    /// <summary>鼠标钩子把"录制到的鼠标键"投给设置面板（1=中键 2=侧键后退 3=侧键前进）。</summary>
    public const uint WM_LIQUIDGLASS_CAPTURE_MOUSE = WM_APP + 45;
    /// <summary>键盘钩子把"录制到的按键"投给设置面板（wParam=vk，lParam=修饰键位掩码）。</summary>
    public const uint WM_LIQUIDGLASS_CAPTURE_KEY = WM_APP + 46;
    /// <summary>录制期间左键点到面板外面 = 放弃录制（防止录制状态一直挂着）。</summary>
    public const uint WM_LIQUIDGLASS_CANCEL_CAPTURE = WM_APP + 47;
    public const uint WM_NCHITTEST = 0x0084;
    public const int HTTRANSPARENT = -1;

    // ---- DWM ----
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWCP_DONOTROUND = 1;
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    // ---- 采集排除 ----
    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// 窗口的还原位置。最小化窗口的 GetWindowRect 会报到任务栏图标那儿去（或者
    /// -32000 那种哨兵值），只有 rcNormalPosition 里还是它真正的尺寸 ——
    /// 卡片按窗口比例算高度时只能信这个。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- 会话 / 电源订阅 ----
    //
    // RegisterSuspendResumeNotification 必须在 Win8+ 上显式调一次，
    // 否则 PBT_APMSUSPEND / PBT_APMRESUMEAUTOMATIC 根本不会送到这个窗口 ——
    // 之前那段 WM_POWERBROADCAST 处理只在"系统恰好广播到所有顶层窗口"时才生效，
    // 属于碰运气。普通应用想稳定拿到挂起/唤醒通知，这是唯一的正路。
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterSuspendResumeNotification(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public const uint PM_REMOVE = 0x0001;
    public const uint QS_ALLINPUT = 0x04FF;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowDisplayAffinity(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    [DllImport("dwmapi.dll")]
    public static extern int DwmIsCompositionEnabled(out bool enabled);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    // ---- DWM 缩略图（悬停预览用）----
    // 这是 Windows 自带任务栏预览用的同一套机制：DWM 直接把目标窗口的**实时**画面
    // 合成到我们的宿主窗口上。相比 PrintWindow 抓一帧，它有两个决定性的好处：
    // 1) 硬件加速窗口（Chrome / Electron / 视频播放器）也能正常工作 —— PrintWindow
    //    对这些窗口拿到的是纯黑；
    // 2) 是实时的，不用每秒重抓一次。
    public const int DWM_TNP_RECTDESTINATION = 0x00000001;
    public const int DWM_TNP_RECTSOURCE = 0x00000002;
    public const int DWM_TNP_OPACITY = 0x00000004;
    public const int DWM_TNP_VISIBLE = 0x00000008;
    public const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumbId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUnregisterThumbnail(IntPtr thumbId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUpdateThumbnailProperties(IntPtr thumbId,
        ref DWM_THUMBNAIL_PROPERTIES props);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
        int width, int height);

    /// <summary>两个区域求并。用来把"圆角矩形"和"上半部的方角矩形"拼起来，
    /// 得到只有下面两个角是圆角的形状。</summary>
    [DllImport("gdi32.dll")]
    public static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    public const int RGN_OR = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? className, string? windowName);

    // ---- 任务栏接管 ----
    // 只做 ShowWindow(SW_HIDE)，绝不结束 explorer.exe、不替换 Shell、不改注册表。
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    public const int SW_SHOWNA = 8;

    // ---- 任务栏隐形 ----
    // 只隐藏窗口(SW_HIDE)挡不住 explorer 的自动隐藏逻辑：鼠标碰底边时它会把窗口
    // 重新显示出来。改成给窗口加 WS_EX_LAYERED + alpha=0 + WS_EX_TRANSPARENT，
    // 窗口仍在、AppBar 注册不变、点击穿透，但完全不可见。
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    // 单实例重复启动时给用户可见反馈。没有这个提示，双击第二次会"什么都没发生"。
    public const uint MB_OK = 0x0;
    public const uint MB_ICONINFORMATION = 0x40;
    public const uint MB_ICONWARNING = 0x30;
    public const uint MB_TOPMOST = 0x40000;
    public const uint MB_SETFOREGROUND = 0x10000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    // ---- 选文件对话框 ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_EXPLORER = 0x00080000;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    /// <summary>
    /// 弹「打开图片」对话框，返回选中的路径；用户取消返回 null。
    ///
    /// 缓冲区用 AllocHGlobal 自己管、传 IntPtr 而不是 StringBuilder：
    /// 这个结构体很老，用字符串字段回填时不同封送方式行为不一致（常见是拿到空串），
    /// 手写指针最省心。lpstrFilter 里分隔符是 \0，结尾要补两个 \0。
    /// </summary>
    public static string? PickImageFile(IntPtr owner, string title)
    {
        const int cap = 1024;
        IntPtr buf = Marshal.AllocHGlobal(cap * 2);
        try
        {
            Marshal.WriteInt16(buf, 0, 0);

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = owner,
                lpstrFilter = "图片 (*.png;*.jpg;*.bmp;*.ico)\0*.png;*.jpg;*.jpeg;*.bmp;*.ico\0所有文件 (*.*)\0*.*\0\0",
                nFilterIndex = 1,
                lpstrFile = buf,
                nMaxFile = cap,
                lpstrTitle = title,
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR,
            };

            if (!GetOpenFileNameW(ref ofn)) return null;

            string? picked = Marshal.PtrToStringUni(buf);
            return string.IsNullOrWhiteSpace(picked) ? null : picked;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateMutex(IntPtr attributes, bool initialOwner, string name);

    [DllImport("kernel32.dll")]
    public static extern uint GetLastError();

    [DllImport("kernel32.dll")]
    public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    public const int ERROR_ALREADY_EXISTS = 183;

    // ---- 运行中应用（任务栏替代） ----
    // 只用公开文档化 API：读窗口信息、发激活请求。不注入、不读写别的进程内存、
    // 不需要管理员权限。拿不到的（提权进程）就跳过，不硬闯。
    public const int GW_OWNER = 4;
    public const int DWMWA_CLOAKED = 14;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild,
        uint threadId, uint timeMs);

    // ---- 窗口形状（部分命中）----
    //
    // 为什么不用 WM_NCHITTEST + HTTRANSPARENT：按文档它只在**同一线程**的窗口
    // 之间向下转发（"the message will be sent to underlying windows in the same
    // thread until one of them returns a code that is not HTTRANSPARENT"）。
    // 我们下面全是别的进程的窗口（WorkBuddy、桌面），所以那个 -1 完全不起作用 ——
    // 用户点玻璃上沿压着的按钮，照样被我们吃掉。
    // SetWindowRgn 改的是窗口形状本身，命中测试在系统层面就按形状走，跨进程有效。
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    /// <summary>设置窗口形状。成功后 hrgn 归系统所有，不能再 DeleteObject。</summary>
    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hwnd, IntPtr hrgn, bool redraw);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hwnd, int cmd);

    // 预览宿主窗口只需要"什么都不画、但把 WM_PAINT 消化掉"
    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

    // ---------------------------------------------------------------- 输入法（IMM32）

    /// <summary>取窗口当前的输入上下文。返回 0 表示这个窗口没有关联 IME。</summary>
    [DllImport("imm32.dll")]
    public static extern IntPtr ImmGetContext(IntPtr hwnd);

    [DllImport("imm32.dll")]
    public static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr himc);

    /// <summary>lpBuf 传 0 时返回"要多少字节"，据此分配再取一次。</summary>
    [DllImport("imm32.dll")]
    public static extern int ImmGetCompositionStringW(IntPtr himc, int index, IntPtr lpBuf, int dwBufLen);

    public const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    /// <summary>只请求"等它退出"这一项权限，够用且不会因为权限过大被拒。</summary>
    public const uint SYNCHRONIZE = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    // ---- 右键菜单（原生 win32 弹出菜单）----
    // 不用自绘菜单：Dock 窗口本身只有一条窄带，自绘菜单要额外扩窗口、还要重写
    // 命中测试与关闭逻辑；原生菜单是系统画的、跟随深浅色主题、点外部自动关，
    // 这部分不值得自己造。
    public const uint WM_NULL = 0x0000;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_TOPALIGN = 0x0000;
    public const uint TPM_BOTTOMALIGN = 0x0020;
    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_DISABLED = 0x0002;
    public const uint MF_CHECKED = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? item);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y,
        IntPtr hwnd, IntPtr paramsPtr);

    // ---- 只读注册表（读任务栏固定项顺序，不写）----
    public static readonly IntPtr HKEY_CURRENT_USER = unchecked((IntPtr)0x80000001);
    public const int KEY_QUERY_VALUE = 0x0001;
    public const int REG_BINARY = 3;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegOpenKeyExW(IntPtr root, string subKey, int options, int samDesired,
        out IntPtr result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegQueryValueExW(IntPtr key, string valueName, IntPtr reserved,
        out int type, byte[]? data, ref int dataSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegCloseKey(IntPtr key);

    // ---- 定时器精度与消息等待 ----
    //
    // 默认情况下 Thread.Sleep(1) 实际会睡 15.6ms（系统定时器分辨率），
    // 动画要按 60Hz 排帧就会被这个量化卡成忽快忽慢，肉眼看到的就是"抖"。
    // timeBeginPeriod(1) 把分辨率提到 1ms，退出时必须还回去，否则会拖累整机功耗。
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint milliseconds);

    /// <summary>
    /// 等"有消息"或者"超时"。比 Thread.Sleep 好在有消息会立刻返回 ——
    /// 空闲期降帧时鼠标划过来能马上醒，不用等这一帧睡完。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint milliseconds,
        uint wakeMask, uint flags);

    // ---------------------------------------------------------------- 高精度定时器
    //
    // MsgWaitForMultipleObjectsEx 的毫秒超时精度实测差约 1.4ms（目标 16.67 实到 18.1，
    // 目标 33.3 实到 34.7，恒定的正偏差）。60Hz 下 1.4ms 已是 8% 的帧长，
    // 表现为"整体慢半拍"，靠它排不出均匀的帧。
    //
    // Win10 1803 起有 CREATE_WAITABLE_TIMER_HIGH_RESOLUTION：0.5ms 级精度，
    // 而且能直接塞进 MsgWaitForMultipleObjectsEx 一起等 —— 既精确又能及时处理消息。
    // 老的 CreateWaitableTimer 不带这个标志时精度反而更差，所以只认高精度这个。

    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    public const uint TIMER_ALL_ACCESS = 0x1F0003;
    public const uint INFINITE = 0xFFFFFFFF;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x00000102;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr arg, bool resume);

    // ---- 合成 Win 键（开始菜单）----
    public const byte VK_LWIN = 0x5B;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    // ---- 全局快捷键 ----
    // 键盘组合走 RegisterHotKey（系统帮我们匹配，窗口没焦点也能收到）；
    // 鼠标中键/侧键没有对应的注册接口，只能装一个低级鼠标钩子自己判断。
    public const int WM_HOTKEY = 0x0312;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int WH_MOUSE_LL = 14;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_MENU = 0x12;
    public const byte VK_SHIFT = 0x10;
    public const byte VK_RWIN = 0x5C;

    /// <summary>
    /// 中文输入法处于英文/拼音状态时，字母键的 WM_KEYDOWN 带的是这个伪虚拟键，
    /// 真实键值被输入法吞掉了。要从 lParam 的扫描码反查回来。
    /// </summary>
    public const byte VK_PROCESSKEY = 0xE5;
    public const uint MAPVK_VSC_TO_VK_EX = 1;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int vk);

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    // ---- 低级键盘钩子（录制快捷键用）----
    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExWKeyboard(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    /// <summary>
    /// 虚拟键 → 扫描码。组合键（Win+A 这种）必须带真实扫描码：
    /// 只给虚拟键、扫描码填 0 时，有些程序收得到、有些收不到，
    /// 而"快速设置"面板恰好属于后者。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern uint MapVirtualKeyW(uint code, uint mapType);

    // ---------------------------------------------------------------- 逐像素分层窗口
    // 设置面板 / 启动台要画圆角、外阴影和半透明卡片。普通窗口用 WM_PAINT 画，
    // 透明区域会被系统填成黑底；分层窗口把"整张位图 + 每像素 alpha"交给 DWM，
    // 圆角和阴影边缘才干净。代价是坐标、命中、重绘都要自己管。

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx, cy;
        public SIZE(int w, int h) { cx = w; cy = h; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;
    public const uint ULW_ALPHA = 0x00000002;
    public const uint TME_LEAVE = 0x00000002;

    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTNOWHERE = 0;

    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_MOUSEWHEEL = 0x020A;

    // ---- 输入法 ----
    // 中文不能靠 WM_CHAR：IME 把拼音吞进去以后，只有 WM_IME_COMPOSITION 上
    // 带 GCS_RESULTSTR 的那一次才有真正的汉字。窗口只要 EN_CHAR 不看 IME，
    // 中文输入法在搜索框里就完全没反应（输入法候选框出来了、字却进不去）。
    public const uint WM_IME_STARTCOMPOSITION = 0x010D;
    public const uint WM_IME_ENDCOMPOSITION = 0x010E;
    public const uint WM_IME_COMPOSITION = 0x010F;
    public const uint WM_IME_CHAR = 0x0286;
    public const int GCS_RESULTSTR = 0x0800;
    /// <summary>还在组合中的串（拼音本身）。只用来在输入框里做预览。</summary>
    public const int GCS_COMPSTR = 0x0008;
    public const int MA_ACTIVATE = 1;

    /// <summary>
    /// WM_MOUSEACTIVATE 返回它 = "照常收鼠标消息，但别把我激活"。
    /// 悬停预览卡片必须用这个：卡片一激活，用户正在用的窗口就丢焦点了。
    /// </summary>
    public const int MA_NOACTIVATE = 3;
    public const int WA_INACTIVE = 0;

    public const int IDC_ARROW = 32512;
    public const int IDC_HAND = 32649;
    /// <summary>"I" 形文本光标。搜索框上必须换成它，否则鼠标划过去看不出能输入。</summary>
    public const int IDC_IBEAM = 32513;
    public const byte VK_ESCAPE = 0x1B;
    public const byte VK_RETURN = 0x0D;
    public const byte VK_BACK = 0x08;
    public const byte VK_UP = 0x26;
    public const byte VK_DOWN = 0x28;
    public const byte VK_PRIOR = 0x21;
    public const byte VK_NEXT = 0x22;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size,
        IntPtr srcDc, ref POINT src, uint colorKey, ref BLENDFUNCTION blend, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("user32.dll")]
    public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);
}

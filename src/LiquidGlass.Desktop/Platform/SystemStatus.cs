using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace LiquidGlass.Desktop.Platform;

/// <summary>
/// 一次读取的托盘状态快照。全是值类型，读一次用一秒，不持引用。
/// </summary>
internal readonly struct StatusSnapshot
{
    /// <summary>这台机器有没有电池（台式机为 false，就不显示电量）。</summary>
    public readonly bool HasBattery;

    /// <summary>电量百分比，-1 = 系统没给。</summary>
    public readonly int Percent;

    /// <summary>正插着电。</summary>
    public readonly bool Charging;

    /// <summary>能上网。</summary>
    public readonly bool Online;

    /// <summary>当前主要走无线。</summary>
    public readonly bool Wifi;

    public StatusSnapshot(bool hasBattery, int percent, bool charging, bool online, bool wifi)
    {
        HasBattery = hasBattery;
        Percent = percent;
        Charging = charging;
        Online = online;
        Wifi = wifi;
    }

    public static bool operator ==(StatusSnapshot a, StatusSnapshot b)
        => a.HasBattery == b.HasBattery && a.Percent == b.Percent && a.Charging == b.Charging &&
           a.Online == b.Online && a.Wifi == b.Wifi;

    public static bool operator !=(StatusSnapshot a, StatusSnapshot b) => !(a == b);

    public override bool Equals(object? obj) => obj is StatusSnapshot s && s == this;

    public override int GetHashCode() => HashCode.Combine(HasBattery, Percent, Charging, Online, Wifi);

    public override string ToString()
        => $"电池={(HasBattery ? Percent + "%" : "无")} 充电={Charging} 在线={Online} 无线={Wifi}";
}

/// <summary>
/// 托盘状态的来源。任务栏整条被藏掉之后，电量/网络这些信息就没地方看了，
/// 这里把它们从系统 API 里直接读回来，喂给 Dock 右端的状态胶囊。
///
/// 为什么不用 WMI / WLAN API 拿 Wi-Fi 名称：那两个第一次调用要几百毫秒，
/// 还会拉起 COM 和 WlanSvc，为了显示一个 SSID 不值当。这里只要"通/不通/有线/无线"，
/// <see cref="NetworkInterface"/> 就够，而且有缓存。
///
/// 所有读取都带缓存：GetSystemPowerStatus 很快但没必要每帧调，
/// NetworkInterface 枚举在网卡多的机器上能到几十毫秒，绝不能放进渲染循环。
/// </summary>
internal static class SystemStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 = 断电, 1 = 交流电, 255 = 未知
        public byte BatteryFlag;         // bit0 高, bit1 低, bit2 充电中, bit3 无电池, bit7 未知
        public byte BatteryLifePercent;  // 0..100, 255 = 未知
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    // BatteryFlag 的位定义（winbase.h）：
    //   0x01 电量高   0x02 电量低   0x04 电量危急   0x08 **正在充电**
    //   0x80 **没有电池**                            0xFF 未知
    //
    // 这里原来把 Charging 写成了 0x04、NoBattery 写成 0x08 —— 正好错位两格。
    // 后果不是"偶尔不准"，而是**一插上电源电量就整块消失**：
    // 充电时 BatteryFlag 带 0x08（充电位），旧代码把它当成"没有电池"，
    // 于是 HasBattery=false，胶囊按台式机布局收窄，电池图标和百分比一起消失。
    // 日志里的证据很干净：23:26:07「电池=22% 充电=False」，插电之后每一行都变成「电池=无」。
    private const byte FlagHigh = 0x01;
    private const byte FlagLow = 0x02;
    private const byte FlagCritical = 0x04;
    private const byte FlagCharging = 0x08;
    private const byte FlagNoBattery = 0x80;

    /// <summary>电源状态最多 1 秒读一次。插拔电源到画面更新，一秒的延迟足够。</summary>
    private const long PowerCacheMs = 1000;

    /// <summary>网卡枚举最贵，5 秒一次。断网/联网的感知延迟 5 秒可以接受。</summary>
    private const long NetCacheMs = 5000;

    private static readonly object Gate = new();

    private static StatusSnapshot _snapshot;

    // 初值必须是 0 而不是 long.MinValue：
    // TickCount64（一个正值）减去 long.MinValue 会整数溢出回绕成负数，
    // "now - _netAt >= 5000" 永远不成立，读取一次都不会发生。
    // 这个坑的表现极其安静：不报错、不抛异常，只是状态永远停在初始值上。
    private static long _powerAt;
    private static long _netAt;

    private static bool _hasBattery;
    private static int _percent = -1;
    private static bool _charging;

    private static bool _online;
    private static bool _wifi;

    /// <summary>网卡明细只打一次日志。</summary>
    private static bool _netDumped;

    /// <summary>上一次记录过的在线状态，null = 还没记过。</summary>
    private static bool? _lastOnlineLog;

    /// <summary>当前状态。内部按各自的缓存周期刷新对应字段。</summary>
    public static StatusSnapshot Current()
    {
        lock (Gate)
        {
            long now = Environment.TickCount64;

            if (now - _powerAt >= PowerCacheMs)
            {
                _powerAt = now;
                ReadPower();
            }

            if (now - _netAt >= NetCacheMs)
            {
                _netAt = now;
                ReadNetwork();
            }

            return _snapshot;
        }
    }

    /// <summary>强制下次读取都走真调用。插拔电源/网线后想立刻看到时用。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _powerAt = 0;
            _netAt = 0;
        }
    }

    private static void ReadPower()
    {
        if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS s))
        {
            // 调用失败就当作"未知"，不要编一个 100% 出来
            _hasBattery = false;
            _percent = -1;
            _charging = false;
            _snapshot = new StatusSnapshot(false, -1, false, _online, _wifi);
            return;
        }

        // 255 = 系统不知道有没有电池；带 0x80 = 明确报告没有电池。其余都算有。
        // 只认 0x80，**不能**把 0x08（充电中）也当成"没电池"。
        _hasBattery = s.BatteryFlag != 255 && (s.BatteryFlag & FlagNoBattery) == 0;
        _percent = s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent;
        // 充电中，或者接着交流电但没报充电标志（满电时就会这样），都算"有电"
        _charging = (s.BatteryFlag & FlagCharging) != 0 || s.ACLineStatus == 1;

        // 有些机型的驱动会在"电池在位但百分比暂时读不到"时报 255。
        // 这时 HasBattery 仍然是 true（上面只按 0x80 判），胶囊会画一个空电池壳 ——
        // 比整块消失好：用户至少知道电池还在，只是这一秒读不到数。

        _snapshot = new StatusSnapshot(_hasBattery, _percent, _charging, _online, _wifi);
    }

    /// <summary>
    /// 网络位置感知（NLM）的 COM 对象。Windows 任务栏那颗地球就是按它的
    /// <c>IsConnectedToInternet</c> 画的 —— 背后是 NCSI 的主动探测，能区分
    /// "网卡拿到 IP" 和 "真的能连外网"。
    ///
    /// 这正是之前断网还画 Wi-Fi 弧的根因：旧判据只看「有没有非 169.254 的 IPv4」，
    /// 而连着认证掉线的校园网 / 没有外网的热点 / Clash 的 TUN 时，IP 照样在，
    /// 判据为真，图标就一直是在线。IP 只说明"局域网通了"，不说明"能上网"。
    /// </summary>
    private static readonly Guid ClsidNetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    private static object? _nlm;

    /// <summary>NLM 说能不能上网；COM 不可用时返回 null，由调用方退回旧判据。</summary>
    private static bool? ReadNlmOnline()
    {
        try
        {
            if (_nlm is null)
            {
                Type? type = Type.GetTypeFromCLSID(ClsidNetworkListManager);
                if (type is null) return null;
                _nlm = Activator.CreateInstance(type);
            }

            if (_nlm is null) return null;
            return ((dynamic)_nlm).IsConnectedToInternet;
        }
        catch
        {
            // COM 挂了就别一直拽着：下次重新创建，失败就退回旧判据
            _nlm = null;
            return null;
        }
    }

    private static void ReadNetwork()
    {
        _online = false;
        _wifi = false;
        bool interfaceOnline = false;

        try
        {
            NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();

            // 第一次读的时候把每张网卡的真实情况记一遍。
            // "托盘一直显示断网"这类问题，光看结果分不出是系统没网还是判定写错了，
            // 有这张表就能一眼看出哪张卡被筛掉、被什么条件筛掉的。
            if (!_netDumped)
            {
                _netDumped = true;
                foreach (NetworkInterface ni in all)
                {
                    string ips;
                    try
                    {
                        var list = new List<string>();
                        foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                            list.Add(ua.Address.ToString());
                        ips = list.Count > 0 ? string.Join(",", list) : "无";
                    }
                    catch (Exception ex) { ips = "读取失败:" + ex.GetType().Name; }

                    LiquidGlass.Desktop.App.Logger.Info(
                        $"[net] {ni.Name} type={ni.NetworkInterfaceType} status={ni.OperationalStatus} " +
                        $"ip={ips} virtual={IsVirtual(ni)} usable={HoldsUsableAddress(ni)}");
                }
            }

            foreach (NetworkInterface ni in all)
            {
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                if (IsVirtual(ni)) continue;

                // 关键：**不能只看 OperationalStatus**。
                // 这台机器的 WLAN 网卡明明连着，.NET 却把它报成 Down/Unknown，
                // 结果托盘里永远是一个"断网"红叉。真正可靠的判据是"有没有一个能用的 IP"：
                // 拿到非 169.254 的 IPv4，就说明这张网卡在正常工作。
                if (!HoldsUsableAddress(ni)) continue;

                interfaceOnline = true;
                if (IsWireless(ni)) _wifi = true;
            }
        }
        catch
        {
            // 枚举失败就保持"没网"，绝不让异常冒到渲染循环里
        }

        // 能不能上网以 NLM 为准；只有它不可用（COM 缺失 / 远程会话）才退回"有 IP 就算在线"。
        bool? nlmOnline = ReadNlmOnline();
        _online = nlmOnline ?? interfaceOnline;
        if (!_online) _wifi = false;

        // 只在翻转时记一行：这两个判据以后要是又不一致，先看这行就知道是谁说的。
        if (_lastOnlineLog != _online)
        {
            _lastOnlineLog = _online;
            LiquidGlass.Desktop.App.Logger.Info(
                $"[net] 联网状态: {(_online ? "在线" : "离线")}" +
                $"（NLM={(nlmOnline.HasValue ? (nlmOnline.Value ? "在线" : "离线") : "不可用")} " +
                $"网卡IP={(interfaceOnline ? "有" : "无")} 无线={_wifi}）");
        }

        _snapshot = new StatusSnapshot(_hasBattery, _percent, _charging, _online, _wifi);
    }

    /// <summary>虚拟网卡（VPN、Hyper-V、VMware、WSL）不算"上网"。</summary>
    private static bool IsVirtual(NetworkInterface ni)
    {
        string n = ni.Name.ToLowerInvariant();
        string d = (ni.Description ?? "").ToLowerInvariant();

        return n.Contains("vethernet") || n.Contains("virtual") || n.Contains("wsl") ||
               n.Contains("vmware") || n.Contains("loopback") || n.Contains("tap") ||
               d.Contains("virtual") || d.Contains("vmware") || d.Contains("hyper-v") ||
               d.Contains("tap-");
    }

    /// <summary>
    /// 这张网卡有没有一个能用的 IPv4。169.254.x.x 是 Windows 自己编的"拿不到 DHCP"地址，
    /// 有它等于没网，必须排掉。
    /// </summary>
    private static bool HoldsUsableAddress(NetworkInterface ni)
    {
        try
        {
            foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.Address.ToString().StartsWith("169.254.")) continue;
                return true;
            }
        }
        catch
        {
            // 网卡正在被禁用 / 驱动重置时会抛，当作没有
        }

        return false;
    }

    /// <summary>
    /// 是不是无线。NetworkInterfaceType 在部分驱动上同样会报 Unknown，
    /// 所以再按名字和描述兜一层。
    /// </summary>
    private static bool IsWireless(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return true;

        string text = (ni.Name + " " + (ni.Description ?? "")).ToLowerInvariant();
        return text.Contains("wlan") || text.Contains("wi-fi") || text.Contains("wifi") ||
               text.Contains("wireless") || text.Contains("802.11");
    }
}

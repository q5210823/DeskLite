using System.Diagnostics;

namespace DeskLite.Core;

/// <summary>
/// 与原版腾讯桌面整理（DeskGo）的冲突探测。
///
/// 背景（经实测确认）：
///   DeskGo 的 DesktopMgr64.exe 会创建一个名为 TXMiniSkin 的 WS_POPUP 顶层窗口，
///   尺寸等于整个虚拟桌面，在其上重绘「格子 + 图标」。
///   该窗口把 explorer.exe 的原生桌面图标层（Progman→SHELLDLL_DefView→SysListView32）
///   完整盖住，因此两者同时运行时：
///     · DeskLite 隐藏原生 ListView 的接管动作「视觉上无效」（被覆盖层挡着）
///     · 两套格子会叠在一起，体验混乱
///
/// 结论：DeskLite 应在 DeskGo 未运行时工作；若检测到 DeskGo 在跑，进入「共存预览」模式并提示用户。
/// </summary>
public static class Conflict
{
    /// <summary>DeskGo 主程序与其服务进程名</summary>
    private static readonly string[] DeskGoProcesses =
    {
        "DesktopMgr64", "DesktopMgr", "DesktopService64", "DesktopService",
    };

    /// <summary>DeskGo 覆盖层窗口类名（实测）</summary>
    public const string OverlayClass = "TXMiniSkin";

    /// <summary>返回正在运行的 DeskGo 进程（进程名 + PID）；未运行则返回空列表。</summary>
    public static List<(string Name, int Pid)> RunningDeskGo()
    {
        var found = new List<(string, int)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (Array.Exists(DeskGoProcesses,
                        n => string.Equals(n, p.ProcessName, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add((p.ProcessName, p.Id));
                }
            }
            catch { /* 有的系统进程不允许访问，忽略 */ }
            finally { p.Dispose(); }
        }
        return found;
    }

    /// <summary>
    /// 是否存在覆盖整个桌面的 TXMiniSkin 窗口（更强的证据：覆盖层真的在画桌面）。
    /// 注意：实测在本机 Windows 11 25H2 上 EnumWindows **拿不到** TXMiniSkin
    /// （原因未明，疑与其窗口归属有关），因此这里必须带回退路径：
    ///   快路径 → EnumWindows 直接找；
    ///   慢路径 → 取桌面图标坐标反向 WindowFromPoint，看命中的是谁（已证实 100% 可靠）。
    /// </summary>
    public static bool OverlayPresent(out IntPtr hwnd, out int pid)
    {
        if (TryEnum(out hwnd, out pid)) return true;
        return TryProbe(out hwnd, out pid);
    }

    /// <summary>快路径：直接枚举顶层窗口找 TXMiniSkin</summary>
    private static bool TryEnum(out IntPtr hwnd, out int pid)
    {
        hwnd = IntPtr.Zero;
        pid = 0;

        IntPtr found = IntPtr.Zero;
        int foundPid = 0;

        Win32.Native.EnumWindows((h, _) =>
        {
            if (ClassOf(h) != OverlayClass) return true;
            if (!Win32.Native.IsWindowVisible(h)) return true;

            Win32.Native.GetWindowRect(h, out var r);
            if (r.Right - r.Left < 800 || r.Bottom - r.Top < 600) return true;  // 只要覆盖大半个桌面的

            Win32.Native.GetWindowThreadProcessId(h, out uint p);
            found = h;
            foundPid = (int)p;
            return false;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero)
        {
            hwnd = found;
            pid = foundPid;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 慢路径（可靠）：枚举桌面图标坐标 → WindowFromPoint 反查实际绘制者。
    /// 若大量图标像素落在同一个非 SysListView32 窗口上，该窗口即桌面覆盖层。
    /// </summary>
    private static bool TryProbe(out IntPtr hwnd, out int pid)
    {
        hwnd = IntPtr.Zero;
        pid = 0;

        Win32.DesktopHost host;
        try
        {
            host = new Win32.DesktopHost();
            if (!host.Locate()) return false;
        }
        catch { return false; }

        Win32.Native.GetWindowRect(host.ListView, out var lv);

        var hits = new Dictionary<IntPtr, int>();
        foreach (var ic in host.EnumerateIcons(24))
        {
            var pt = new Win32.Native.POINT(lv.Left + ic.X + 24, lv.Top + ic.Y + 24);
            IntPtr w = Win32.Native.WindowFromPoint(pt);
            if (w == IntPtr.Zero || w == host.ListView) continue;

            string cls = ClassOf(w);
            if (cls is "SysListView32" or "SHELLDLL_DefView" or "Progman") continue;
            if (cls != OverlayClass) continue;

            hits[w] = hits.GetValueOrDefault(w) + 1;
        }

        if (hits.Count == 0) return false;

        var best = hits.OrderByDescending(k => k.Value).First();
        hwnd = best.Key;
        Win32.Native.GetWindowThreadProcessId(hwnd, out uint p);
        pid = (int)p;
        return true;
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(256);
        Win32.Native.GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskLite.Win32;

/// <summary>桌面上的一个图标项（来自 explorer 的 SysListView32）</summary>
public sealed class DesktopIcon
{
    public int Index { get; set; }
    public string Text { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>
    /// 该项在**系统图标列表**中的索引（LVM_GETITEMW + LVIF_IMAGE 读回）。
    ///
    /// 这个索引与 <c>SHGetFileInfo(SHGFI_SYSICONINDEX)</c> 返回的是同一个东西，
    /// 因此可以直接喂给 <c>SHGetImageList(SHIL_JUMBO / SHIL_EXTRALARGE)</c> 拿到大尺寸带 alpha 的图标。
    /// 虚拟项（此电脑 / 回收站）没有文件路径，只能靠这条索引取图标。取不到时为 -1。
    /// </summary>
    public int ImageIndex { get; set; } = -1;
}

/// <summary>
/// 桌面宿主 —— 定位并操作真实的桌面窗口链：
///   Progman → SHELLDLL_DefView → SysListView32 ("FolderView")
/// SysListView32 属于 explorer.exe，需跨进程读取其内存才能枚举图标。
/// </summary>
public sealed class DesktopHost
{
    public IntPtr Progman { get; private set; }
    public IntPtr DefView { get; private set; }
    public IntPtr ListView { get; private set; }
    public uint ExplorerPid { get; private set; }

    /// <summary>窗口链是否定位成功</summary>
    public bool Located => ListView != IntPtr.Zero;

    /// <summary>定位桌面窗口链。Progman 下找不到时回退到枚举 WorkerW。</summary>
    public bool Locate()
    {
        Progman = Native.FindWindow("Progman", null);

        // 常规路径：Progman 下的 SHELLDLL_DefView
        if (Progman != IntPtr.Zero)
            DefView = Native.FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null);

        // 回退路径：某些配置下 DefView 挂在 WorkerW 下
        if (DefView == IntPtr.Zero)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((h, _) =>
            {
                var sb = new StringBuilder(256);
                Native.GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() == "WorkerW")
                {
                    var dv = Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (dv != IntPtr.Zero) { found = dv; return false; }
                }
                return true;
            }, IntPtr.Zero);
            DefView = found;
        }

        if (DefView == IntPtr.Zero) return false;

        ListView = Native.FindWindowEx(DefView, IntPtr.Zero, "SysListView32", null);
        if (ListView == IntPtr.Zero)
            ListView = Native.FindWindowEx(DefView, IntPtr.Zero, "SysListView32", "FolderView");

        if (ListView == IntPtr.Zero) return false;

        Native.GetWindowThreadProcessId(ListView, out uint pid);
        ExplorerPid = pid;
        return true;
    }

    public int ItemCount()
        => Located ? (int)Native.SendMessage(ListView, Native.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero) : 0;

    public string ExplorerProcessName()
    {
        try { return Process.GetProcessById((int)ExplorerPid).ProcessName; }
        catch { return "(未知)"; }
    }

    /// <summary>
    /// 枚举桌面图标（文本 + 位置 + 可选图标索引）。跨进程：在 explorer 中分配内存 → 发消息 → 读回。
    ///
    /// ⚠ 这里**每一项要几个来回的 SendMessage**，而 SendMessage 是同步的、会阻塞调用线程 ——
    /// 所以「只取真正用得到的字段」是实打实的优化：
    ///   · <paramref name="needLayout"/> 读 X/Y。只有冲突检测（要在图标位置附近找覆盖层窗口）
    ///     与诊断需要；扫描归类只要显示名，让它传 false 可省掉每项一个来回。
    ///   · <paramref name="needImageIndex"/> 读 <c>LVIF_IMAGE</c>。全项目只有诊断里那组
    ///     「ListView 索引 vs SHGetFileInfo 索引」对照要用，**生产路径一次都不用**
    ///     （而且实测两者压根不是同一张表，不能混用 —— 见 IconCache 的说明）。
    ///     它是三项里最贵的一步：写回 LVITEM + 发消息 + 读回整个结构体。
    /// </summary>
    public List<DesktopIcon> EnumerateIcons(int max = int.MaxValue,
        bool needLayout = true, bool needImageIndex = false)
    {
        var result = new List<DesktopIcon>();
        if (!Located) return result;

        int count = ItemCount();
        if (count <= 0) return result;

        var hProc = Native.OpenProcess(
            Native.PROCESS_VM_OPERATION | Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE |
            Native.PROCESS_QUERY_INFORMATION, false, ExplorerPid);
        if (hProc == IntPtr.Zero) return result;

        const int textBuf = 520;                 // 260 个 WCHAR
        var lvItemSize = Marshal.SizeOf<Native.LVITEM>();
        var remoteLvItem = IntPtr.Zero;
        var remoteText = IntPtr.Zero;
        var remotePoint = IntPtr.Zero;
        var hStage = IntPtr.Zero;                // 本地暂存：结构与字节互转，避免每项都 AllocHGlobal

        try
        {
            remoteLvItem = Native.VirtualAllocEx(hProc, IntPtr.Zero, (IntPtr)lvItemSize,
                Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            remoteText = Native.VirtualAllocEx(hProc, IntPtr.Zero, (IntPtr)textBuf,
                Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (needLayout)
                remotePoint = Native.VirtualAllocEx(hProc, IntPtr.Zero, (IntPtr)8,
                    Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);

            if (remoteLvItem == IntPtr.Zero || remoteText == IntPtr.Zero
                || (needLayout && remotePoint == IntPtr.Zero))
                return result;

            // 全部缓冲都在循环外分配一次 —— 旧实现在循环里反复 AllocHGlobal/FreeHGlobal
            hStage = Marshal.AllocHGlobal(lvItemSize);
            var itemBytes = new byte[lvItemSize];
            var backBytes = new byte[lvItemSize];
            var textBytes = new byte[textBuf];
            var ptBytes = new byte[8];

            /// 把 LVITEM 写进 explorer 的 remoteLvItem 并发一条消息
            void PushItem(int msg, int index, Native.LVITEM item)
            {
                item.iItem = index;
                Marshal.StructureToPtr(item, hStage, false);
                Marshal.Copy(hStage, itemBytes, 0, lvItemSize);
                Native.WriteProcessMemory(hProc, remoteLvItem, itemBytes, (IntPtr)lvItemSize, out _);
                Native.SendMessage(ListView, msg, (IntPtr)index, remoteLvItem);
            }

            int limit = Math.Min(count, max);
            for (int i = 0; i < limit; i++)
            {
                // ── 读文本 ──
                PushItem(Native.LVM_GETITEMTEXTW, i, new Native.LVITEM
                {
                    mask = Native.LVIF_TEXT,
                    iSubItem = 0,
                    pszText = remoteText,
                    cchTextMax = textBuf / 2,
                });

                Array.Clear(textBytes);
                Native.ReadProcessMemory(hProc, remoteText, textBytes, (IntPtr)textBuf, out _);
                var textLen = 0;
                while (textLen < textBytes.Length - 1 && !(textBytes[textLen] == 0 && textBytes[textLen + 1] == 0))
                    textLen += 2;
                var text = Encoding.Unicode.GetString(textBytes, 0, textLen);

                // ── 读位置（可选）──
                int px = 0, py = 0;
                if (needLayout)
                {
                    Native.SendMessage(ListView, Native.LVM_GETITEMPOSITION, (IntPtr)i, remotePoint);
                    Native.ReadProcessMemory(hProc, remotePoint, ptBytes, (IntPtr)8, out _);
                    px = BitConverter.ToInt32(ptBytes, 0);
                    py = BitConverter.ToInt32(ptBytes, 4);
                }

                // ── 读图标索引（可选，且只有诊断要用）──
                int imgIndex = -1;
                if (needImageIndex)
                {
                    try
                    {
                        PushItem(Native.LVM_GETITEMW, i, new Native.LVITEM
                        {
                            mask = Native.LVIF_IMAGE,
                            iSubItem = 0,
                        });

                        Native.ReadProcessMemory(hProc, remoteLvItem, backBytes, (IntPtr)lvItemSize, out _);
                        Marshal.Copy(backBytes, 0, hStage, lvItemSize);
                        imgIndex = Marshal.PtrToStructure<Native.LVITEM>(hStage).iImage;
                    }
                    catch { /* 单张图标索引读不到不影响文本与位置 */ }
                }

                result.Add(new DesktopIcon
                {
                    Index = i, Text = text, X = px, Y = py, ImageIndex = imgIndex,
                });
            }
        }
        catch
        {
            // 跨进程操作失败时返回已取到的部分
        }
        finally
        {
            if (hStage != IntPtr.Zero) Marshal.FreeHGlobal(hStage);
            if (remoteLvItem != IntPtr.Zero) Native.VirtualFreeEx(hProc, remoteLvItem, IntPtr.Zero, Native.MEM_RELEASE);
            if (remoteText != IntPtr.Zero) Native.VirtualFreeEx(hProc, remoteText, IntPtr.Zero, Native.MEM_RELEASE);
            if (remotePoint != IntPtr.Zero) Native.VirtualFreeEx(hProc, remotePoint, IntPtr.Zero, Native.MEM_RELEASE);
            Native.CloseHandle(hProc);
        }

        return result;
    }

    /// <summary>是否已由本程序切换为隐藏</summary>
    private bool _iconsHidden;

    /// <summary>
    /// 隐藏原生桌面图标（接管模式开启）。
    /// 实测：ShowWindow(ListView, SW_HIDE) 在 Win11 26200 上有效，
    /// 但 explorer 可能异步恢复，因此配合 EnsureIconsHidden() 做保活。
    ///
    /// 隐藏成功后立刻落盘一个标记（<see cref="Core.CrashGuard.MarkDesktopHidden"/>）：
    /// 万一进程之后崩溃，下次启动就能靠这个标记把桌面无条件救回来 ——
    /// 这是「闪退后桌面图标全消失」这一问题的兜底防线。
    /// </summary>
    public void HideNativeIcons(bool force = false)
    {
        if (!force && _iconsHidden) return;
        if (ListView == IntPtr.Zero) return;

        Native.ShowWindow(ListView, Native.SW_HIDE);
        _iconsHidden = true;
        Core.CrashGuard.MarkDesktopHidden();
    }

    /// <summary>恢复原生桌面图标（接管模式关闭 / 程序退出）</summary>
    public void ShowNativeIcons()
    {
        if (ListView == IntPtr.Zero) return;

        Native.ShowWindow(ListView, Native.SW_SHOW);
        _iconsHidden = false;
        Core.CrashGuard.MarkDesktopShown();
    }

    /// <summary>
    /// 保活：若处于接管状态但 ListView 又变得可见（explorer 异步恢复），
    /// 立即重新隐藏。返回 true 表示本次执行了恢复动作。
    /// </summary>
    public bool EnsureIconsHidden()
    {
        if (!_iconsHidden) return false;

        // explorer 重启后旧句柄会失效 —— 必须先重新定位，否则保活会静默失效
        // （表现为「接管偶尔自己掉了」，且旧的 Progman 句柄也会让嵌入的格子失联）。
        if (!IsAlive(ListView))
        {
            Locate();
            if (ListView == IntPtr.Zero) return false;
            Native.ShowWindow(ListView, Native.SW_HIDE);
            return true;
        }

        if (!Native.IsWindowVisible(ListView)) return false;

        Native.ShowWindow(ListView, Native.SW_HIDE);
        return true;
    }

    /// <summary>窗口句柄是否仍然有效（explorer 重启 / 桌面重建后旧句柄会作废）</summary>
    private static bool IsAlive(IntPtr h)
        => h != IntPtr.Zero && Native.IsWindow(h);

    /// <summary>
    /// 若桌面窗口链已失效则重新定位；返回当前是否可用。
    /// 供保活巡检与「一键整理」等入口复用，用来兜住 explorer 重启这种常见场景。
    /// </summary>
    public bool EnsureLocated()
    {
        if (IsAlive(ListView) && IsAlive(Progman)) return true;
        return Locate();
    }

    /// <summary>当前原生图标是否可见（诊断用）</summary>
    public bool NativeIconsVisible => ListView != IntPtr.Zero && Native.IsWindowVisible(ListView);
}

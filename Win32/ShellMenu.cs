using System.Runtime.InteropServices;

namespace DeskLite.Win32;

/// <summary>
/// 系统**原生右键菜单**（IContextMenu 宿主）。
///
/// ── 为什么要自己宿一个原生菜单 ──
///
/// 格子里的图标是自绘的，它们并不是 explorer 桌面的 ListView 项，
/// 因此右键时系统不会自动弹菜单 —— 必须由我们向 Shell 要一份。
/// 做法与 explorer 桌面完全一致：
///
///   1. 路径 → PIDL（<see cref="SHParseDisplayName"/>），虚拟项走 <c>::{CLSID}</c> 解析名；
///   2. PIDL → <see cref="IContextMenu"/>：
///        · 单项：<c>SHBindToParent</c> 取父目录的 <see cref="IShellFolder"/>，
///          再 <c>GetUIObjectOf</c>（explorer 里每一项都是这么拿的）；
///        · 多选：<c>SHCreateShellItemArrayFromIDLists</c> → <c>BindToHandler(BHID_SFUIObject)</c>，
///          这样「不同目录 + 虚拟项」混选也能得到正确的合并菜单（含"发送到"等复合动词）。
///   3. <c>QueryContextMenu</c> 把项填进一个空 HMENU；
///   4. <c>TrackPopupMenuEx</c> 弹出，拿到命令 id；
///   5. <c>InvokeCommand</c> 执行。
///
/// ── 两个必须处理的坑 ──
///
/// <b>其一：菜单消息转发。</b>「发送到」「打开方式」这类子菜单、以及装了 shell 扩展的项
/// （压缩软件、版本控制…）是**自绘**的，它们靠 <see cref="IContextMenu2"/>/<see cref="IContextMenu3"/>
/// 接收 <c>WM_INITMENUPOPUP / WM_DRAWITEM / WM_MEASUREITEM / WM_MENUCHAR</c> 才画得出来。
/// <c>TrackPopupMenuEx</c> 内部是它自己的模态循环，WinForms 的 <c>Application.AddMessageFilter</c>
/// 根本收不到这些消息 —— 必须挂 <c>WH_MSGFILTER</c> 线程钩子（菜单循环会主动调它）。
/// 少了这一步，菜单能弹出来但子菜单一片空白。
///
/// <b>其二：不占 UI 线程之外的东西。</b><c>IContextMenu</c> 是 STA 对象，只能在创建它的线程上调用；
/// 想「丢到后台线程去 Invoke 免得卡 UI」是行不通的（跨套间调用还是会被封送到 UI 线程），
/// 所以这里就地同步调用，外面用 try/catch 兜住扩展抛出的异常。
/// </summary>
internal static class ShellMenu
{
    // ───────── 调用方约定 ─────────

    /// <summary>自定义项的命令 id 起点（原生项用 1~0x7FFF，两边不重叠）</summary>
    internal const int ExtraIdBase = 0x8000;

    /// <summary>追加到原生菜单末尾的一项</summary>
    internal readonly struct Extra
    {
        internal readonly string Text;
        internal readonly int Id;
        internal readonly bool Enabled;
        internal readonly bool Separator;

        private Extra(string text, int id, bool enabled, bool separator)
        {
            Text = text; Id = id; Enabled = enabled; Separator = separator;
        }

        /// <summary>分隔线</summary>
        internal static Extra Sep() => new("", 0, false, true);

        /// <summary>普通项。<paramref name="enabled"/>=false 时置灰（而不是隐藏），让菜单位置保持稳定。</summary>
        internal static Extra Item(string text, int id, bool enabled = true)
            => new(text, id, enabled, false);
    }

    /// <summary>
    /// 弹出系统原生右键菜单。
    /// </summary>
    /// <param name="owner">宿主窗口（菜单 owner，也作为 shell 动词的父窗口）</param>
    /// <param name="screen">弹出点（屏幕坐标）</param>
    /// <param name="paths">选中项的路径：真实路径，或虚拟项伪路径 <c>::{CLSID}</c></param>
    /// <param name="extras">追加到菜单末尾的自定义项（可为 null）</param>
    /// <param name="extendedVerbs">true = Shift+右键，显示扩展动词（CMF_EXTENDEDVERBS）</param>
    /// <param name="chosenExtra">用户点中的自定义项 id（没点中自定义项则为 0）</param>
    /// <returns>
    /// true  = 原生菜单已弹出（无论最终选中命令还是按 Esc 取消）；
    /// false = 压根没能建立原生菜单（Shell 拒绝、路径全部无法解析…），调用方应回退到自带菜单。
    /// </returns>
    internal static bool TryShow(IntPtr owner, Point screen, IReadOnlyList<string> paths,
                                IReadOnlyList<Extra>? extras, bool extendedVerbs,
                                out int chosenExtra)
    {
        chosenExtra = 0;
        if (paths.Count == 0) return false;

        var pidls = new List<IntPtr>(paths.Count);
        object? cmObj = null;
        IntPtr hMenu = IntPtr.Zero;

        try
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                    pidls.Add(pidl);
            }
            if (pidls.Count == 0) return false;

            cmObj = BindContextMenu(owner, pidls);
            if (cmObj is not IContextMenu cm) return false;

            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return false;

            uint flags = (extendedVerbs ? CMF_EXTENDEDVERBS : 0u) | CMF_NORMAL;
            if (cm.QueryContextMenu(hMenu, 0, IdCmdFirst, 0x7FFF, flags) < 0) return false;

            AppendExtras(hMenu, extras);

            SetForegroundWindow(owner);

            _cm2 = cm as IContextMenu2;
            _cm3 = cm as IContextMenu3;
            _hook = SetWindowsHookEx(WH_MSGFILTER, _hookProc, IntPtr.Zero, GetCurrentThreadId());

            int cmd;
            try
            {
                cmd = TrackPopupMenuEx(hMenu,
                    TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LEFTALIGN | TPM_TOPALIGN,
                    screen.X, screen.Y, owner, IntPtr.Zero);
            }
            finally
            {
                if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
                _cm2 = null;
                _cm3 = null;
            }

            if (cmd >= ExtraIdBase) chosenExtra = cmd;
            else if (cmd > 0) Invoke(cm, owner, cmd, screen);

            return true;
        }
        catch
        {
            // Shell 扩展是第三方代码，一律不许把异常带进宿主 UI 线程
            return hMenu != IntPtr.Zero;
        }
        finally
        {
            _cm2 = null;
            _cm3 = null;
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
            if (cmObj != null)
            {
                try { Marshal.ReleaseComObject(cmObj); } catch { /* 已释放 */ }
            }
        }
    }

    /// <summary>Shell 菜单标志位</summary>
    private const uint CMF_NORMAL = 0x00000000;
    private const uint CMF_EXTENDEDVERBS = 0x00000100;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_GRAYED = 0x00000001;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_TOPALIGN = 0x0000;

    private const int SW_SHOWNORMAL = 1;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;

    // ───────── 取 IContextMenu ─────────

    private static object? BindContextMenu(IntPtr owner, List<IntPtr> pidls)
    {
        IntPtr ppv;

        if (pidls.Count == 1)
        {
            // 单项：父目录 + 子 PIDL —— explorer 桌面用的就是这条
            var iidFolder = IID_IShellFolder;
            if (SHBindToParent(pidls[0], ref iidFolder, out var psf, out var child) != 0
                || psf == IntPtr.Zero)
                return null;
            try
            {
                var folder = (IShellFolder)Marshal.GetObjectForIUnknown(psf);
                var iidMenu = IID_IContextMenu;
                folder.GetUIObjectOf(owner, 1, new[] { child }, ref iidMenu, IntPtr.Zero, out ppv);
                if (ppv == IntPtr.Zero) return null;
                var o = Marshal.GetObjectForIUnknown(ppv);
                Marshal.Release(ppv);
                return o;
            }
            finally { Marshal.Release(psf); }
        }

        // 多选：ShellItemArray，天然支持跨目录与虚拟项混选
        if (SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out var pia) != 0
            || pia == IntPtr.Zero)
            return null;
        try
        {
            var arr = (IShellItemArray)Marshal.GetObjectForIUnknown(pia);
            var bhid = BHID_SFUIObject;
            var iidMenu = IID_IContextMenu;
            arr.BindToHandler(IntPtr.Zero, ref bhid, ref iidMenu, out ppv);
            if (ppv == IntPtr.Zero) return null;
            var o = Marshal.GetObjectForIUnknown(ppv);
            Marshal.Release(ppv);
            return o;
        }
        finally { Marshal.Release(pia); }
    }

    private static void AppendExtras(IntPtr hMenu, IReadOnlyList<Extra>? extras)
    {
        if (extras == null || extras.Count == 0) return;

        bool lastWasSep = false;
        foreach (var ex in extras)
        {
            if (ex.Separator)
            {
                if (lastWasSep) continue;          // 不让连续分隔线出现
                AppendMenu(hMenu, MF_SEPARATOR, IntPtr.Zero, null);
                lastWasSep = true;
                continue;
            }
            AppendMenu(hMenu, MF_STRING | (ex.Enabled ? 0u : MF_GRAYED), (IntPtr)ex.Id, ex.Text);
            lastWasSep = false;
        }
    }

    /// <summary>QueryContextMenu 的第一个命令 id（=1）。InvokeCommand 只认「相对偏移」，所以要减掉它。</summary>
    private const int IdCmdFirst = 1;

    private static void Invoke(IContextMenu cm, IntPtr owner, int cmd, Point screen)
    {
        // ⚠ lpVerb 要的是**相对 idCmdFirst 的偏移**，不是 TrackPopupMenuEx 直接返回的 id。
        //   传 id 本身（少减 1）会被 Shell 判成越界，InvokeCommand 直接返回 E_INVALIDARG ——
        //   现象就是「点了菜单什么都没发生」，非常难查。这里必须减。
        int offset = cmd - IdCmdFirst;

        var ci = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CMIC_MASK_PTINVOKE,
            hwnd = owner,
            lpVerb = (IntPtr)offset,     // MAKEINTRESOURCE：按偏移调用
            lpParameters = IntPtr.Zero,
            lpDirectory = IntPtr.Zero,
            nShow = SW_SHOWNORMAL,
            dwHotKey = 0,
            hIcon = IntPtr.Zero,
            lpTitle = IntPtr.Zero,
            lpVerbW = IntPtr.Zero,
            lpParametersW = IntPtr.Zero,
            lpDirectoryW = IntPtr.Zero,
            lpTitleW = IntPtr.Zero,
            ptInvoke = new Native.POINT(screen.X, screen.Y),
        };
        int hr = cm.InvokeCommand(ref ci);

        // 扩展自己失败（返回错误码）时才留痕：这类问题在界面上毫无提示，只能靠日志追溯
        if (hr < 0)
            Core.CrashGuard.LogText(
                $"[shellmenu] invoke 失败 cmd={cmd} offset={offset} " +
                $"verb='{VerbOf(cm, offset)}' hr=0x{hr:X8}");
    }

    /// <summary>取命令的规范动词名（GCS_VERBW），只为诊断日志用</summary>
    private static string VerbOf(IContextMenu cm, int offset)
    {
        IntPtr buf = IntPtr.Zero, reserved = IntPtr.Zero;
        try
        {
            buf = Marshal.AllocHGlobal(512);
            reserved = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(reserved, 0);
            cm.GetCommandString((IntPtr)offset, 4 /*GCS_VERBW*/, reserved, buf, 256);
            return Marshal.PtrToStringUni(buf) ?? "";
        }
        catch { return "?"; }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            if (reserved != IntPtr.Zero) Marshal.FreeHGlobal(reserved);
        }
    }

    // ───────── 菜单消息转发（子菜单 / 自绘项靠它）─────────

    private const int WH_MSGFILTER = -1;
    private const uint WM_INITMENU = 0x0116;
    private const uint WM_INITMENUPOPUP = 0x0117;
    private const uint WM_DRAWITEM = 0x002B;
    private const uint WM_MEASUREITEM = 0x002C;
    private const uint WM_MENUCHAR = 0x0120;

    private static IContextMenu2? _cm2;
    private static IContextMenu3? _cm3;
    private static IntPtr _hook;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // 委托必须由托管侧持引用，否则会被 GC 回收导致回调进已释放的存根
    private static readonly HookProc _hookProc = MenuHook;

    private static IntPtr MenuHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && lParam != IntPtr.Zero && (_cm2 != null || _cm3 != null))
        {
            var msg = Marshal.PtrToStructure<MSG>(lParam);
            uint m = msg.message;
            if (m == WM_INITMENUPOPUP || m == WM_DRAWITEM || m == WM_MEASUREITEM
                || m == WM_MENUCHAR || m == WM_INITMENU)
            {
                try
                {
                    if (_cm3 != null)
                    {
                        if (_cm3.HandleMenuMsg2(m, msg.wParam, msg.lParam, out var res) == 0)
                            return res;
                    }
                    else if (_cm2 != null && _cm2.HandleMenuMsg(m, msg.wParam, msg.lParam) == 0)
                    {
                        return IntPtr.Zero;
                    }
                }
                catch { /* 扩展自己抛的异常不该影响菜单继续弹 */ }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // ───────── 结构 / COM 接口 / P-Invoke ─────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Native.POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public Native.POINT ptInvoke;
    }

    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid BHID_SFUIObject = new("3981E225-F559-11D3-8E3A-00C04F6837D5");

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

        void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref uint rgfInOut);

        void GetUIObjectOf(IntPtr hwndOwner, uint cidl,
            [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);

        void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);

        void SetNameOf(IntPtr hwnd, IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszName,
            uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu,
            uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        void GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved,
            IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        // IContextMenu 的三个（vtable 必须原样复刻，接口继承在这里不被 COM 认可）
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu,
            uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        void GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved,
            IntPtr pszName, uint cchMax);

        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu,
            uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        void GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved,
            IntPtr pszName, uint cchMax);

        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        void GetPropertyStore(uint flags, ref Guid riid, out IntPtr ppv);

        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);

        void GetAttributes(uint attribFlags, uint sfgaoMask, out uint psfgaoAttribs);

        void GetCount(out uint pdwNumItems);

        void GetItemAt(uint dwIndex, out IntPtr ppsi);

        void EnumItems(out IntPtr ppenumShellItems);
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid,
        out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateShellItemArrayFromIDLists(uint cidl,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] rgpidl, out IntPtr ppsiItemArray);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y,
        IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

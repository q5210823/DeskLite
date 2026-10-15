using System.Runtime.InteropServices;
using System.Text;

namespace DeskLite.Win32;

/// <summary>
/// Win32 API 声明。桌面接管所需的窗口定位、跨进程读取 ListView 项、窗口挂载等。
/// </summary>
internal static class Native
{
    // ───────── ListView 消息 ─────────
    public const int LVM_FIRST = 0x1000;
    public const int LVM_GETITEMCOUNT = LVM_FIRST + 4;   // 0x1004
    public const int LVM_GETITEMPOSITION = LVM_FIRST + 16; // 0x1010
    public const int LVM_GETITEMTEXTW = LVM_FIRST + 115;   // 0x1073
    public const int LVM_GETITEMW = LVM_FIRST + 75;        // 0x104B —— W 版，用于读回 iImage

    public const uint LVIF_TEXT = 0x0001;
    public const uint LVIF_IMAGE = 0x0002;

    // ───────── 进程/内存 ─────────
    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;

    // ───────── 窗口样式 ─────────
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_CHILD = 0x40000000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// WS_EX_TRANSPARENT —— 鼠标消息直接穿透到下层窗口。
    /// 对齐参考线覆盖层靠它做到「盖在最上面但完全不挡操作」。
    /// </summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>分层窗口标志。WinForms 的 <c>Form.Opacity &lt; 1</c> 就是靠它实现的。</summary>
    public const int WS_EX_LAYERED = 0x00080000;

    // ───────── 窗口样式（GWL_STYLE）─────────
    // 组合出「非客户区」的这几位置 1 时，客户区会比窗口外框小一圈，
    // 而 UpdateLayeredWindow 提交的位图是按窗口外框铺的 —— 两者不等就会把窗口改小。
    public const int WS_CAPTION = 0x00C00000;     // = WS_BORDER | WS_DLGFRAME
    public const int WS_BORDER = 0x00800000;
    public const int WS_DLGFRAME = 0x00400000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;

    /// <summary>WS_EX_WINDOWEDGE —— 有边框的窗口才带，跟随 <see cref="WS_CAPTION"/> 一起清</summary>
    public const int WS_EX_WINDOWEDGE = 0x00000100;

    /// <summary>SetLayeredWindowAttributes 的 LWA_ALPHA：用常数 alpha 混合整个窗口</summary>
    public const uint LWA_ALPHA = 0x00000002;

    /// <summary>
    /// 设置分层窗口的透明方式。检查返回值可判断「窗口当前能否分层」——
    /// 非分层窗口（例如某些父子关系下的子窗口）会返回 <c>false</c>，
    /// 这是「调了透明度但没反应」的根因判定手段。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

    /// <summary>WM_COMMAND —— 用于向 SHELLDLL_DefView 发送「显示桌面图标」切换命令</summary>
    public const int WM_COMMAND = 0x0111;

    /// <summary>
    /// 「查看 → 显示桌面图标」菜单项命令 ID。
    /// 向 SHELLDLL_DefView 发送 WM_COMMAND + 该 ID 可切换桌面图标显示。
    /// 实测：直接 ShowWindow(ListView, SW_HIDE) 会被 explorer 立即恢复，必须走这个命令。
    /// </summary>
    public const int TOGGLE_DESKTOP_ICONS = 0x7402;


    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    // ───────── 结构体 ─────────

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    // ───────── 分层窗口（逐像素 alpha）─────────
    //
    // 用 UpdateLayeredWindow 可以让窗口的每个像素带自己的 alpha，
    // 于是「半透明底板 + 完全不透明的图标文字」可以共存 ——
    // 这是 Form.Opacity（整窗一个 alpha）做不到的。

    public const int ULW_ALPHA = 0x00000002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;
    public const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx, cy;
        public SIZE(int w, int h) { cx = w; cy = h; }
    }

    /// <summary>传给 UpdateLayeredWindow 的混合方式；AlphaFormat 必须是 AC_SRC_ALPHA 才有逐像素透明</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;      // 负值 = 自上而下（与 GDI+ 的扫描行顺序一致，省一次翻转）
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RGBQUAD
    {
        public byte rgbBlue, rgbGreen, rgbRed, rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public RGBQUAD[] bmiColors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
        uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    /// <summary>把「父窗口客户区坐标」换算成屏幕坐标（传 (0,0) 即得到客户区原点）</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// 建立一块 32bpp 的 DIB section，返回位图句柄与像素首地址。
    ///
    /// ⚠️ 不能用 <c>Bitmap.GetHbitmap()</c> —— 它产出的是 Format32bppRgb，
    /// **alpha 通道会被丢掉**，贴上去就是一张不透明的图（分层等于没做）。
    /// 必须自己建 DIB section 再用 LockBits 把 ARGB 像素原样拷进去。
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi,
        uint usage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    /// <summary>非托管内存块拷贝（把 GDI+ 位图的 ARGB 扫描行搬进 DIB section）</summary>
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    public static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

    /// <summary>LVITEMW —— x64 布局</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LVITEM
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    // ───────── user32 ─────────

    public const uint SPI_GETDESKWALLPAPER = 0x0073;

    /// <summary>读取当前桌面壁纸路径（用于「样式预览」合成真实背景）</summary>
    public static string? DesktopWallpaper()
    {
        try
        {
            var sb = new StringBuilder(1024);
            if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) && sb.Length > 0)
            {
                var p = sb.ToString();
                if (File.Exists(p)) return p;
            }
        }
        catch { }
        return null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);


    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);


    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    /// <summary>
    /// PIDL 版重载 —— 配合 <see cref="SHGFI_PIDL"/> 使用，此时第一个参数是 ITEMIDLIST 指针而不是字符串。
    /// 虚拟项（此电脑 / 回收站 / 网络）没有文件路径，只能走这条：
    /// 「解析名 ::{CLSID}」→ <see cref="SHParseDisplayName"/> 得到 PIDL → 再取图标 / 图标索引。
    /// </summary>
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    public const uint SHGFI_PIDL = 0x000000008;

    /// <summary>
    /// 把 Shell 解析名（如 <c>::{20D04FE0-3AEA-1069-A2D8-08002B30309D}</c>）解析成 PIDL。
    /// 返回 0（S_OK）表示成功，此时 <paramref name="ppidl"/> 需用
    /// <c>Marshal.FreeCoTaskMem</c> 释放。
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string pszName, IntPtr pbc,
        out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    // ───────── 回收站（删除走系统回收，可还原）─────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    public const uint FO_DELETE = 0x0003;
    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOERRORUI = 0x0400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>把文件/文件夹移入回收站（可还原），失败返回 false</summary>
    public static bool MoveToRecycleBin(string path)
    {
        // pFrom 必须是双 null 结尾的多字符串
        var op = new SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            pTo = null,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0;
    }

    // ───────── 回收站：查询数量 / 清空 ──────────

    /// <summary>清空回收站。返回 0（S_OK）表示成功。</summary>
    /// <param name="pszRootPath">为 null 表示所有驱动器</param>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    public const uint SHERB_NOCONFIRMATION = 0x00000001;
    public const uint SHERB_NOPROGRESSUI = 0x00000002;
    public const uint SHERB_NOSOUND = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    /// <summary>查询回收站里的项数与占用字节数</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>回收站当前项数（查不到返回 -1）</summary>
    public static long RecycleBinItemCount()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64NumItems : -1;
        }
        catch { return -1; }
    }

    /// <summary>回收站占用字节数（查不到返回 -1）</summary>
    public static long RecycleBinSize()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : -1;
        }
        catch { return -1; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint SHGFI_SYSICONINDEX = 0x000004000;

    // ───────── Shell 系统 Imagelist（取高清图标的可靠途径）─────────
    //
    // 实测：SHGetFileInfo(SHGFI_ICON) 对 .lnk 快捷方式经常返回「纯黑图标」，
    // 而先用 SHGFI_SYSICONINDEX 拿索引、再从系统 Imagelist 取图则可以拿到真实图标。

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
        [PreserveSig] int GetImageInfo(int i, IntPtr pImageInfo);
        [PreserveSig] int Copy(int iDst, IImageList punkSrc, int iSrc, int uFlags);
        [PreserveSig] int Merge(int i1, IImageList punk2, int i2, int dx, int dy, ref Guid riid, ref IntPtr ppv);
        [PreserveSig] int Clone(ref Guid riid, ref IntPtr ppv);
        [PreserveSig] int GetImageRect(int i, IntPtr prc);
        [PreserveSig] int GetIconSize(ref int cx, ref int cy);
        [PreserveSig] int SetIconSize(int cx, int cy);
        [PreserveSig] int GetImageCount(ref int pi);
        [PreserveSig] int SetImageCount(int uNewCount);
        [PreserveSig] int SetBkColor(int clrBk, ref int pclr);
        [PreserveSig] int GetBkColor(ref int pclr);
    }

    [DllImport("shell32.dll")]
    public static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    public const int SHIL_LARGE = 0;
    public const int SHIL_EXTRALARGE = 2;
    public const int SHIL_JUMBO = 4;
    public const int ILD_TRANSPARENT = 0x1;

    public static readonly Guid IID_IImageList =
        new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    // ───────── gdi32：图标 → 保留 alpha 的位图 ─────────
    //
    // 说明：.NET 的 Icon.ToBitmap() / Bitmap.FromHbitmap() 会丢掉 alpha 通道，
    // 把透明区域渲染成黑色（本项目实测到的"黑方块"就是这个原因）。
    // 正确做法是 DrawIconEx 到一块 32bpp ARGB 的 DIB 上 —— 它会尊重 alpha。

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetIconInfo(IntPtr hIcon, ref ICONINFO pIconInfo);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    public const int DI_NORMAL = 0x0003;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

    // DeleteObject 已在「分层窗口」段声明（gdi32），此处不再重复。

    /// <summary>GDI BITMAP 结构 —— 用来查询 HICON 内部位图的真实像素尺寸</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll")]
    public static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    // ───────── 资源占用自检 ─────────

    /// <summary>进程已分配的 GDI / USER 对象数（用于轻量化核对）</summary>
    public const uint GR_GDIOBJECTS = 0;
    public const uint GR_USEROBJECTS = 1;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMin, IntPtr dwMax);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// 把本进程「已不需要的物理页面」交还系统（等价于 TrimWorkingSet）。
    ///
    /// 用途：WinForms + .NET 启动时会把运行时与框架程序集大量映射进工作集，
    /// 首屏渲染结束后其中大部分页面短期内不会再被访问，但它们仍占着物理内存，
    /// 让任务管理器里的「内存」看起来偏大。启动完成、界面稳定后调用一次，
    /// 由系统回收这些页面即可显著降低常驻占用；后续真正用到的页面会按需换入，
    /// 功能与正确性完全不受影响（只是首次访问多一点页错误）。
    ///
    /// 注意：只在启动完成时做**一次**，绝不在运行期反复调用
    /// —— 反复 trim 会让热页面不断被丢弃再换入，反而拖慢响应。
    /// </summary>
    public static bool TrimWorkingSet()
    {
        try { return SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); }
        catch { return false; }
    }

    // ───────── user32：消息注册 / 广播 / 全局热键（右键集成 + 命令总线）─────────

    /// <summary>广播到所有顶层窗口</summary>
    public const int HWND_BROADCAST = 0xFFFF;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ───────── kernel32 ─────────

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
        IntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, IntPtr dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, IntPtr dwSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int dwProcessId);


}

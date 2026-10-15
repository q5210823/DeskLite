using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DeskLite.Core;
using DeskLite.Win32;

namespace DeskLite.UI;

/// <summary>
/// 格子窗口 —— 只是一层很薄的窗口壳，真正的绘制与交互都在 <see cref="BoxView"/> 里。
///
/// 壳层负责：
///   · 无边框 + 圆角 + **逐像素 alpha** 毛玻璃底板（CFG_SYS_GLASS）
///   · 标题区拖动移动格子（位置持久化）
///   · 四角 / 四边拖动改尺寸，内容按新宽度自动重排列数
///   · 双击标题栏折叠 / 展开（对齐 DeskGo 的"格子自动收展"）
///   · 项右键菜单、改名落地、移入回收站
///   · SetParent 嵌入桌面层
///   · 尺寸贴合内容
///
/// ── 关于透明度（本轮改造的核心）──
///
/// 旧做法是 <c>Form.Opacity</c>，它底层是 <c>SetLayeredWindowAttributes(LWA_ALPHA)</c>：
/// 整窗**一个常数 alpha**。后果就是底板和图标被同一个系数一起乘掉 ——
/// 调低透明度时图标跟着一起变淡，用户看到的正是这个现象。
///
/// 新做法改用 <c>UpdateLayeredWindow</c>：窗口内容换成一张 32bpp ARGB 位图，
/// **每个像素带自己的 alpha**。于是底板填 alpha≈235 的深色、图标与文字照常以 255 合成，
/// 两者互不干扰 —— 「底板透明、图标保持清晰」就是这么做出来的。
/// </summary>
internal sealed class BoxForm : Form
{
    private const int CornerRadius = 8;

    /// <summary>
    /// 关掉逐像素 alpha 通道，退回旧的整窗 <c>Form.Opacity</c>。
    /// 只用于诊断对照（判断分层合成占多少内存 / 出问题时快速回退），正常不设。
    /// </summary>
    internal static readonly bool LayerDisabled =
        Environment.GetEnvironmentVariable("DESKLITE_NOLAYER") == "1" ||
        Environment.GetEnvironmentVariable("DESKLITE_NOOPACITY") == "1";

    private readonly DeskBox _box;
    private readonly BoxView _view;
    private readonly ContextMenuStrip _itemMenu;
    private readonly ContextMenuStrip _boxMenu;

    /// <summary>布局追踪开关（与 MainContext.TraceLayout 同一个环境变量）</summary>
    private static readonly bool TraceLayout =
        Environment.GetEnvironmentVariable("DESKLITE_TRACE_LAYOUT") == "1";

    private readonly Action _onLayoutChanged;
    private readonly Action _onItemsChanged;
    private readonly Action<IReadOnlyList<string>, int>? _onMoveToBox;
    private readonly Action<int, IReadOnlyList<string>>? _onReorderBox;
    private readonly Action<IReadOnlyList<string>>? _onClearAssignMany;
    private readonly Action<IReadOnlyList<string>>? _onFilesDropped;

    /// <summary>格子级操作回调：重命名 / 删除 / 新建（由 MainContext 真正执行并落盘）</summary>
    private readonly Action<int, string>? _onRenameBox;
    private readonly Action<int>? _onDeleteBox;
    private readonly Action? _onCreateBox;

    private bool _dragging;
    private bool _dblTitle;
    private Point _dragCursorStart;
    private Point _dragWindowStart;

    // ── 边缘拖拽调整尺寸 ──
    // 方向位掩码：1 = 左, 2 = 右, 4 = 上, 8 = 下；0 表示不在边缘
    private const int DirLeft = 1, DirRight = 2, DirTop = 4, DirBottom = 8;

    private bool _resizing;
    private int _resizeDir;
    private Rectangle _resizeStart;
    private Point _resizeCursorStart;

    /// <summary>本轮拖拽实际应用的客户区尺寸（落盘用，不读可能被外部改过的 Width/Height）</summary>
    private Size _resizeApplied;

    /// <summary>边缘感应宽度（按 DPI 缩放）</summary>
    private int Grip => Math.Max(4, (int)Math.Round(7 * DeviceDpi / 96.0));

    /// <summary>四角感应边长 —— 比边缘宽一些，角落更好抓</summary>
    private int GripCorner => Math.Max(10, (int)Math.Round(16 * DeviceDpi / 96.0));

    /// <summary>右键菜单当前作用的项（为 null 表示点在空白处）</summary>
    private DiscoveredItem? _menuItem;

    /// <summary>标题就地改名用的编辑框（null 表示不在改名）</summary>
    private TextBox? _titleEditor;

    /// <summary>拖动移动时的吸附顾问（由 MainContext 提供；为 null 则不吸附）</summary>
    private readonly IBoxSnapper? _snapper;

    // ───────── 逐像素 alpha 渲染缓冲（分层窗口）─────────
    //
    // 渲染 = 「GDI+ 画到 32bpp 位图」+「把像素搬进 DIB section」+「UpdateLayeredWindow 提交」。
    // 这三步每次重绘都要做，所以位图与 DIB 都缓存下来，只在尺寸变化时重建 ——
    // 否则鼠标划过（悬停高亮）就会不断分配几百 KB 的位图，GC 抖动明显。
    private Bitmap? _buf;                 // 32bpp ARGB 画布
    private IntPtr _dib;                  // DIB section 位图句柄
    private IntPtr _dibBits;              // DIB 像素首地址
    private IntPtr _dibDc;                // 与 DIB 绑定的内存 DC
    private IntPtr _dibOld;               // DIB 之前被选中的位图（还原用）
    private int _dibW, _dibH;

    private bool _rendering;              // 防重入：ULW 可能反过来触发 Resize
    private bool _renderAgain;
    private bool _ulwErrorLogged;

    // ───────── 渲染合并 ─────────
    //
    // 一次「灌内容」会连着触发好几轮重绘请求：
    //   视图重建（BoxView.SetItems 末尾 Repaint）→ 展开状态（Collapsed setter）
    //   → 尺寸变化（FitToContent → ClientSize → OnResize）→ 收尾显式重绘
    // 每一次都是一整轮「GDI+ 全量绘制 + 把像素搬进 DIB + UpdateLayeredWindow 提交」，
    // 四个格子就是十几轮 —— 启动耗时与瞬态内存大头都在这里。
    // 这些中间帧用户根本看不见（同一消息循环内就过去了），所以合并成一次。
    private int _renderSuspend;           // >0 表示处于批量布局中，重绘请求只登记不执行
    private bool _renderPending;          // 挂起期间是否收到过重绘请求

    /// <summary>
    /// 延迟首帧：把「建窗阶段的几次重绘」（句柄创建 / 非客户区样式修正 / 首次尺寸）
    /// 推迟到紧随其后的首次 <see cref="SetItems"/> 一起画。
    ///
    /// 由 MainContext 在 <c>CreateBoxes</c> / <c>CreateBoxInteractive</c> 里调用 ——
    /// 那两处紧跟着就是 <c>RefreshAll()</c>，所以最多只延迟几十微秒，用户感知不到；
    /// 而分层窗口在第一次 <c>UpdateLayeredWindow</c> 之前本来就是不可见的，
    /// 所以延迟也不会露出空白窗口（实测建窗阶段要白渲染 3 次）。
    /// </summary>
    private bool _deferFirstRender;

    internal void DeferFirstRender() => _deferFirstRender = true;

    /// <summary>
    /// 放行「延迟首帧」并把积攒的重绘一次做掉。
    /// 每个「会连续改布局」的公开入口（<see cref="SetItems"/> / <see cref="ApplySettings"/>）
    /// 结束时都必须调它 —— 否则若首帧还没放行，那些重绘请求会被一直吞掉，格子永远不显示。
    /// </summary>
    private void FlushRender()
    {
        if (!_deferFirstRender && !_renderPending) return;
        _deferFirstRender = false;
        _renderPending = false;
        try { RenderLayered(_view); } catch { }
    }

    /// <summary>
    /// 挂起重绘：区间内的所有重绘请求合并为「结束时恰好重绘一次」。
    /// 只用于 <see cref="SetItems"/> / <see cref="ApplySettings"/> 这类会连续改布局的入口，
    /// 交互路径（悬停、拖动）不要用 —— 那里每一帧都是用户要看的。
    /// </summary>
    private RenderScope SuspendRender() => new(this);

    private sealed class RenderScope : IDisposable
    {
        private BoxForm? _form;
        public RenderScope(BoxForm form) { _form = form; _form._renderSuspend++; }

        public void Dispose()
        {
            var f = _form;
            if (f == null) return;
            _form = null;                     // 幂等：重复 Dispose 直接返回

            if (--f._renderSuspend > 0) return;     // 还有更外层的作用域
            if (!f._renderPending) return;

            f._renderPending = false;
            f.RenderLayered(f._view);               // 合并后的唯一一次提交
        }
    }
    private bool _lastUlwOk;
    private int _lastUlwErr;

    /// <summary>
    /// 诊断埋点：设 DESKLITE_TRACE_ULW=1 后打印每次 UpdateLayeredWindow 的坐标往返
    /// </summary>
    private static readonly bool UlwTraceOn =
        Environment.GetEnvironmentVariable("DESKLITE_TRACE_ULW") == "1";

    private static void UlwTrace(string s)
    {
        if (!UlwTraceOn) return;
        try { Console.WriteLine("[ULW] " + s); } catch { }
    }

    /// <summary>
    /// 生命周期窗口几何追踪（诊断用，<c>DESKLITE_TRACE_LAYOUT=1</c> 时开）。
    ///
    /// 目的：分清「WinForms 记账的尺寸」「真实窗口矩形」「真实客户区」「子视图尺寸」
    /// 四者到底在哪一步开始不一致 —— 这是定位「格子越用越小 / 点不到图标」的关键。
    /// </summary>
    private void LogLife(string tag)
    {
        if (!TraceLayout) return;
        try
        {
            string view = _view.IsHandleCreated
                ? $"{_view.Width}x{_view.Height}@{_view.Left},{_view.Top}"
                : "(无句柄)";
            string nat = "";
            if (IsHandleCreated)
            {
                Native.GetWindowRect(Handle, out var wr);
                Native.GetClientRect(Handle, out var cr);
                long st = Native.GetWindowLongPtr(Handle, Native.GWL_STYLE).ToInt64();
                long ex = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
                nat = $" win={wr.Right - wr.Left}x{wr.Bottom - wr.Top}"
                    + $" cli={cr.Right - cr.Left}x{cr.Bottom - cr.Top}"
                    + $" cap={((st & 0x00C00000) != 0 ? 1 : 0)} thick={((st & 0x00040000) != 0 ? 1 : 0)}"
                    + $" popup={((st & 0x80000000L) != 0 ? 1 : 0)} style=0x{st & 0xFFFFFFFF:X8}"
                    + $" ex=0x{ex & 0xFFFFFFFF:X8}";
            }
            CrashGuard.LogText($"[life] Box {_box.Id} {tag} Size={Width}x{Height} "
                + $"Client={ClientSize.Width}x{ClientSize.Height} Bounds={Bounds.Left},{Bounds.Top} "
                + $"View={view}{nat}");
        }
        catch (Exception ex) { CrashGuard.Log(ex, "LogLife"); }
    }

    /// <summary>菜单打开时的目标格子（用于「改回自动归类」）</summary>
    private readonly Func<string, bool>? _onClearAssign;

    public int BoxId => _box.Id;

    /// <summary>诊断用：格子模型（位置 / 尺寸 / 折叠）</summary>
    public DeskBox Model => _box;

    /// <summary>是否走「逐像素 alpha」的分层渲染路径（诊断开关关掉时退回整窗 Opacity）</summary>
    internal bool UsesLayeredWindow => !LayerDisabled;

    /// <summary>诊断用：格子内图标 / 文字是否正在被就地改名</summary>
    internal bool IsRenaming => _view.IsRenaming;

    /// <summary>诊断用：选中第 index 项并开始改名</summary>
    internal void ProbeRename(int index = 0) => _view.ProbeRename(index);

    /// <summary>诊断用：格子内的项数</summary>
    internal int ItemCount => _view.ItemCount;

    /// <summary>
    /// 诊断用：最近一次 <c>UpdateLayeredWindow</c> 是否成功、Win32 错误码，以及当前位图尺寸。
    /// 用来确认「分层路径真的生效了」而不是悄悄回退。
    /// </summary>
    internal (bool Ok, int Error, Size Bitmap) LayerState
        => (_lastUlwOk, _lastUlwErr, _buf?.Size ?? Size.Empty);

    public BoxForm(DeskBox box, IconCache icons, AppSettings settings,
        Action onLayoutChanged, Action onItemsChanged,
        Action<IReadOnlyList<string>, int>? onMoveToBox = null,
        Action<IReadOnlyList<string>>? onFilesDropped = null,
        Func<string, bool>? onClearAssign = null,
        Action<int, string>? onRenameBox = null,
        Action<int>? onDeleteBox = null,
        Action? onCreateBox = null,
        Action<int, IReadOnlyList<string>>? onReorderBox = null,
        Action<IReadOnlyList<string>>? onClearAssignMany = null,
        IBoxSnapper? snapper = null)
    {
        _box = box;
        _snapper = snapper;
        _onLayoutChanged = onLayoutChanged;
        _onItemsChanged = onItemsChanged;
        _onMoveToBox = onMoveToBox;
        _onReorderBox = onReorderBox;
        _onClearAssignMany = onClearAssignMany;
        _onFilesDropped = onFilesDropped;
        _onClearAssign = onClearAssign;
        _onRenameBox = onRenameBox;
        _onDeleteBox = onDeleteBox;
        _onCreateBox = onCreateBox;

        // ── 窗口壳 ──
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimizeBox = false;
        MaximizeBox = false;
        Text = box.Name;
        KeyPreview = false;
        DoubleBuffered = true;

        // ⚠ 不再设置 Form.Opacity（除非显式关掉分层做对照）。
        //   Opacity < 1 会让 WinForms 去调 SetLayeredWindowAttributes，
        //   而它和 UpdateLayeredWindow 是互斥的两条路（后者会返回 ERROR_INVALID_PARAMETER）。
        //   透明度现在由底板像素的 alpha 表达。
        if (LayerDisabled) Opacity = settings.Opacity;

        // ── 内容视图 ──
        _view = new BoxView(box.Id, icons, settings)
        {
            Dock = DockStyle.Fill,
            IdealColumns = IdealColumnsOf(box.Id),
            Title = box.Name,
            Collapsed = box.Collapsed,
        };
        _view.ItemActivated += it => Open(it);
        _view.ContextRequested += OnContextRequested;
        _view.DeleteRequested += it => { _menuItem = it; DeleteItem(it); };
        _view.DeleteSelectedRequested += DeleteItems;
        _view.CollapseToggled += ToggleCollapse;
        _view.RenameRequested += DoRename;
        _view.ItemsMovedToBox += (paths, target) => _onMoveToBox?.Invoke(paths, target);
        _view.ReorderRequested += ReorderItems;
        _view.FilesDropped += files => _onFilesDropped?.Invoke(files);
        _view.MouseDown += View_TitleMouseDown;
        _view.MouseMove += View_TitleMouseMove;
        _view.MouseUp += View_TitleMouseUp;

        BackColor = _view.BackgroundColor;
        Controls.Add(_view);

        _itemMenu = BuildItemMenu();
        _boxMenu = BuildBoxMenu();

        FitToContent(initial: true);
        Location = ClampToScreen(new Point(box.X, box.Y), Size);
        LogLife("ctor/end");
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LogLife("OnLoad");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LogLife("OnShown");
    }

    // ───────── 分层窗口：创建参数 / 生命周期 ─────────

    /// <summary>
    /// 建窗时就带上 <c>WS_EX_LAYERED</c>。
    ///
    /// 为什么不放到 <c>OnHandleCreated</c> 里补：补标志会让窗口先以普通窗口身份显示一帧，
    /// 那一帧是不透明的黑板，视觉上会闪。直接在建窗参数里给，第一帧就是分层窗口。
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (UsesLayeredWindow)
            {
                cp.ExStyle |= Native.WS_EX_LAYERED;

                // ── 把「非客户区」从建窗第一帧起就掐掉 ──
                //
                // 实测（DESKLITE_TRACE_LAYOUT=1）：即便构造函数里写了 FormBorderStyle.None，
                // 本机建窗瞬间窗口仍带 WS_CAPTION，客户区比窗口外框小 16×39；
                // WinForms 要到 OnHandleCreated 之后才把这几位移掉。
                // 而 RenderLayered 提交位图必须与窗口尺寸严格一致，客户区一变小
                // 就会顺着 UpdateLayeredWindow 把窗口一起改小（详见 PushLayered 注释）。
                // 这里显式清干净，不给这个窗口留任何非客户区。
                cp.Style &= ~(Native.WS_CAPTION | Native.WS_BORDER | Native.WS_DLGFRAME
                              | Native.WS_THICKFRAME | Native.WS_SYSMENU
                              | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX);
                cp.ExStyle &= ~Native.WS_EX_WINDOWEDGE;
            }
            return cp;
        }
    }

    /// <summary>
    /// 分层窗口的内容完全来自 <c>UpdateLayeredWindow</c> 提交的位图，
    /// 往窗口 DC 上画的任何东西都会被丢弃 —— 所以壳层干脆不画，
    /// 省掉每次 WM_PAINT 的整块背景填充。
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e) { /* 内容来自分层位图 */ }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // 分层窗口标志已置位、但还没调用过 UpdateLayeredWindow 的窗口是**完全不可见**的，
        // 所以句柄一建好就得先把内容推上去。
        LogLife("OnHandleCreated/前");
        RenderLayered(_view);
        LogLife("OnHandleCreated/后");
    }

    // ───────── 逐像素 alpha 渲染 ─────────

    /// <summary>
    /// 把格子重绘到分层位图并提交。
    /// 由 <see cref="BoxView.OnPaint"/>（内容变化）、<c>OnResize</c>、<c>OnHandleCreated</c>
    /// 以及嵌入桌面层之后调用。
    /// </summary>
    internal void RenderLayered(BoxView view)
    {
        if (LayerDisabled || IsDisposed || !IsHandleCreated) return;

        // 批量布局中 / 首帧尚未放行：只登记「待重绘」，等条件满足时合并成一次
        if (_renderSuspend > 0 || _deferFirstRender) { _renderPending = true; return; }

        // 防重入：UpdateLayeredWindow 会改写窗口位置，极端情况下会回灌一个 Resize
        if (_rendering) { _renderAgain = true; return; }

        // ── 位图尺寸取「窗口外框」，不是「客户区」──
        //
        // UpdateLayeredWindow 的位图是从**窗口原点**（含非客户区）开始贴的，
        // 而内容（BoxView）画在**客户区原点**。两者在无边框时重合，
        // 一旦窗口带上边框/标题栏就差一个偏移量。
        //
        // 所以：位图铺满整个窗口，内容按客户区原点平移 —— 两种情形都画得对，
        // 而且位图尺寸恒等于窗口尺寸，不会让 UpdateLayeredWindow 去改窗口大小。
        int w = Width, h = Height;
        if (w <= 0 || h <= 0) return;

        if (TraceLayout)
            CrashGuard.LogText($"[layout] Box {_box.Id} RenderLayered 位图={w}×{h} "
                + $"视图={view.Width}×{view.Height} 客户区={ClientSize.Width}×{ClientSize.Height} "
                + $"窗体={Width}×{Height}");

        _rendering = true;
        try
        {
            if (!EnsureBuffers(w, h)) return;

            var off = ClientOriginInWindow();

            // ── ① GDI+ 画到 32bpp ARGB 位图 ──
            using (var g = Graphics.FromImage(_buf!))
            {
                // 必须先清成全透明：GDI+ 的 FromImage 不会替你重置像素，
                // 不清的话上一帧的残影会留在圆角外的区域，看起来像方角。
                g.Clear(Color.Transparent);
                if (off.X != 0 || off.Y != 0) g.TranslateTransform(off.X, off.Y);
                view.PaintInto(g);
            }

            // ── ② 提交 ──
            PushLayered(w, h);
        }
        catch (Exception ex)
        {
            if (!_ulwErrorLogged) { _ulwErrorLogged = true; BoxView.T("   RenderLayered 异常：" + ex.Message); }
        }
        finally
        {
            _rendering = false;
            if (_renderAgain)
            {
                _renderAgain = false;
                RenderLayered(view);
            }
        }
    }

    /// <summary>
    /// 客户区原点相对窗口左上角的偏移。
    /// 无边框窗口恒为 (0,0)；万一窗口带着边框/标题栏（建窗瞬间的系统行为），
    /// 靠这个偏移把内容摆回客户区，而不是把窗口改小。
    /// </summary>
    private Point ClientOriginInWindow()
    {
        if (!IsHandleCreated) return Point.Empty;
        var p = new Native.POINT(0, 0);
        if (!Native.ClientToScreen(Handle, ref p)) return Point.Empty;
        if (!Native.GetWindowRect(Handle, out var wr)) return Point.Empty;
        return new Point(p.X - wr.Left, p.Y - wr.Top);
    }

    /// <summary>按需重建画布与 DIB section；尺寸没变就复用，返回是否可用</summary>
    private bool EnsureBuffers(int w, int h)
    {
        if (_buf == null || _buf.Width != w || _buf.Height != h)
        {
            _buf?.Dispose();
            _buf = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        }

        if (_dib != IntPtr.Zero && _dibW == w && _dibH == h) return true;

        ReleaseBuffers(keepBitmap: true);

        _dibDc = Native.CreateCompatibleDC(IntPtr.Zero);
        if (_dibDc == IntPtr.Zero) return false;

        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,                  // 负值 = 自上而下，与 GDI+ 扫描行顺序一致，省一次翻转
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,              // BI_RGB
                biSizeImage = (uint)(w * h * 4),
            },
            bmiColors = new Native.RGBQUAD[1],
        };

        _dib = Native.CreateDIBSection(_dibDc, ref bmi, Native.DIB_RGB_COLORS,
            out _dibBits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
        {
            Native.DeleteDC(_dibDc);
            _dibDc = IntPtr.Zero;
            return false;
        }

        _dibOld = Native.SelectObject(_dibDc, _dib);
        _dibW = w;
        _dibH = h;
        return true;
    }

    /// <summary>把画布像素搬进 DIB，再用 UpdateLayeredWindow 提交（含 pptDst 坐标系自校正）</summary>
    private void PushLayered(int w, int h)
    {
        if (_buf == null || _dib == IntPtr.Zero || _dibBits == IntPtr.Zero) return;

        // ── GDI+ 位图 → DIB ──
        //
        // ⚠ 不能用 Bitmap.GetHbitmap()：它产出 Format32bppRgb，alpha 会被整条丢掉，
        //   贴上去就是一张不透明的图（分层等于白做）。必须 LockBits 拿原始 ARGB 扫描行，
        //   再逐行搬进自己建的 DIB section。
        var data = _buf.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = w * 4;
            if (data.Stride == rowBytes)
            {
                Native.CopyMemory(_dibBits, data.Scan0, (uint)(rowBytes * h));
            }
            else
            {
                // GDI+ 的扫描行是 4 字节对齐的，32bpp 下一般 stride == w*4；
                // 万一不等（系统差异）就逐行搬，行距各按各的。
                for (int y = 0; y < h; y++)
                    Native.CopyMemory(
                        IntPtr.Add(_dibBits, y * rowBytes),
                        IntPtr.Add(data.Scan0, y * data.Stride),
                        (uint)rowBytes);
            }
        }
        finally { _buf.UnlockBits(data); }

        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,   // 逐像素 alpha 由位图自己带，这里保持 255 不做二次衰减
            AlphaFormat = Native.AC_SRC_ALPHA,
        };
        var ptSrc = new Native.POINT(0, 0);

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            // ── ⚠ 这里是「格子越用越小学」的根因所在，改动前先读完整段注释 ──
            //
            // UpdateLayeredWindow 会把**窗口外框**直接设成传入的 psize。
            // 旧代码把「客户区尺寸」当 psize 传，而窗口带非客户区时客户区比外框小一圈，
            // 于是每次提交窗口就缩小一圈，形成正反馈收缩：
            //     窗体 429×124 → 客户区 413×85 → 位图 413×85 → 窗口 413×85
            //  → 客户区 397×46  → 位图 397×46  → 窗口 397×46 → …
            // 而 OnResize / FitToContent 会把缩小后的值写回模型并落盘 ——
            // 用户看到的就是「格子越用越小、位置记不住」。
            //
            // 现在 psize 一律取**窗口当前的真实矩形**，与窗口外框恒等，调用是幂等的；
            // 位图尺寸也等于窗口外框（见 RenderLayered），两者天然对齐。
            //
            // pptDst 同理不再累加式自校正（旧代码靠事后比对落点去攒偏移量，
            // 结果把面板整体推偏 ~32px，边缘罩住邻格图标，图标既点不到也拖不走）。
            // 这里每次都用真实矩形现算，方向明确、不会漂。
            Native.GetWindowRect(Handle, out var cur);
            var size = new Native.SIZE(cur.Right - cur.Left, cur.Bottom - cur.Top);

            int px = cur.Left, py = cur.Top;   // 默认按屏幕坐标（顶层窗口）
            IntPtr parent = Native.GetParent(Handle);
            if (parent != IntPtr.Zero)
            {
                // 嵌进桌面层之后窗口是 Progman 的子窗口，pptDst 得按**父客户区坐标**给
                var origin = new Native.POINT(0, 0);
                if (Native.ClientToScreen(parent, ref origin)) { px -= origin.X; py -= origin.Y; }
            }
            var ptDst = new Native.POINT(px, py);

            _lastUlwOk = Native.UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref size,
                _dibDc, ref ptSrc, 0, ref blend, Native.ULW_ALPHA);
            _lastUlwErr = _lastUlwOk ? 0 : Marshal.GetLastWin32Error();

            if (!_lastUlwOk && !_ulwErrorLogged)
            {
                _ulwErrorLogged = true;
                BoxView.T($"   UpdateLayeredWindow 失败（Win32 错误 {_lastUlwErr}）");
            }

            UlwTrace($"bitmap={w}x{h} rect={cur.Left},{cur.Top} {size.cx}x{size.cy} " +
                     $"dst=({px},{py}) parent=0x{parent.ToInt64():X} ok={_lastUlwOk} err={_lastUlwErr}");

            if (TraceLayout)
            {
                Native.GetWindowRect(Handle, out var fin);
                CrashGuard.LogText(
                    $"[layout] Box {_box.Id} PushLayered 位图={w}×{h} " +
                    $"窗口rect={fin.Left},{fin.Top} {fin.Right - fin.Left}×{fin.Bottom - fin.Top} " +
                    $"WinForms窗体={Width}×{Height} 客户区={ClientSize.Width}×{ClientSize.Height} " +
                    $"Dpi={DeviceDpi} ok={_lastUlwOk}");
            }
        }
        finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
    }

    /// <summary>
    /// 释放分层渲染缓冲（GDI+ 画布 + DIB section + 内存 DC）。
    /// 用于「一键隐藏格子」期间：窗口本来就看不见，握着几百 KB 的位图纯属浪费。
    /// 需要重绘时 <see cref="EnsureBuffers"/> 会按需重建，不影响正确性。
    /// </summary>
    public void FreeBuffers() => ReleaseBuffers(keepBitmap: false);

    /// <summary>释放 DIB 与（可选的）画布</summary>
    private void ReleaseBuffers(bool keepBitmap)
    {
        if (!keepBitmap) { _buf?.Dispose(); _buf = null; }

        if (_dibDc != IntPtr.Zero)
        {
            if (_dibOld != IntPtr.Zero) Native.SelectObject(_dibDc, _dibOld);
            if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
            Native.DeleteDC(_dibDc);
        }

        _dib = IntPtr.Zero;
        _dibBits = IntPtr.Zero;
        _dibDc = IntPtr.Zero;
        _dibOld = IntPtr.Zero;
        _dibW = _dibH = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseBuffers(keepBitmap: false);
        base.Dispose(disposing);
    }

    // ───────── 尺寸 ─────────

    /// <summary>
    /// 按内容重算窗口尺寸（宽 = 列数，高 = 行数），并写回模型。
    ///
    /// <see cref="DeskBox.ManualSize"/> 置位后（用户拖过四角 / 四边）改为**尊重用户尺寸**：
    /// 只兜一个下限，不再自动贴合 —— 否则刚拉好的大小会被下一次桌面刷新抹掉。
    /// 装不下的内容由格子内部滚动承载（<c>BoxView.MaxScroll</c>）。
    /// </summary>
    private void FitToContent(bool initial = false)
    {
        BoxView.T("   FitToContent 开始");
        var want = _view.MeasureContent();

        // 「目标尺寸」与「写回模型的尺寸」分开算 ——
        //
        // ⚠ 不能拿事后的 Width/Height 当写回值：窗口尺寸可能被外部路径改过
        //   （最典型的就是 UpdateLayeredWindow 引起的非客户区收缩，见 PushLayered）。
        //   用被改过的值写回模型，用户的格子就会「越用越小」并且永久落盘。
        //   所以模型里存的永远是**我们本来想要的那个尺寸**，读也按客户区读。
        Size size;
        int persistW, persistH;
        if (_box.ManualSize)
        {
            int w = Math.Max(_view.MinWindowWidth, initial ? _box.Width : ClientSize.Width);
            int h = Math.Max(_view.MinWindowHeight, initial ? _box.Height : ClientSize.Height);
            size = _box.Collapsed ? new Size(w, _view.TitleHeight) : new Size(w, h);
            persistW = w;
            persistH = h;
        }
        else
        {
            size = _box.Collapsed ? new Size(want.Width, _view.TitleHeight) : want;
            persistW = want.Width;
            persistH = want.Height;
        }

        if (ClientSize != size)
        {
            ClientSize = size;
            if (!initial) _onLayoutChanged();
            BoxView.T("   FitToContent 结束（已改尺寸）");
        }
        else
        {
            if (initial) _view.Collapsed = _box.Collapsed;
            BoxView.T("   FitToContent 结束（尺寸未变）");
        }

        // 合并到一次日志：问题现场只需要这几个数
        if (TraceLayout)
        {
            CrashGuard.LogText(
                $"[layout] Box {_box.Id} FitToContent(initial={initial}) " +
                $"ManualSize={_box.ManualSize} Collapsed={_box.Collapsed} " +
                $"模型={_box.Width}×{_box.Height} 窗体={Width}×{Height} 客户区={ClientSize.Width}×{ClientSize.Height} " +
                $"样式={FormBorderStyle} 视图={_view.Width}×{_view.Height} " +
                $"内容需要={want.Width}×{want.Height} 项数={_view.ItemCount} " +
                $"下限={_view.MinWindowWidth}×{_view.MinWindowHeight}");
        }

        // 写回模型：手工尺寸记实际值，自动尺寸记内容需要的值。
        // 折叠时窗口高度就是标题栏高，不能写回 —— 否则展开会丢高度。
        _box.Width = persistW;
        if (!_box.Collapsed)
            _box.Height = persistH;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (LayerDisabled)
        {
            // 回退路径：形状只能靠窗口 Region 裁
            using var path = Shapes.RoundedPath(new Rectangle(0, 0, Width, Height), CornerRadius);
            Region = new Region(path);
        }
        else if (Region != null)
        {
            // 分层窗口自带「按 alpha 通道做命中测试」，圆角已经是透明的，
            // 再套一层 Region 等于裁两遍，而且部分系统上会让分层内容整块不显示。
            Region = null;
        }

        // 尺寸变了要重画（ICB 位图尺寸也跟着变）
        LogLife("OnResize/前");
        RenderLayered(_view);
        BoxView.T($"   OnResize → {Width}×{Height}");
    }

    // ───────── 内容 ─────────

    public void SetItems(IReadOnlyList<DiscoveredItem> items)
    {
        BoxView.T("  BoxForm.SetItems 开始");

        // 下面这一串原本各自触发一次完整重绘（视图重建 / 展开状态 / 尺寸变化 / 收尾），
        // 合并成结束时的一次。最后那次显式重绘是必须的：不能指望
        // 「尺寸变了 → OnResize → 重绘」这条副作用链（尺寸稳定时它根本不触发）。
        try
        {
            using (SuspendRender())
            {
                _view.SetItems(items);
                _view.Collapsed = _box.Collapsed;
                FitToContent();
                ClampIntoWorkArea();
            }
        }
        finally
        {
            // 首次灌入就是建窗阶段的收尾：放行「延迟首帧」并把一路积攒的重绘一次做掉。
            // 放在 finally 里是硬要求 —— 万一行程中抛了异常，也必须放行，
            // 否则这个格子会永远停在「有句柄但从未提交过位图」的不可见状态。
            FlushRender();
        }

        BoxView.T("  BoxForm.SetItems 结束");
    }

    // ───────── 格子内排序 ─────────

    /// <summary>
    /// 格子内拖动排序：把被拖走的项挪到 <paramref name="target"/> 位次，其余项整体让位，
    /// 然后把整格的新顺序交给 MainContext 落盘。
    ///
    /// 语义是「**占据目标格**」而不是「插到目标项之前」：用户指着一个格子松手，
    /// 期望被拖的图标就落在那一格。所以插入位置直接对「摘掉被拖项之后」的列表取
    /// <paramref name="target"/> —— 后者会让前移时少走一格（拖到第 3 格却落在第 2 格）。
    /// </summary>
    private void ReorderItems(IReadOnlyList<string> moving, int target)
    {
        var order = _view.ItemOrder.ToList();
        if (order.Count == 0) return;

        var set = new HashSet<string>(moving, StringComparer.OrdinalIgnoreCase);
        var picked = order.Where(set.Contains).ToList();
        if (picked.Count == 0) return;

        var rest = order.Where(p => !set.Contains(p)).ToList();
        int at = Math.Clamp(target, 0, rest.Count);

        var next = rest.Take(at).Concat(picked).Concat(rest.Skip(at)).ToList();
        if (next.SequenceEqual(order, StringComparer.OrdinalIgnoreCase)) return;

        _view.ApplyOrder(next);                    // 界面先跟上，拖动体感才连贯
        _onReorderBox?.Invoke(_box.Id, next);      // 再落盘
        BoxView.T($"   格子内排序 → 第 {at} 位（{picked.Count} 项）");
    }

    /// <summary>应用设置变更（毛玻璃 / 图标大小 / 主题 / 打开方式）</summary>
    public void ApplySettings(AppSettings s)
    {
        // 透明度不再走 Form.Opacity —— 它是整窗 alpha，会把图标一起调淡。
        // 现在只需重新渲染，新值在 BoxView.PaintInto 里作为底板 alpha 生效。
        if (LayerDisabled) Opacity = s.Opacity;

        BackColor = _view.BackgroundColor;

        // 同 SetItems：视图重绘 / 尺寸变化 / 收尾原本各来一遍，合并成一次
        using (SuspendRender())
        {
            _view.ApplySettings(s);
            FitToContent();
            ClampIntoWorkArea();
        }
        FlushRender();
    }

    /// <summary>窗口被拖到屏幕外时拉回可视区</summary>
    private void ClampIntoWorkArea()
    {
        var pos = ClampToScreen(Location, Size);
        if (pos == Location) return;
        Location = pos;
        _box.X = pos.X;
        _box.Y = pos.Y;
    }

    /// <summary>移动到指定坐标（自动排布时使用，同时写回模型）</summary>
    public void MoveTo(int x, int y)
    {
        _box.X = x;
        _box.Y = y;
        Location = new Point(x, y);
    }

    // ───────── 折叠 / 展开 ─────────

    private void ToggleCollapse()
    {
        _box.Collapsed = !_box.Collapsed;
        _view.Collapsed = _box.Collapsed;
        Height = _box.Collapsed ? _view.TitleHeight : _box.Height;
        _onLayoutChanged();
    }

    /// <summary>更换图标缓存（设置里改了图标尺寸后）</summary>
    public void SetIconCache(IconCache cache) => _view.SetIconCache(cache);

    /// <summary>改格子标题（重命名后由 MainContext 调用，同时刷新窗口标题与位图）</summary>
    public void SetTitle(string name)
    {
        _box.Name = name;
        Text = name;
        _view.Title = name;
        RenderLayered(_view);
    }

    /// <summary>清掉「用户手工定过尺寸」标记，回到按图标数量自动贴合</summary>
    private void ResetManualSize()
    {
        _box.ManualSize = false;
        FitToContent();
        ClampIntoWorkArea();
        _onLayoutChanged();
        BoxView.T("   已恢复自动贴合尺寸");
    }

    // ───────── 标题区拖动 / 边缘调整尺寸 ─────────

    /// <summary>
    /// 命中测试：鼠标是否落在格子边缘（四角优先，给更大的感应区）。
    /// 返回方向位掩码，0 表示不靠边。
    /// </summary>
    private int HitEdge(Point p)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return 0;

        int g = Grip, c = GripCorner;
        bool nearLeft = p.X < g, nearRight = p.X >= w - g;
        bool nearTop = p.Y < g, nearBottom = p.Y >= h - g;

        // 四角
        if (p.X < c && p.Y < c) return DirLeft | DirTop;
        if (p.X >= w - c && p.Y < c) return DirRight | DirTop;
        if (p.X < c && p.Y >= h - c) return DirLeft | DirBottom;
        if (p.X >= w - c && p.Y >= h - c) return DirRight | DirBottom;

        // 四边
        if (nearLeft) return DirLeft;
        if (nearRight) return DirRight;
        if (nearTop) return DirTop;
        if (nearBottom) return DirBottom;
        return 0;
    }

    private static Cursor? CursorFor(int dir)
    {
        if (dir == 0) return null;
        bool l = (dir & DirLeft) != 0, r = (dir & DirRight) != 0;
        bool t = (dir & DirTop) != 0, b = (dir & DirBottom) != 0;

        if ((l && t) || (r && b)) return Cursors.SizeNWSE;
        if ((r && t) || (l && b)) return Cursors.SizeNESW;
        if (l || r) return Cursors.SizeWE;
        if (t || b) return Cursors.SizeNS;
        return null;
    }

    private void View_TitleMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        // ① 边缘优先：开始调整尺寸（折叠时只允许改宽度）
        int dir = HitEdge(e.Location);
        if (dir != 0)
        {
            if (_box.Collapsed) dir &= ~(DirTop | DirBottom);
            if (dir != 0)
            {
                _resizing = true;
                _resizeDir = dir;
                _resizeStart = Bounds;
                _resizeCursorStart = Cursor.Position;
                _view.SuppressMouse = true;      // 别同时选中图标
                return;
            }
        }

        // ② 标题区：拖动移动 / 双击折叠
        if (e.Y >= _view.TitleHeight) return;

        if (e.Clicks >= 2) { _dblTitle = true; ToggleCollapse(); return; }

        _dblTitle = false;
        _dragging = true;
        _dragCursorStart = Cursor.Position;
        _dragWindowStart = Location;
    }

    private void View_TitleMouseMove(object? sender, MouseEventArgs e)
    {
        if (_resizing) { DoResize(); return; }

        if (_dragging)
        {
            var cur = Cursor.Position;
            var raw = new Point(
                _dragWindowStart.X + (cur.X - _dragCursorStart.X),
                _dragWindowStart.Y + (cur.Y - _dragCursorStart.Y));

            // 吸附：把「想去的位置」交给顾问，拿回修正后的落点（顺带画出对齐参考线）
            Location = _snapper is null
                ? raw
                : _snapper.Snap(_box.Id, new Rectangle(raw, Size));
            return;
        }

        // 光标反馈：贴边时显示调尺寸光标（由视图统一决定，避免被它自己覆盖）
        int dir = HitEdge(e.Location);
        if (_box.Collapsed) dir &= ~(DirTop | DirBottom);
        _view.EdgeCursor = CursorFor(dir);
    }

    /// <summary>拖动边缘时按起始矩形 + 光标位移重算窗口大小</summary>
    private void DoResize()
    {
        if (_resizeDir == 0) return;

        var cur = Cursor.Position;
        int dx = cur.X - _resizeCursorStart.X;
        int dy = cur.Y - _resizeCursorStart.Y;

        int minW = Math.Max(80, _view.MinWindowWidth);
        int minH = _box.Collapsed ? _view.TitleHeight : Math.Max(60, _view.MinWindowHeight);

        var r = _resizeStart;

        if ((_resizeDir & DirLeft) != 0)
        {
            int w2 = r.Width - dx;
            if (w2 >= minW) { r.X = r.X + dx; r.Width = w2; }
        }
        else if ((_resizeDir & DirRight) != 0)
        {
            r.Width = Math.Max(minW, r.Width + dx);
        }

        if ((_resizeDir & DirTop) != 0)
        {
            int h2 = r.Height - dy;
            if (h2 >= minH) { r.Y = r.Y + dy; r.Height = h2; }
        }
        else if ((_resizeDir & DirBottom) != 0)
        {
            r.Height = Math.Max(minH, r.Height + dy);
        }

        // 只设「位置 + 客户区尺寸」：模型里记的就是可绘制区域的大小。
        // 不用 Bounds（那是窗口外框，一旦窗口带上非客户区就会比客户区大一圈，
        // 落盘时会把外框当内容区存下去 —— 格子会一次比一次小）。
        Location = new Point(r.X, r.Y);
        ClientSize = new Size(r.Width, r.Height);
        _resizeApplied = new Size(r.Width, r.Height);
        BoxView.T($"   调整尺寸 → {r.Width}×{r.Height}");
    }

    private void View_TitleMouseUp(object? sender, MouseEventArgs e)
    {
        if (_resizing)
        {
            _resizing = false;
            _resizeDir = 0;
            _view.SuppressMouse = false;
            _view.EdgeCursor = null;

            // 落盘：位置 + 客户区尺寸 + 「用户手工定过尺寸」标记
            _box.X = Location.X;
            _box.Y = Location.Y;
            _box.Width = _resizeApplied.Width > 0 ? _resizeApplied.Width : ClientSize.Width;
            if (!_box.Collapsed)
                _box.Height = _resizeApplied.Height > 0 ? _resizeApplied.Height : ClientSize.Height;
            _box.ManualSize = true;

            _onLayoutChanged();
            BoxView.T($"   调整尺寸完成 → {Width}×{Height}（已记住）");
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        _snapper?.EndSnap();
        if (_dblTitle) { _dblTitle = false; return; }

        _box.X = Location.X;
        _box.Y = Location.Y;
        _onLayoutChanged();
    }

    // ───────── 右键菜单 ─────────

    private void OnContextRequested(DiscoveredItem? item, Point screenPoint)
    {
        _menuItem = item;
        if (_titleEditor != null) CommitBoxRename();

        // 点到项 → 项菜单；点在标题 / 空白 → 格子菜单
        if (item != null) { _itemMenu.Show(screenPoint); return; }
        _boxMenu.Show(screenPoint);
    }

    /// <summary>
    /// 格子自身的右键菜单（右键标题栏 / 格子空白处）。
    ///
    /// ⚠ <b>绝不能</b>在菜单自己的 <c>Closed</c> 事件里 <c>Dispose()</c> 它。
    /// <c>Closed</c> 触发时，<c>ToolStripManager</c> 的菜单过滤器仍把这个下拉当作
    /// 「当前活动菜单」；这时把它释放掉，下一次鼠标按下走到
    /// <c>ModalMenuFilter.CloseActiveDropDown</c> 就会访问已释放对象，
    /// 抛 <c>ObjectDisposedException</c> 且**绕过** UI 线程异常兜底，直接把进程带走
    /// —— 这正是「闪退」的成因之一。
    ///
    /// 正确做法与 <see cref="BuildItemMenu"/> 一致：构造时建一次、之后一直复用，
    /// 会变的项（折叠/展开、是否手工调过尺寸）交给 <c>Opening</c> 按需更新。
    /// </summary>
    private ContextMenuStrip BuildBoxMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("一键整理桌面", null, (_, _) => _onItemsChanged());
        m.Items.Add(new ToolStripSeparator());

        var miCollapse = new ToolStripMenuItem("折叠格子", null, (_, _) => ToggleCollapse());
        m.Items.Add(miCollapse);

        m.Items.Add("重命名此格子…", null, (_, _) => BeginBoxRename());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("新建格子…", null, (_, _) => _onCreateBox?.Invoke());
        var miDeleteBox = new ToolStripMenuItem("删除此格子…", null, (_, _) => DeleteBox());
        m.Items.Add(miDeleteBox);

        var miFitSep = new ToolStripSeparator();
        var miFit = new ToolStripMenuItem("自动贴合内容（回到按图标数量定大小）",
            null, (_, _) => ResetManualSize());
        m.Items.Add(miFitSep);
        m.Items.Add(miFit);

        m.Opening += (_, _) =>
        {
            miCollapse.Text = _box.Collapsed ? "展开格子" : "折叠格子";

            // 只有手工调过尺寸才给「恢复自动贴合」—— 否则点了也没有变化，徒增困惑
            miFitSep.Visible = miFit.Visible = _box.ManualSize;

            // 内置格子删不得：它的固定 ID 就是自动归类规则的落点，删掉之后扫描还会算出
            // 同一个 ID、启动时也会被重建回来。光把它置灰，用户只会以为是坏掉了 ——
            // 把原因直接写进菜单文字。
            miDeleteBox.Enabled = !_box.IsBuiltIn;
            miDeleteBox.Text = _box.IsBuiltIn ? "删除此格子（内置格不可删）" : "删除此格子…";
        };
        return m;
    }

    /// <summary>请求删除本格子（带确认；内置格子在菜单里就置灰了，这里是二道防线）</summary>
    private void DeleteBox()
    {
        if (_box.IsBuiltIn)
        {
            MessageBox.Show(this, "内置格子（系统 / 我的文件夹 / 我的文件 / APP）不能删除。",
                "无法删除", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int assigned = _view.ItemCount;
        var extra = assigned > 0
            ? $"\n\n格子里现有 {assigned} 项，删除后会回到「按规则自动归类」。"
            : "";

        var r = MessageBox.Show(this, $"确定删除格子「{_box.Name}」吗？{extra}",
            "删除格子", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        _onDeleteBox?.Invoke(_box.Id);
    }

    // ───────── 格子标题就地改名 ─────────

    /// <summary>诊断 / 托盘入口：开始重命名本格子</summary>
    internal void BeginBoxRename()
    {
        if (_titleEditor != null) return;

        int h = _view.TitleHeight;
        int pad = Math.Max(6, (int)Math.Round(10 * DeviceDpi / 96.0));

        var ed = new TextBox
        {
            Text = _box.Name,
            BorderStyle = BorderStyle.None,
            TextAlign = HorizontalAlignment.Center,
            BackColor = _view.BackgroundColor,
            ForeColor = _view.TitleColor,
            Font = _view.TitleFont,
            Bounds = new Rectangle(pad, (h - 22) / 2, Math.Max(40, Width - pad * 2), 22),
            TabStop = true,
        };

        ed.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { CommitBoxRename(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { CancelBoxRename(); e.SuppressKeyPress = true; }
        };
        ed.LostFocus += (_, _) => { if (_titleEditor != null) CommitBoxRename(); };

        _titleEditor = ed;
        Controls.Add(ed);
        ed.BringToFront();
        ed.Focus();
        ed.SelectAll();
    }

    private void CommitBoxRename()
    {
        var ed = _titleEditor;
        if (ed == null) return;
        _titleEditor = null;

        string text = ed.Text.Trim();
        Controls.Remove(ed);
        ed.Dispose();

        if (text.Length > 0 && !string.Equals(text, _box.Name, StringComparison.Ordinal))
            _onRenameBox?.Invoke(_box.Id, text);
    }

    private void CancelBoxRename()
    {
        var ed = _titleEditor;
        if (ed == null) return;
        _titleEditor = null;
        Controls.Remove(ed);
        ed.Dispose();
    }

    private ContextMenuStrip BuildItemMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("打开", null, (_, _) => { if (_menuItem != null) Open(_menuItem); });

        // ── 系统虚拟项的专属操作（普通文件没有意义，Opening 时按类型显隐）──
        var miEmptyBin = new ToolStripMenuItem("清空回收站…", null, (_, _) => EmptyRecycleBin());
        var miManage = new ToolStripMenuItem("管理（计算机管理）", null, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("compmgmt.msc") { UseShellExecute = true }); } catch { }
        });
        var miSysProps = new ToolStripMenuItem("系统属性", null, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "system") { UseShellExecute = true }); } catch { }
        });

        var miSepVirtual = new ToolStripSeparator();
        m.Items.Add(miEmptyBin);
        m.Items.Add(miManage);
        m.Items.Add(miSysProps);
        m.Items.Add(miSepVirtual);

        var miRename = new ToolStripMenuItem("重命名", null, (_, _) => _view.BeginRenameSelected());
        m.Items.Add(miRename);
        m.Items.Add(new ToolStripSeparator());

        var miCopyPath = new ToolStripMenuItem("复制完整路径", null, (_, _) => CopySelectedPaths());
        m.Items.Add(miCopyPath);

        var miLocate = new ToolStripMenuItem("打开所在文件夹", null, (_, _) =>
        {
            if (_menuItem == null) return;
            try { Process.Start("explorer.exe", $"/select,\"{_menuItem.Path}\""); } catch { }
        });
        m.Items.Add(miLocate);
        m.Items.Add("属性", null, (_, _) => ShowProperties());
        m.Items.Add(new ToolStripSeparator());

        var miAuto = new ToolStripMenuItem("按规则自动归类", null, (_, _) => ClearAssignSelected());
        m.Items.Add(miAuto);

        var miUnpin = new ToolStripMenuItem("以此格子为准（记住位置）", null, (_, _) => PinSelected());
        m.Items.Add(miUnpin);

        m.Items.Add(new ToolStripSeparator());
        var miDelete = new ToolStripMenuItem("删除", null, (_, _) => DeleteFromMenu());
        m.Items.Add(miDelete);

        // ── 格子本身的操作（重命名 / 新建 / 删除）──
        //
        // 这几个入口原先只在「右键标题栏或格子空白处」时出现，而格子装满图标之后
        // 几乎点不到空白处 —— 结果就是用户根本找不到格子管理功能。
        // 这里在图标菜单里再放一份，右键格子的任意位置都能管理这个格子。
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("重命名此格子…", null, (_, _) => BeginBoxRename());
        m.Items.Add("新建格子…", null, (_, _) => _onCreateBox?.Invoke());
        var miDeleteBox = new ToolStripMenuItem("删除此格子…", null, (_, _) => DeleteBox());
        m.Items.Add(miDeleteBox);

        m.Opening += (_, _) =>
        {
            int n = _view.SelectedCount;
            bool multi = n > 1;
            bool has = multi || _menuItem != null;

            // 虚拟项（此电脑 / 回收站 / 网络）没有磁盘实体：不能重命名、不能定位、不能删除。
            // 与其让操作静默失败，不如直接置灰。
            bool real = multi
                ? _view.SelectedRealCount > 0
                : _menuItem is { IsVirtual: false };

            // 但虚拟项各有各的专属操作：回收站能清空、此电脑能进管理 / 系统属性
            string clsid = !multi && _menuItem != null
                ? (VirtualItems.Lookup(_menuItem.Path)?.Clsid ?? "")
                : "";
            bool isBin = clsid.Equals(RecycleBinClsid, StringComparison.OrdinalIgnoreCase);
            bool isPc = clsid.Equals(ThisPcClsid, StringComparison.OrdinalIgnoreCase);

            miEmptyBin.Visible = isBin;
            if (isBin)
            {
                // 菜单里直接带上数量，让用户不用先打开才知道有没有东西
                long cnt = Native.RecycleBinItemCount();
                miEmptyBin.Text = cnt > 0 ? $"清空回收站（{cnt} 项）…" : "清空回收站（已空）";
                miEmptyBin.Enabled = cnt != 0;
            }

            miManage.Visible = isPc;
            miSysProps.Visible = isPc;
            miSepVirtual.Visible = isBin || isPc;

            // 多选时菜单文字带数量，明确告诉用户「这一下作用到几项」
            string suffix = multi ? $"（{n} 项）" : "";
            miCopyPath.Text = "复制完整路径" + suffix;

            // 内置格子删不得：其固定 ID 是自动归类规则的落点，删了下次扫描 / 启动还会重建。
            // 置灰之外还得把原因说清楚，否则用户只会以为是 bug。
            miDeleteBox.Enabled = !_box.IsBuiltIn;
            miDeleteBox.Text = _box.IsBuiltIn ? "删除此格子（内置格不可删）" : "删除此格子…";
            miAuto.Text = "按规则自动归类" + suffix;
            miUnpin.Text = "以此格子为准（记住位置）" + suffix;
            miDelete.Text = "删除" + suffix;

            miRename.Enabled = !multi && real;   // 就地改名只对单项有意义
            miLocate.Enabled = !multi && real;   // 「打开所在文件夹」同理
            miAuto.Enabled = has;
            miUnpin.Enabled = has;
            miDelete.Enabled = real;
        };
        return m;
    }

    /// <summary>
    /// 菜单的「当前作用对象」：多选时是整个选择集，单选时退回右键点到的那一项。
    /// 这样一套菜单代码同时支持单选和批量，不用维护两份。
    /// </summary>
    private IReadOnlyList<DiscoveredItem> MenuTargets
    {
        get
        {
            var sel = _view.SelectedItems;
            if (sel.Count > 1) return sel;
            return _menuItem != null ? new[] { _menuItem } : Array.Empty<DiscoveredItem>();
        }
    }

    private void CopySelectedPaths()
    {
        var t = MenuTargets;
        if (t.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, t.Select(i => i.Path))); }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    /// <summary>按规则自动归类：单选走原路径（会立刻刷新），多选走批量一次落盘</summary>
    private void ClearAssignSelected()
    {
        var t = MenuTargets;
        if (t.Count == 0) return;

        if (t.Count == 1)
        {
            if (_onClearAssign?.Invoke(t[0].Path) == true) _onItemsChanged();
            return;
        }
        _onClearAssignMany?.Invoke(t.Select(i => i.Path).ToList());
    }

    /// <summary>「以此格子为准」：把当前作用对象（一项或一批）钉在本格子并记住位次</summary>
    private void PinSelected()
    {
        var paths = MenuTargets.Select(i => i.Path).ToList();
        if (paths.Count == 0) return;
        _onMoveToBox?.Invoke(paths, _box.Id);
    }

    private void DeleteFromMenu()
    {
        var t = MenuTargets;
        if (t.Count == 0) return;

        if (t.Count == 1) DeleteItem(t[0]);
        else DeleteItems(t);
    }

    private const string RecycleBinClsid = "{645FF040-5081-101B-9F08-00AA002F954E}";
    private const string ThisPcClsid = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    /// <summary>清空回收站（带确认；用系统 API，与资源管理器里的行为一致）</summary>
    private void EmptyRecycleBin()
    {
        long n = Native.RecycleBinItemCount();
        if (n == 0)
        {
            MessageBox.Show(this, "回收站已经是空的。", "清空回收站",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        long bytes = Native.RecycleBinSize();
        string size = bytes > 0 ? $"\n\n共 {n} 项，约 {bytes / 1024.0 / 1024:0.0} MB。" : $"\n\n共 {n} 项。";

        var r = MessageBox.Show(this, $"确定清空回收站吗？{size}\n清空后无法还原。",
            "清空回收站", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes) return;

        int hr = Native.SHEmptyRecycleBin(Handle, null,
            Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);

        if (hr != 0)
        {
            MessageBox.Show(this, $"清空回收站失败（错误码 0x{hr:X8}）。", "清空回收站",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _onItemsChanged();
    }

    private void ShowProperties()
    {
        var it = _menuItem;
        if (it == null) return;

        // 虚拟项没有文件大小 / 修改时间可报，说明它的真实来源
        if (it.IsVirtual)
        {
            MessageBox.Show(this,
                $"名称：{it.Name}\n" +
                $"类型：{Rules.TypeName(it.Type)}（类型码 {(int)it.Type}）\n" +
                $"来源：Shell 命名空间虚拟项（不在文件系统中）\n" +
                $"路径：{it.Path}",
                "属性", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string info;
        if (Directory.Exists(it.Path))
        {
            info = $"名称：{it.Name}\n类型：文件夹（类型码 {(int)ItemType.Folder}）\n位置：{it.Path}";
        }
        else
        {
            var fi = new FileInfo(it.Path);
            info = $"名称：{it.Name}\n类型：{Rules.TypeName(it.Type)}（类型码 {(int)it.Type}）\n" +
                   $"大小：{fi.Length:N0} 字节\n修改：{fi.LastWriteTime:yyyy-MM-dd HH:mm}\n位置：{it.Path}";
        }
        MessageBox.Show(this, info, "属性", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ───────── 项操作 ─────────

    private static void Open(DiscoveredItem it)
    {
        try
        {
            // 虚拟项（此电脑 / 回收站 / 网络）没有磁盘路径，得交给 explorer 打开命名空间：
            //   explorer.exe shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}
            // 直接 Process.Start("::{CLSID}") 在部分系统上会被当成相对路径而失败。
            if (it.IsVirtual)
            {
                var vi = Core.VirtualItems.Lookup(it.Path);
                if (vi == null) return;
                Process.Start(new ProcessStartInfo("explorer.exe", vi.ShellPath) { UseShellExecute = true });
                return;
            }

            Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DoRename(DiscoveredItem it, string newName)
    {
        if (newName == it.Name) return;

        // 虚拟项没有实体文件可改名（右键菜单已置灰，这里是二道防线）
        if (it.IsVirtual)
        {
            MessageBox.Show(this, "系统项（此电脑 / 回收站 / 网络）不能重命名。",
                "无法重命名", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(it.Path)!;
            var ext = it.IsShortcut || File.Exists(it.Path) ? Path.GetExtension(it.Path) : "";
            var target = Path.Combine(dir, newName + ext);

            if (File.Exists(it.Path)) File.Move(it.Path, target);
            else if (Directory.Exists(it.Path)) Directory.Move(it.Path, target);
            else return;

            it.Path = target;
            it.Name = newName;
            _onItemsChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "重命名失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>单项删除（右键菜单 / Delete 键）</summary>
    private void DeleteItem(DiscoveredItem? it)
    {
        if (it == null) return;

        // 虚拟项不对应任何文件，「移入回收站」既无意义也不可能成功。
        // 顺带一提：ShFileOperation 对「回收站」自己发删除请求的行为也不可预期，必须挡住。
        if (it.IsVirtual)
        {
            MessageBox.Show(this,
                $"「{it.Name}」是系统项，不对应磁盘上的文件，无法删除。\n\n" +
                "如果想让它不出现在桌面上，请在「桌面图标设置」里关闭它。",
                "无法删除", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var r = MessageBox.Show(this,
            $"确定要删除「{it.Name}」吗？\n\n{it.Path}\n\n（文件将移入回收站，可还原）",
            "删除确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes) return;

        DeleteFiles(new[] { it });
    }

    /// <summary>多选批量删除：一次确认，逐个移入回收站，最后统一刷新一次</summary>
    private void DeleteItems(IReadOnlyList<DiscoveredItem> items)
    {
        var real = items.Where(i => !i.IsVirtual).ToList();
        int skipped = items.Count - real.Count;

        if (real.Count == 0)
        {
            MessageBox.Show(this,
                "选中的都是系统项（此电脑 / 回收站 / 网络），不对应磁盘上的文件，无法删除。",
                "无法删除", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var head = string.Join("\n", real.Take(12).Select(i => "· " + i.Name));
        if (real.Count > 12) head += $"\n…（其余 {real.Count - 12} 项未列出）";

        var tail = skipped > 0 ? $"\n\n（另有 {skipped} 个系统项会自动跳过）" : "";
        var r = MessageBox.Show(this,
            $"确定要删除选中的 {real.Count} 项吗？\n\n{head}{tail}\n\n（文件将移入回收站，可还原）",
            "批量删除确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes) return;

        DeleteFiles(real);
    }

    /// <summary>真正执行「移入回收站」，最后统一刷新一次 —— 批量时不能每项刷一遍</summary>
    private void DeleteFiles(IReadOnlyList<DiscoveredItem> items)
    {
        var failed = new List<string>();
        foreach (var it in items)
        {
            try
            {
                if (!Native.MoveToRecycleBin(it.Path)) failed.Add(it.Name);
            }
            catch { failed.Add(it.Name); }
        }

        _onItemsChanged();

        if (failed.Count == 0) return;
        MessageBox.Show(this,
            $"有 {failed.Count} 项删除失败（可能被占用或权限不足）：\n\n" +
            string.Join("\n", failed.Take(12)),
            "部分删除失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ───────── 消息 ─────────

    private const int WM_MOUSEWHEEL = 0x020A;

    protected override void WndProc(ref Message m)
    {
        // 滚轮转发：格子作为独立顶层窗口，未聚焦时收不到 OnMouseWheel
        if (m.Msg == WM_MOUSEWHEEL)
        {
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            _view.ScrollBy(delta);
            return;
        }
        base.WndProc(ref m);
    }

    // ───────── 桌面层嵌入 ─────────

    /// <summary>
    /// 把格子嵌入桌面层（SetParent 到 Progman）：
    /// 嵌入后格子随桌面显示/隐藏，不占任务栏、不抢焦点 —— 与 DeskGo 的格子行为一致。
    /// 传 <see cref="IntPtr.Zero"/> 则恢复为普通顶层窗口。
    /// </summary>
    public void EmbedInto(IntPtr parent)
    {
        try
        {
            long style = Native.GetWindowLongPtr(Handle, Native.GWL_STYLE).ToInt64();
            bool wasChild = (style & Native.WS_CHILD) != 0;

            if (parent == IntPtr.Zero)
            {
                // 恢复顶层：只在**确实是子窗口**时才动 —— 别误伤「有 owner 的顶层窗口」
                if (wasChild)
                {
                    Native.SetParent(Handle, IntPtr.Zero);
                    Native.ShowWindow(Handle, Native.SW_SHOW);
                }
            }
            else
            {
                // ⚠ 坐标系：模型里存的是**屏幕坐标**（用户拖出来的位置），
                //   而子窗口的 SetWindowPos 用的是**父窗口客户区坐标**。
                //   Progman 的客户区原点并不在 (0,0)（本机实测 (0,-678)），
                //   直接传屏幕坐标会把格子整体挪出屏幕 —— 所以先取原点再相减。
                var origin = new Native.POINT(0, 0);
                Native.ClientToScreen(parent, ref origin);

                Native.SetParent(Handle, parent);
                Native.SetWindowPos(Handle, IntPtr.Zero, _box.X - origin.X, _box.Y - origin.Y,
                    Width, Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

                Native.GetWindowRect(Handle, out var er);
                UlwTrace($"EmbedInto: 父=0x{parent.ToInt64():X} 客户区原点=({origin.X},{origin.Y}) " +
                         $"模型=({_box.X},{_box.Y}) 落点=({er.Left},{er.Top})");
            }
        }
        catch { /* 嵌入失败不影响基本功能 */ }

        // 换父窗口之后窗口的屏幕位置会变，重画一次让底层位图跟上。
        RenderLayered(_view);
    }

    // ───────── 辅助 ─────────

    /// <summary>各内置格子的理想列数（对齐参考图：APP 5 列 / 文件夹 3 列 / 文件 3 列）</summary>
    private static int IdealColumnsOf(int boxId) => boxId switch
    {
        BoxIds.Apps => 5,
        BoxIds.MyFolders => 3,
        BoxIds.MyFiles => 3,
        BoxIds.System => 3,      // 此电脑 / 回收站 / 网络 —— 正好一行
        _ => 4,
    };

    private static Point ClampToScreen(Point p, Size size)
    {
        var wa = Screen.FromPoint(p).WorkingArea;
        int x = Math.Max(wa.Left, Math.Min(p.X, wa.Right - size.Width));
        int y = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - Math.Min(size.Height, wa.Height)));
        return new Point(x, y);
    }
}

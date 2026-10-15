using System.Drawing.Drawing2D;
using System.Drawing.Text;
using DeskLite.Core;
using DeskLite.Win32;

namespace DeskLite.UI;

/// <summary>
/// 格子内容视图 —— 完全自绘，样式对齐腾讯桌面管家的格子：
///   · 半透明深色圆角面板 + 1px 细描边（无阴影、无渐变，保持轻量）
///   · 标题**水平居中**，与内容同一底色（没有独立标题栏色块），仅在悬停时露出折叠箭头
///   · 图标网格：图标下方居中两行文字，超长自动省略号（对齐参考图的 "Visual Studio Code"）
///   · 悬停 / 选中使用极低透明度的白色圆角块，不抢图标视觉
///   · 格子尺寸**贴合内容**（列数按格子类型固定，行数随项数），与参考图一致
///
/// 交互：单击选中 · 双击打开 · 右键菜单 · F2 就地改名 · Delete 删除
///       滚轮滚动 · 拖拽到别的格子 · 从资源管理器拖文件进来
/// </summary>
internal sealed class BoxView : Control
{
    // ── 度量（96 DPI 基准，实际按 DeviceDpi 缩放）──
    private const int TitleBase = 30;
    private const int PadXBase = 12;
    private const int PadBottomBase = 12;
    private const int CellExtraWBase = 38;   // 单元格宽度 = 图标 + 该值
    private const int LabelGapBase = 4;      // 图标与文字间距
    private const int CellBottomBase = 8;    // 单元格底部留白
    private const int CornerBase = 8;
    private const int ChevronBase = 16;
    private const int MinWidthBase = 132;

    /// <summary>标签最多显示两行（与参考图一致）</summary>
    public const int LabelLines = 2;

    /// <summary>内部拖拽使用的数据格式（形如 <c>盒子ID|路径</c>）</summary>
    public const string DragFormat = "DeskLite.BoxItem";

    // ── 调色板 ──
    private readonly struct Palette
    {
        public readonly Color Bg, Border, Title, Label, Hover, HoverEdge, Sel, SelEdge, Chevron;
        public readonly bool Light;

        public Palette(bool light, Color bg, Color border, Color title, Color label,
                       Color hover, Color hoverEdge, Color sel, Color selEdge, Color chevron)
        {
            Light = light; Bg = bg; Border = border; Title = title;
            Label = label; Hover = hover; HoverEdge = hoverEdge;
            Sel = sel; SelEdge = selEdge; Chevron = chevron;
        }
    }

    // ── 悬停 / 选中配色 ──
    //
    // 早期用的是 8% / 15% 的纯白叠加，在实际的半透明底板上几乎看不出来，
    // 用户反馈「悬停和点击感觉没选中」。现在改成资源管理器那套：
    //   · 悬停：一层淡白（暗色）/ 淡黑（亮色），刚刚好能看出「这块亮了一点」
    //   · 选中：半透明的蓝色块 + 明显描边 —— 蓝色是 Windows 里「选中」的通用语义，
    //     在灰底 / 深色玻璃底上都一眼可辨（腾讯桌面整理也是这个路子）
    private static readonly Palette DarkPalette = new(
        false,
        bg: Color.FromArgb(18, 18, 24),
        border: Color.FromArgb(34, 255, 255, 255),
        title: Color.FromArgb(245, 245, 250),
        label: Color.FromArgb(242, 242, 248),
        hover: Color.FromArgb(36, 255, 255, 255),
        hoverEdge: Color.FromArgb(46, 255, 255, 255),
        sel: Color.FromArgb(86, 64, 148, 255),
        selEdge: Color.FromArgb(190, 130, 190, 255),
        chevron: Color.FromArgb(170, 170, 185));

    private static readonly Palette LightPalette = new(
        true,
        bg: Color.FromArgb(250, 250, 252),
        border: Color.FromArgb(26, 0, 0, 0),
        title: Color.FromArgb(30, 30, 38),
        label: Color.FromArgb(46, 46, 54),
        hover: Color.FromArgb(28, 0, 0, 0),
        hoverEdge: Color.FromArgb(40, 0, 0, 0),
        sel: Color.FromArgb(72, 0, 110, 230),
        selEdge: Color.FromArgb(160, 0, 90, 200),
        chevron: Color.FromArgb(120, 120, 132));

    // ── 状态 ──
    private readonly List<DiscoveredItem> _items = new();
    private IconCache _icons;
    private AppSettings _settings;

    private string _title = "";
    private bool _collapsed;
    private int _iconSize = 48;

    private int _hover = -1;
    private int _scroll;
    private bool _titleHover;

    // ── 选择（单选只是「集合里恰好一个」的特例）──
    //
    // _sel 是选中集合，_anchor 是最后一次**不带修饰键**点击落下的锚点，
    // Shift 范围选择从它开始算。这样重命名 / 右键菜单 / 删除这些既有入口
    // 只要改用 Primary，就自动对多选也成立。
    private readonly SortedSet<int> _sel = new();
    private int _anchor = -1;

    // ── 框选（在空白处按下拖动拉出的橡皮筋）──
    private bool _banding;
    private bool _bandAdditive;
    private Point _bandStart;
    private Rectangle _band;
    private List<int> _bandHit = new();
    private readonly HashSet<int> _bandBase = new();

    // ── 格子内拖动排序：落点目标格（-1 = 不显示插入指示）──
    private int _dropIndex = -1;

    // 计算出的度量
    private int _pad, _padBottom, _titleH, _labelLine, _cellW, _cellH, _cols, _contentH;

    // 拖拽
    private bool _mouseDown;
    private Point _downPt;
    private int _downIndex = -1;
    private bool _dragStarted;

    // 就地改名（独立小窗，原因见 RenameBox 的注释）
    private RenameBox? _renameBox;

    private Font? _fTitle, _fLabel;

    /// <summary>诊断用：绘制/布局路径的埋点（正常运行时为 null，零开销）</summary>
    internal static Action<string>? Trace;
    internal static void T(string tag) { if (Trace != null) { try { Trace("~" + tag); } catch { } } }
    private bool _tracePainted;

    // ── 诊断开关（默认全开；仅在排查内存构成时用环境变量关掉某一项）──
    private static readonly bool DiagNoIcon = Environment.GetEnvironmentVariable("DESKLITE_NOICON") == "1";
    private static readonly bool DiagNoText = Environment.GetEnvironmentVariable("DESKLITE_NOTEXT") == "1";
    private static readonly bool DiagNoFill = Environment.GetEnvironmentVariable("DESKLITE_NOFILL") == "1";

    // ── 事件 ──
    public event Action<DiscoveredItem>? ItemActivated;
    public event Action<DiscoveredItem?>? DeleteRequested;
    public event Action<IReadOnlyList<DiscoveredItem>>? DeleteSelectedRequested;
    public event Action<DiscoveredItem?, Point>? ContextRequested;
    public event Action? CollapseToggled;
    public event Action<IReadOnlyList<string>, int>? ItemsMovedToBox;   // 批量跨格移动（路径, 目标格子）
    public event Action<IReadOnlyList<string>, int>? ReorderRequested;  // 格子内排序（路径, 落点位次）
    public event Action<IReadOnlyList<string>>? FilesDropped;

    public BoxView(int boxId, IconCache icons, AppSettings settings)
    {
        BoxId = boxId;
        _icons = icons;
        _settings = settings;
        _iconSize = Math.Clamp(settings.IconSize, 16, 128);

        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        SetStyle(ControlStyles.StandardDoubleClick, true);

        AllowDrop = true;
        TabStop = true;
        BackColor = Color.Black;   // 占位，实际由 OnPaint 全量绘制
    }

    // ───────── 公开属性 ─────────

    public int BoxId { get; }

    /// <summary>该格子的理想列数（对齐参考图：APP 5 列、文件夹/文件 3 列）</summary>
    public int IdealColumns { get; set; } = 4;

    /// <summary>
    /// 由 <see cref="BoxForm"/> 在鼠标贴近格子边缘时设置的光标（null = 用默认光标）。
    ///
    /// 为什么需要这个：<see cref="OnMouseMove"/> 自己会设置 <c>Cursor</c>，
    /// 而它跑在 <c>MouseMove</c> 事件**之后**，会把外壳层刚设的光标覆盖掉。
    /// 所以边缘光标必须由视图自己参与决策。
    /// </summary>
    public Cursor? EdgeCursor { get; set; }

    /// <summary>
    /// 由 <see cref="BoxForm"/> 在边缘拖拽调整尺寸时置位，
    /// 让视图跳过图标命中 / 选中 / 拖拽逻辑 —— 否则按下边缘会同时选中图标。
    /// </summary>
    public bool SuppressMouse { get; set; }

    public string Title
    {
        get => _title;
        set
        {
            var v = value ?? "";
            if (string.Equals(_title, v, StringComparison.Ordinal)) return;   // 值没变就不重排/重绘
            _title = v;
            Relayout();
            Repaint();
        }
    }

    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;   // 值没变就不重绘（SetItems 每次都会回灌一次折叠态）
            _collapsed = value;
            Repaint();
        }
    }

    public int TitleHeight => _titleH;

    /// <summary>诊断用：当前实际排布列数</summary>
    internal int CurrentColumns => _cols;

    /// <summary>诊断用：内容总高（含超出可视区的部分）</summary>
    internal int CurrentContentHeight => _contentH;

    /// <summary>诊断用：单元格尺寸</summary>
    internal Size CurrentCellSize => new(_cellW, _cellH);

    /// <summary>诊断用：是否正处于就地改名状态</summary>
    internal bool IsRenaming => _renameBox != null;

    /// <summary>诊断用：选中第 index 项并开始改名（越界则忽略）</summary>
    internal void ProbeRename(int index = 0)
    {
        if (index >= 0 && index < _items.Count) BeginRename(index);
    }

    public int IconSize
    {
        get => _iconSize;
        set
        {
            var v = Math.Clamp(value, 16, 128);
            if (v == _iconSize) return;
            _iconSize = v;
            Relayout();
            Repaint();
        }
    }

    /// <summary>主选中项 —— 重命名 / 属性 / 菜单标题都以它为准</summary>
    public DiscoveredItem? Selected
    {
        get
        {
            int p = Primary;
            return p >= 0 && p < _items.Count ? _items[p] : null;
        }
    }

    /// <summary>当前选中的全部项（按格子内顺序）</summary>
    public IReadOnlyList<DiscoveredItem> SelectedItems
        => _sel.Where(i => i >= 0 && i < _items.Count).Select(i => _items[i]).ToList();

    /// <summary>选中项数量</summary>
    public int SelectedCount => _sel.Count;

    /// <summary>非虚拟项的数量（批量操作要用它判断「有没有可操作的对象」）</summary>
    public int SelectedRealCount => SelectedItems.Count(i => !i.IsVirtual);

    public int ItemCount => _items.Count;

    /// <summary>当前项的路径顺序（格子内）</summary>
    internal IReadOnlyList<string> ItemOrder => _items.Select(i => i.Path).ToList();

    // ── 选择集维护 ──

    private int Primary => _anchor >= 0 && _sel.Contains(_anchor)
        ? _anchor
        : (_sel.Count > 0 ? _sel.Min : -1);

    private void SelectOnly(int index)
    {
        _sel.Clear();
        if (index >= 0 && index < _items.Count) _sel.Add(index);
        _anchor = _sel.Count > 0 ? index : -1;
    }

    private void SelectRange(int to)
    {
        if (_anchor < 0 || _anchor >= _items.Count) { SelectOnly(to); return; }
        _sel.Clear();
        int a = Math.Min(_anchor, to), b = Math.Max(_anchor, to);
        for (int i = a; i <= b; i++) _sel.Add(i);
    }

    private void ToggleSelect(int index)
    {
        if (!_sel.Remove(index)) { _sel.Add(index); _anchor = index; }
        else if (_anchor == index) _anchor = _sel.Count > 0 ? _sel.Min : -1;
    }

    /// <summary>
    /// 按路径顺序就地重排，不重建列表、不重置滚动位置。
    /// 拖动排序一帧一次调用，走 SetItems 那套（重建 + 重置滚动）太重。
    /// </summary>
    internal void ApplyOrder(IReadOnlyList<string> paths)
    {
        var map = new Dictionary<string, DiscoveredItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in _items) map[it.Path] = it;

        var next = new List<DiscoveredItem>(paths.Count);
        foreach (var p in paths)
            if (map.TryGetValue(p, out var it)) next.Add(it);

        // 兜底：paths 万一漏项（理论上不会）补到末尾 —— 绝不丢项
        foreach (var it in _items)
            if (!next.Contains(it)) next.Add(it);

        var keep = new HashSet<string>(
            SelectedItems.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);

        _items.Clear();
        _items.AddRange(next);

        _sel.Clear();
        for (int i = 0; i < _items.Count; i++)
            if (keep.Contains(_items[i].Path)) _sel.Add(i);
        _anchor = _sel.Count > 0 ? _sel.Min : -1;

        Relayout();
        Repaint();
    }

    private Palette Pal => _settings.IsLight ? LightPalette : DarkPalette;

    /// <summary>面板底色（窗口壳层用它填充客户区，避免圆角处出现异色）</summary>
    public Color BackgroundColor => Pal.Bg;

    /// <summary>标题字色（壳层做「标题就地改名」时用来配编辑框）</summary>
    internal Color TitleColor => Pal.Title;

    private float S => DeviceDpi / 96f;

    /// <summary>标题字体（壳层做「标题就地改名」时复用，避免多建一份 Font）</summary>
    internal Font TitleFont => _fTitle ??= new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular);
    private Font LabelFont => _fLabel ??= new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular);

    /// <summary>
    /// 请求重绘 —— <b>分层宿主下不要改用 <c>Invalidate()</c></b>。
    ///
    /// 格子窗口的外观完全来自 <c>UpdateLayeredWindow</c> 提交的 32bpp 位图，
    /// 系统不会给这种窗口的子控件派发 <c>WM_PAINT</c>：本机实测即使从外部
    /// <c>RedrawWindow(hwnd, RDW_INVALIDATE|RDW_UPDATENOW)</c> 强制失效，
    /// <see cref="OnPaint"/> 依然一次都不会被调用。
    /// 于是 <c>Invalidate()</c> 等于什么都没做 —— 表现就是用户反馈的
    /// 「鼠标悬停没有效果」「点中了也看不出来」「内容灌进去了还是空格子」。
    ///
    /// 所以这里直接驱动宿主重新提交位图（同步、开销就是一次 GDI+ 重绘）；
    /// 只有非分层回退路径才走普通的 <c>Invalidate()</c>。
    /// </summary>
    internal void Repaint()
    {
        if (Parent is BoxForm { UsesLayeredWindow: true } host) { host.RenderLayered(this); return; }
        Invalidate();
    }

    // ───────── 内容 ─────────

    public void SetItems(IReadOnlyList<DiscoveredItem> items)
    {
        T($"SetItems 开始（{items.Count} 项）");
        if (_renameBox != null) CommitEdit();

        // 选择按**路径**保留：刷新只是重建列表，用户手上的选择不该被清掉
        var keep = new HashSet<string>(
            SelectedItems.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);

        _items.Clear();
        _items.AddRange(items);

        _sel.Clear();
        for (int i = 0; i < _items.Count; i++)
            if (keep.Contains(_items[i].Path)) _sel.Add(i);
        _anchor = _sel.Count > 0 ? _sel.Min : -1;

        _scroll = 0;
        _hover = -1;
        _dropIndex = -1;
        _banding = false;
        _band = Rectangle.Empty;
        Relayout();
        Repaint();
        T("SetItems 结束");
    }

    public void ApplySettings(AppSettings s)
    {
        _settings = s;
        IconSize = Math.Clamp(s.IconSize, 16, 128);
        Relayout();
        Repaint();
    }

    /// <summary>
    /// 更换图标缓存（设置里改了图标尺寸后，MainContext 会建一个新尺寸的缓存）。
    /// 视图只持有引用，不拥有缓存 —— 所以这里直接换，旧的由调用方释放。
    /// </summary>
    public void SetIconCache(IconCache cache)
    {
        _icons = cache;
        Repaint();
    }

    /// <summary>贴合格子内容所需的尺寸（宽 = 列数决定，高 = 行数决定，超出工作区 80% 则滚动）</summary>
    public Size MeasureContent()
    {
        T("MeasureContent 开始");
        Relayout();

        int count = Math.Max(1, _items.Count);
        int cols = Math.Min(IdealColumns, count);
        int rows = Math.Max(1, (count + cols - 1) / cols);

        int w = _pad * 2 + cols * _cellW;
        int titleW = TextRenderer.MeasureText(_title, TitleFont).Width + (int)(ChevronBase * S * 2.4);
        w = Math.Max(w, titleW);
        w = Math.Max(w, (int)(MinWidthBase * S));

        int h = _titleH + _padBottom + rows * _cellH;

        var wa = Screen.FromPoint(Location).WorkingArea;
        int maxH = (int)(wa.Height * 0.8);
        if (h > maxH) h = maxH;

        T($"MeasureContent 结束 {w}×{h}");
        return new Size(w, h);
    }

    /// <summary>内容高度超出可视区时返回最大滚动量</summary>
    private int MaxScroll => Math.Max(0, _contentH - (Height - _titleH - _padBottom));

    // ───────── 布局 ─────────

    private void Relayout()
    {
        float s = S;
        _pad = (int)Math.Round(PadXBase * s);
        _padBottom = (int)Math.Round(PadBottomBase * s);
        _titleH = (int)Math.Round(TitleBase * s);
        _labelLine = (int)Math.Ceiling(LabelFont.GetHeight());

        _cellW = _iconSize + (int)Math.Round(CellExtraWBase * s);
        _cellH = _iconSize
               + (int)Math.Round(LabelGapBase * s)
               + _labelLine * LabelLines
               + (int)Math.Round(CellBottomBase * s);

        int count = Math.Max(1, _items.Count);

        // 列数由**实际可用宽度**反推，而不是写死 IdealColumns。
        //
        // 这样两件事同时成立：
        //   · 自动尺寸时，窗口宽度 = 2*pad + min(IdealColumns,count)*cellW，
        //     反推回来正好还是 min(IdealColumns,count) —— 与旧行为完全一致；
        //   · 用户手工把格子拉宽 / 拉窄后，列数自动跟随，图标重排。
        //     （旧写法写死 IdealColumns，拉宽只会让图标之间出现大片空隙。）
        _cols = ColumnsForWidth(Width);
        _contentH = (int)Math.Ceiling(count / (double)_cols) * _cellH;
    }

    /// <summary>按给定宽度能排下的列数（至少 1 列，至多 <see cref="MaxColumns"/> 列）</summary>
    private int ColumnsForWidth(int width)
    {
        int avail = width - _pad * 2;
        int cols = avail / Math.Max(1, _cellW);
        if (cols < 1) cols = 1;
        if (cols > MaxColumns) cols = MaxColumns;
        return cols;
    }

    /// <summary>列数上限 —— 格子被拉得极宽时也不至于排成一条长龙</summary>
    public const int MaxColumns = 12;

    /// <summary>窗口宽度下限（一列 + 左右内边距）</summary>
    public int MinWindowWidth => _pad * 2 + _cellW;

    /// <summary>窗口高度下限（标题栏 + 一行 + 底部内边距）</summary>
    public int MinWindowHeight => _titleH + _padBottom + _cellH;

    private Rectangle CellRect(int index)
    {
        int col = index % _cols;
        int row = index / _cols;
        return new Rectangle(
            _pad + col * _cellW,
            _titleH + row * _cellH - _scroll,
            _cellW,
            _cellH);
    }

    private Rectangle ChevronRect()
    {
        int d = (int)Math.Round(ChevronBase * S);
        return new Rectangle(
            Width - _pad - d,
            (_titleH - d) / 2,
            d + 4,
            d + 4);
    }

    private int HitTest(Point p)
    {
        if (p.Y < _titleH) return -1;
        for (int i = 0; i < _items.Count; i++)
            if (CellRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout();
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
    }

    // ───────── 绘制 ─────────

    protected override void OnPaintBackground(PaintEventArgs pevent) { /* 全量自绘 */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var host = Parent as BoxForm;

        // 分层宿主：内容由 BoxForm 用 UpdateLayeredWindow 提交的 32bpp 位图决定。
        // 子控件若自己往窗口 DC 上画，就会用不透明像素盖住那张位图的半透明底板，
        // 所以这里只负责「触发渲染」，真正绘制在 PaintInto。
        if (host is { UsesLayeredWindow: true })
        {
            host.RenderLayered(this);
            return;
        }

        // 非分层宿主（离屏样式预览 --uishot、诊断截图 DrawToBitmap 等）：
        // 没有窗口位图可依赖，就老老实实画到传进来的画布上。
        PaintInto(e.Graphics);
    }

    /// <summary>
    /// 把整个格子绘制到指定画布（画布尺寸 = 控件尺寸）。
    ///
    /// 与旧 <c>OnPaint</c> 的唯一实质差别在底板：现在填的是**带 alpha 的颜色**，
    /// 而四角圆角也改成在位图里裁出（原来靠窗口 Region）。
    /// 图标 / 文字 / 悬停块全部照常合成，因此它们保持完全不透明 ——
    /// 这正是「只有底板透明、图标保持清晰」的实现方式。
    /// </summary>
    internal void PaintInto(Graphics g)
    {
        bool trace = !_tracePainted;
        if (trace) { _tracePainted = true; T($"PaintInto 开始 {Width}×{Height}（{_items.Count} 项）"); }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var pal = Pal;
        int radius = (int)Math.Round(CornerBase * S);

        using (var round = Shapes.RoundedPath(new Rectangle(0, 0, Width, Height), radius))
        {
            // 画布已经是透明底，四角裁出来就是 alpha=0，圆角自然平滑
            g.SetClip(round);

            if (!DiagNoFill)
            {
                // ★ 底板透明度的来源。整块底板一次性带 alpha 填充，
                //   之后画的图标 / 文字 / 悬停块用各自的 alpha 正常叠加。
                int a = (int)Math.Round(Math.Clamp(_settings.GlassOpacity, 10, 100) * 2.55);
                using (var bg = new SolidBrush(Color.FromArgb(a, pal.Bg)))
                    g.FillRectangle(bg, new Rectangle(0, 0, Width, Height));

                using var pen = new Pen(pal.Border, 1f);
                g.DrawPath(pen, round);
            }

            g.ResetClip();
        }

        // 标题（水平居中）
        DrawTitle(g, pal);

        if (_collapsed) return;

        // 网格
        var clip = new Rectangle(0, _titleH, Width, Height - _titleH);
        g.SetClip(clip);

        for (int i = 0; i < _items.Count; i++)
        {
            var cell = CellRect(i);
            if (cell.Bottom < clip.Top || cell.Top > clip.Bottom) continue;
            if (trace && i == 0) T("  首个 DrawCell 前");
            DrawCell(g, pal, i, cell);
            if (trace && i == 0) T("  首个 DrawCell 后");
        }

        // 空格子（尤其是刚新建的）给一句提示，否则用户不知道它能装什么
        if (_items.Count == 0)
        {
            var hintRect = new Rectangle(0, _titleH, Width, Height - _titleH - _padBottom);
            DrawText(g, "把文件或文件夹拖到这里", LabelFont, hintRect,
                Color.FromArgb(pal.Light ? 110 : 105, pal.Label), 1, vCenter: true);
        }

        DrawDropIndicator(g);
        DrawBand(g, pal);

        g.ResetClip();
        DrawScrollHint(g, pal);
        if (trace) T("PaintInto 结束");
    }

    /// <summary>格子内拖动排序的落点提示：把松手后会顶替的那一格圈出来</summary>
    private void DrawDropIndicator(Graphics g)
    {
        if (_dropIndex < 0 || _dropIndex >= _items.Count) return;

        var r = Rectangle.Inflate(CellRect(_dropIndex), -(int)(2 * S), -(int)(1 * S));
        if (r.Width <= 0 || r.Height <= 0) return;

        using var path = Shapes.RoundedPath(r, (int)Math.Round(6 * S));
        using (var brush = new SolidBrush(Color.FromArgb(52, 64, 148, 255)))
            g.FillPath(brush, path);
        using var pen = new Pen(Color.FromArgb(235, 120, 180, 255), 2f * Math.Max(1f, S));
        g.DrawPath(pen, path);
    }

    /// <summary>框选橡皮筋</summary>
    private void DrawBand(Graphics g, Palette pal)
    {
        if (!_banding || _band.Width <= 0 || _band.Height <= 0) return;

        using (var brush = new SolidBrush(Color.FromArgb(pal.Light ? 38 : 44, 64, 148, 255)))
            g.FillRectangle(brush, _band);
        using var pen = new Pen(Color.FromArgb(205, 100, 165, 255), Math.Max(1f, S));
        g.DrawRectangle(pen, _band);
    }

    private void DrawTitle(Graphics g, Palette pal)
    {
        var band = new Rectangle(0, 0, Width, _titleH);
        DrawText(g, _title, TitleFont, band, pal.Title, 1, vCenter: true);

        // 悬停时才露出折叠箭头（保持默认视觉干净）
        if (!_titleHover) return;

        var r = ChevronRect();
        using var pen = new Pen(pal.Chevron, Math.Max(1.4f, 1.6f * S))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        float cx = r.Left + r.Width / 2f;
        float cy = r.Top + r.Height / 2f;
        float a = 3.4f * S;

        if (_collapsed)
        {
            // ▸ 向右
            g.DrawLines(pen, new[]
            {
                new PointF(cx - a * 0.45f, cy - a),
                new PointF(cx + a * 0.55f, cy),
                new PointF(cx - a * 0.45f, cy + a),
            });
        }
        else
        {
            // ▾ 向下
            g.DrawLines(pen, new[]
            {
                new PointF(cx - a, cy - a * 0.45f),
                new PointF(cx, cy + a * 0.55f),
                new PointF(cx + a, cy - a * 0.45f),
            });
        }
    }

    private void DrawCell(Graphics g, Palette pal, int index, Rectangle cell)
    {
        // 悬停 / 选中底
        bool selected = _sel.Contains(index);
        if (selected || index == _hover)
        {
            var bgRect = Rectangle.Inflate(cell, -(int)(2 * S), -(int)(1 * S));
            using var path = Shapes.RoundedPath(bgRect, (int)Math.Round(6 * S));

            using (var brush = new SolidBrush(selected ? pal.Sel : pal.Hover))
                g.FillPath(brush, path);

            // 选中给一圈描边；悬停也给一条更淡的，让「指到哪一块」看得清
            using var pen = new Pen(selected ? pal.SelEdge : pal.HoverEdge,
                selected ? 1.4f * Math.Max(1f, S) : 1f);
            g.DrawPath(pen, path);
        }

        var it = _items[index];

        // 图标：水平居中，单元格顶部
        int ix = cell.Left + (cell.Width - _iconSize) / 2;
        int iy = cell.Top + (int)(2 * S);
        if (!DiagNoIcon)
        try
        {
            // 虚拟项（此电脑 / 回收站 / 网络）按 ::{CLSID} 伪路径传入，
            // GetIcon 内部会自动走「解析名 → PIDL」的取图标路径。
            var img = _icons.GetIcon(it.Path);

            // ⚠ 清晰度关键点：缓存的位图尺寸**就是** _iconSize，属于 1:1 贴图。
            // 如果还走带目标矩形的 DrawImage，GDI+ 会按当前 InterpolationMode
            // （HighQualityBicubic）重采样一遍 —— 明明 1:1 却被双三次滤了一道，
            // 结果就是边缘发虚、细节被抹平，看起来「不清晰」。
            // 因此尺寸相同直接 DrawImageUnscaled 原样搬（零重采样），
            // 只有尺寸不一致（DPI 变化等）时才走高质量缩放路径。
            if (img.Width == _iconSize && img.Height == _iconSize)
            {
                g.DrawImageUnscaled(img, ix, iy);
            }
            else
            {
                var old = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, new Rectangle(ix, iy, _iconSize, _iconSize));
                g.InterpolationMode = old;
            }
        }
        catch
        {
            // 图标失败不影响文字
        }

        // 文件名：图标下方居中，最多两行，超出省略号
        if (DiagNoText) return;

        var labelRect = new Rectangle(
            cell.Left + (int)(2 * S),
            iy + _iconSize + (int)Math.Round(LabelGapBase * S),
            cell.Width - (int)(4 * S),
            _labelLine * LabelLines + (int)(4 * S));

        DrawText(g, it.Name, LabelFont, labelRect, pal.Label, LabelLines);
    }

    /// <summary>
    /// 居中绘制文本。
    ///
    /// 这里用 GDI+ 的 <c>DrawString</c>（灰度抗锯齿）而不是 GDI 的 <c>TextRenderer</c>：
    /// 格子窗口带 <c>Form.Opacity</c> 整体 alpha，ClearType 的次像素渲染在这种
    /// 半透明合成下会在字形边缘产生彩色毛边，灰度抗锯齿更干净，也更贴近参考图观感。
    /// </summary>
    private static void DrawText(Graphics g, string text, Font font, Rectangle rect,
        Color color, int lines, bool vCenter = false)
    {
        if (string.IsNullOrEmpty(text)) return;

        int lineH = (int)Math.Ceiling(font.GetHeight(g.DpiY));
        var box = new Rectangle(rect.X, rect.Y, rect.Width,
            vCenter ? rect.Height : Math.Min(rect.Height, lineH * lines + 2));

        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat(StringFormatFlags.LineLimit)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = vCenter ? StringAlignment.Center : StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            HotkeyPrefix = HotkeyPrefix.None,
        };
        g.DrawString(text, font, brush, box, fmt);
    }

    /// <summary>内容溢出时，在右侧画一条极细的滚动指示</summary>
    private void DrawScrollHint(Graphics g, Palette pal)
    {
        int max = MaxScroll;
        if (max <= 0) return;

        int trackTop = _titleH + 2;
        int trackH = Height - trackTop - _padBottom;
        if (trackH <= 8) return;

        int thumbH = Math.Max(24, (int)(trackH * (trackH / (double)_contentH)));
        int thumbY = trackTop + (int)((trackH - thumbH) * (_scroll / (double)max));

        using var brush = new SolidBrush(Color.FromArgb(34, pal.Light ? Color.Black : Color.White));
        g.FillRectangle(brush, new Rectangle(Width - 5, thumbY, 3, thumbH));
    }

    // ───────── 鼠标 ─────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // 边缘拖拽调整尺寸期间：只让外壳层处理，视图不参与
        if (SuppressMouse) return;

        // ① 框选进行中：只更新橡皮筋与选择集
        if (_banding) { UpdateBand(e.Location); return; }

        // ② 拖拽启动判定（载荷带上整个选择集，多选就能一起拖走）
        if (_mouseDown && !_dragStarted && _downIndex >= 0)
        {
            var dx = Math.Abs(e.X - _downPt.X);
            var dy = Math.Abs(e.Y - _downPt.Y);
            if (dx > 6 || dy > 6)
            {
                _dragStarted = true;
                StartDrag();
                _mouseDown = false;
                return;
            }
        }

        bool wasTitleHover = _titleHover;
        _titleHover = e.Y < _titleH && ChevronRect().Contains(e.Location);

        int h = HitTest(e.Location);
        if (h != _hover || _titleHover != wasTitleHover)
        {
            _hover = h;
            Repaint();
        }

        Cursor = EdgeCursor                    // 边缘调尺寸优先
               ?? (_titleHover ? Cursors.Hand : Cursors.Default);
    }

    /// <summary>
    /// 开始拖拽。载荷格式 <c>盒子ID|路径1\n路径2…</c> ——
    /// 落在**别的**格子就是（批量）换格，落回本格子就是格子内排序。
    /// </summary>
    private void StartDrag()
    {
        // 拖的项不在选择集里 → 先把选择收敛成它自己（与资源管理器一致）
        if (!_sel.Contains(_downIndex)) { SelectOnly(_downIndex); Repaint(); }

        var picked = SelectedItems.Count > 0
            ? SelectedItems
            : new List<DiscoveredItem> { _items[_downIndex] };

        var data = new DataObject();
        data.SetData(DragFormat, BoxId + "|" + string.Join('\n', picked.Select(i => i.Path)));
        data.SetData(DataFormats.Text, _items[_downIndex].Path);
        try { DoDragDrop(data, DragDropEffects.Move | DragDropEffects.Copy); }
        catch { /* 拖拽失败忽略 */ }
    }

    /// <summary>更新框选：算出橡皮筋矩形，并把与之相交的图标纳入选择</summary>
    private void UpdateBand(Point cur)
    {
        int x1 = Math.Min(_bandStart.X, cur.X), x2 = Math.Max(_bandStart.X, cur.X);
        int y1 = Math.Min(_bandStart.Y, cur.Y), y2 = Math.Max(_bandStart.Y, cur.Y);

        // 夹到内容区，别让橡皮筋爬到标题栏上
        y1 = Math.Max(y1, _titleH);
        if (y2 < y1) y2 = y1;

        var next = Rectangle.FromLTRB(x1, y1, x2, y2);

        var hit = new List<int>();
        for (int i = 0; i < _items.Count; i++)
            if (CellRect(i).IntersectsWith(next)) hit.Add(i);

        // 只有矩形或命中集合真的变了才重画 —— 鼠标每移动一像素就重绘整张位图太贵
        bool changed = !next.Equals(_band) || !hit.SequenceEqual(_bandHit);
        _band = next;
        _bandHit = hit;

        _sel.Clear();
        foreach (var i in _bandBase) _sel.Add(i);
        foreach (var i in hit) _sel.Add(i);
        if (hit.Count > 0) _anchor = hit[0];

        if (changed) Repaint();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1 || _titleHover)
        {
            _hover = -1;
            _titleHover = false;
            Repaint();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        // 外壳层判定这是「拖边缘调尺寸」→ 视图只管让开。
        // 注意：base.OnMouseDown 已经把事件抛给 BoxForm，所以这里的标志是已更新过的。
        if (SuppressMouse) return;

        Focus();

        if (e.Button == MouseButtons.Right)
        {
            if (_renameBox != null) CommitEdit();
            int idx = HitTest(e.Location);

            // 右键落在已选中的项上 → 保留整个选择集（批量操作）；
            // 落在没选中的项上 → 收敛成它自己。
            if (idx >= 0 && !_sel.Contains(idx)) { SelectOnly(idx); Repaint(); }
            else if (idx < 0 && _sel.Count == 0) Repaint();

            ContextRequested?.Invoke(idx >= 0 ? _items[idx] : null, PointToScreen(e.Location));
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        // 编辑框是独立小窗，点格子里的任何地方都算「结束了这次改名」
        if (_renameBox != null) CommitEdit();

        // 标题区：交给外层处理拖动 / 双击折叠
        if (e.Y < _titleH) return;

        // 折叠箭头
        if (ChevronRect().Contains(e.Location))
        {
            CollapseToggled?.Invoke();
            return;
        }

        // 双击打开
        if (e.Clicks >= 2)
        {
            int dbl = HitTest(e.Location);
            if (dbl >= 0)
            {
                SelectOnly(dbl);
                Repaint();
                ItemActivated?.Invoke(_items[dbl]);
            }
            return;
        }

        int index = HitTest(e.Location);
        bool ctrl = (ModifierKeys & Keys.Control) != 0;
        bool shift = (ModifierKeys & Keys.Shift) != 0;

        // 空白处按下 → 开始框选（不是拖图标）
        if (index < 0)
        {
            _banding = true;
            _bandAdditive = ctrl || shift;
            _bandStart = e.Location;
            _band = Rectangle.Empty;
            _bandHit = new List<int>();
            _bandBase.Clear();
            if (_bandAdditive) foreach (var i in _sel) _bandBase.Add(i);
            else { SelectOnly(-1); }

            _mouseDown = false;
            _downIndex = -1;
            Repaint();
            return;
        }

        // 图标上按下
        _mouseDown = true;
        _dragStarted = false;
        _downIndex = index;
        _downPt = e.Location;

        if (ctrl) ToggleSelect(index);
        else if (shift) SelectRange(index);
        else if (!_sel.Contains(index)) SelectOnly(index);
        // 已选中的图标上再按一次（不带修饰键）保持整个多选，好让它能被一起拖走

        Repaint();

        // 单击打开：多选状态下不触发，免得一按就弹出好几个
        if (index >= 0 && !_settings.DoubleClickOpen && _sel.Count == 1)
            ItemActivated?.Invoke(_items[index]);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _mouseDown = false;
        _dragStarted = false;
        _downIndex = -1;

        if (_banding)
        {
            _banding = false;
            _band = Rectangle.Empty;
            _bandHit = new List<int>();
            _bandBase.Clear();
            Repaint();
        }
    }

    /// <summary>
    /// 滚动内容。由窗口壳从 <c>WM_MOUSEWHEEL</c> 转发 —— 格子是独立顶层窗口，
    /// 未聚焦时收不到 <see cref="Control.OnMouseWheel"/>，必须走消息转发。
    /// </summary>
    public void ScrollBy(int delta)
    {
        int max = MaxScroll;
        if (max <= 0) return;

        _scroll = Math.Clamp(_scroll - delta / 3, 0, max);
        Repaint();
    }

    // ───────── 键盘 ─────────

    protected override bool IsInputKey(Keys keyData)
        => keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Enter
           || keyData is Keys.A or Keys.Escape
           || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Ctrl+A 全选 —— 多选的组成部分（不是方向键 / 快捷键那一类键盘导航）
        if (e.Control && e.KeyCode == Keys.A)
        {
            _sel.Clear();
            for (int i = 0; i < _items.Count; i++) _sel.Add(i);
            _anchor = _items.Count > 0 ? 0 : -1;
            Repaint();
            e.Handled = true;
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Escape when _sel.Count > 0:
                SelectOnly(-1);
                Repaint();
                e.Handled = true;
                break;

            case Keys.Enter when Selected != null:
                ItemActivated?.Invoke(Selected!);
                e.Handled = true;
                break;

            case Keys.F2 when Selected != null:
                BeginRename(Primary);
                e.Handled = true;
                break;

            // 多选 → 批量删除；单选走原来的单项确认
            case Keys.Delete when _sel.Count > 1:
                DeleteSelectedRequested?.Invoke(SelectedItems);
                e.Handled = true;
                break;

            case Keys.Delete when Selected != null:
                DeleteRequested?.Invoke(Selected!);
                e.Handled = true;
                break;

            case Keys.Space when Selected != null:
                CollapseToggled?.Invoke();
                e.Handled = true;
                break;
        }
    }

    // ───────── 就地改名 ─────────

    public void BeginRenameSelected() => BeginRename(Primary);

    /// <summary>
    /// 在图标下方弹出就地改名小窗。
    ///
    /// 编辑框必须落在**屏幕**坐标上：它是个独立的顶层窗口，不是本视图的子控件
    /// （原因见 <see cref="RenameBox"/>）。宽度给得比标签宽一些，长文件名才看得全，
    /// 居中于单元格。
    /// </summary>
    private void BeginRename(int index)
    {
        if (index < 0 || index >= _items.Count) return;

        SelectOnly(index);
        Repaint();

        var item = _items[index];
        var cell = CellRect(index);

        // 宽度取单元格本身：和 explorer 里就地改名一样，长名靠框内滚动看，
        // 不做「比图标还宽、压到邻居身上」的漂浮大白框。
        int w = cell.Width;
        int h = _labelLine + (int)(10 * S);
        var rect = new Rectangle(
            cell.Left + (cell.Width - w) / 2,
            cell.Top + _iconSize + (int)Math.Round(LabelGapBase * S),
            w, h);
        var screen = PointToScreen(rect.Location);

        _renameBox?.ForceClose();

        var box = new RenameBox(item.Name, new Rectangle(screen, rect.Size), LabelFont);
        _renameBox = box;

        // 回调里对比引用：期间可能已经弹了另一个编辑框（连续改名）
        box.Committed += text =>
        {
            if (ReferenceEquals(_renameBox, box)) _renameBox = null;
            if (text.Length > 0) RenameRequested?.Invoke(item, text);
            Repaint();
        };
        box.Cancelled += () =>
        {
            if (ReferenceEquals(_renameBox, box)) _renameBox = null;
            Repaint();
        };
        box.Show();
    }

    /// <summary>收尾一次进行中的改名（等同回车）。用于「点开了别的东西」这类外部打断。</summary>
    private void CommitEdit() => _renameBox?.CommitNow();

    /// <summary>改名请求（由外层真正执行文件改名）</summary>
    public event Action<DiscoveredItem, string>? RenameRequested;

    // ───────── 拖放 ─────────

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = DragEffect(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effect = DragEffect(e);

        var p = PointToClient(new Point(e.X, e.Y));
        int h = HitTest(p);

        // 从**本格子**拖出来的（同格拖动 = 排序）→ 点亮落点那一格，
        // 让用户看得见「松手会插到哪」。跨格子拖动不点亮（那是整体换格）。
        int drop = PayloadFrom(e.Data) is { } src && src.BoxId == BoxId && _items.Count > 0
            ? GridIndexAt(p)
            : -1;

        if (h != _hover || drop != _dropIndex)
        {
            _hover = h;
            _dropIndex = drop;
            Repaint();
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _hover = -1;
        _dropIndex = -1;
        Repaint();
    }

    private static DragDropEffects DragEffect(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DragFormat) == true) return DragDropEffects.Move;
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) return DragDropEffects.Copy;
        return DragDropEffects.None;
    }

    /// <summary>解析内部拖拽载荷（形如 <c>盒子ID|路径1\n路径2…</c>）；非内部拖拽返回 null</summary>
    private static (int BoxId, List<string> Paths)? PayloadFrom(IDataObject? data)
    {
        if (data?.GetDataPresent(DragFormat) != true) return null;
        if (data.GetData(DragFormat) is not string payload) return null;

        int bar = payload.IndexOf('|');
        if (bar <= 0 || !int.TryParse(payload[..bar], out int box)) return null;

        var paths = payload[(bar + 1)..]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length > 0)
            .ToList();

        return paths.Count == 0 ? null : (box, paths);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);

        int at = GridIndexAt(PointToClient(new Point(e.X, e.Y)));
        _hover = -1;
        _dropIndex = -1;
        Repaint();

        if (PayloadFrom(e.Data) is { } src)
        {
            // 落在别的格子 = （批量）换格；落回本格子 = 格子内排序
            if (src.BoxId != BoxId) ItemsMovedToBox?.Invoke(src.Paths, BoxId);
            else ReorderRequested?.Invoke(src.Paths, at);
            return;
        }

        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            FilesDropped?.Invoke(files);
        }
    }

    /// <summary>把控件坐标换算成网格位次（供拖动落点用；标题栏上松手算插到最前）</summary>
    private int GridIndexAt(Point p)
    {
        if (_items.Count == 0) return 0;

        int cw = Math.Max(1, _cellW), ch = Math.Max(1, _cellH);
        int col = Math.Clamp((p.X - _pad) / cw, 0, Math.Max(0, _cols - 1));
        int row = (p.Y - _titleH + _scroll) / ch;
        return Math.Clamp(row * _cols + col, 0, _items.Count - 1);
    }

    // ───────── 清理 ─────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fTitle?.Dispose();
            _fLabel?.Dispose();
            _renameBox?.ForceClose();
        }
        base.Dispose(disposing);
    }

}

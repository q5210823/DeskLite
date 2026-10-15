using DeskLite.Win32;

namespace DeskLite.UI;

/// <summary>
/// 格子拖动时的吸附顾问。由 <see cref="MainContext"/> 实现 —— 只有它掌握全部格子的实时矩形。
///
/// <see cref="BoxForm"/> 只负责「把我想去的位置报上去」，拿回一个修正后的左上角，
/// 不关心跟谁对齐、线画在哪里。这样吸附逻辑不用侵入窗口壳，也不会让格子之间互相持有引用。
/// </summary>
internal interface IBoxSnapper
{
    /// <summary>
    /// 请求吸附。<paramref name="selfId"/> 是要排除的自己（格子不能吸到自己身上），
    /// <paramref name="proposed"/> 是这次想要落到的窗口矩形。
    /// </summary>
    /// <returns>修正后的左上角坐标</returns>
    Point Snap(int selfId, Rectangle proposed);

    /// <summary>拖动结束 / 取消：收起参考线</summary>
    void EndSnap();
}

/// <summary>
/// 对齐参考线覆盖层 —— 一块铺满虚拟屏幕、**点击穿透**的透明窗口，
/// 只在拖动格子时画一两条线（格子与他格的边 / 中线对齐处）。
///
/// 为什么要单独开一个窗口：格子自己是分层窗口，位图被裁在窗口矩形之内，
/// 画不出「越过格子边界伸到邻格」的线。参考线必须有一个更大的画布。
///
/// 穿透与不抢焦点靠 <c>WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>，
/// 所以它就算置顶也完全不挡用户点图标、不进 Alt+Tab、不抢焦点。
/// 背景色用品红 + <c>TransparencyKey</c>，等于「整块透明，只留画上去的线」。
/// </summary>
internal sealed class SnapOverlay : Form
{
    private static readonly Color Key = Color.Magenta;
    private static readonly Color Line = Color.FromArgb(255, 0, 150, 255);

    /// <summary>当前要画的线（屏幕坐标，宽或高为 1 表示一条细线）</summary>
    private readonly List<Rectangle> _lines = new();

    public SnapOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        Enabled = false;                                   // 不参与输入，只当画布

        // TransparencyKey 属于「分层窗口」那套，必须先允许透明；
        // 而 AllowTransparency 又要求窗口无边框 —— 顺序不能颠倒。
        try
        {
            AllowTransparency = true;
            BackColor = Key;
            TransparencyKey = Key;
        }
        catch
        {
            // 个别环境不接受键色透明：退化成「不带透明」也比抛异常强，
            // 参考线依然可见，只是底面会被键色铺满 —— 由外层兜底关掉这个开关。
            TransparencyKey = Color.Empty;
        }

        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);

        Bounds = SystemInformation.VirtualScreen;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TRANSPARENT          // 鼠标穿透
                        | Native.WS_EX_NOACTIVATE           // 不抢焦点
                        | Native.WS_EX_TOOLWINDOW;          // 不进 Alt+Tab
            cp.Style &= ~(Native.WS_CAPTION | Native.WS_BORDER | Native.WS_DLGFRAME
                          | Native.WS_THICKFRAME | Native.WS_SYSMENU);
            return cp;
        }
    }

    /// <summary>背景由品红键色决定，不用画</summary>
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Key);
        if (_lines.Count == 0) return;

        using var pen = new Pen(Line, 1f);
        foreach (var r in _lines)
            e.Graphics.DrawRectangle(pen, r.X, r.Y,
                Math.Max(1, r.Width - 1), Math.Max(1, r.Height - 1));
    }

    /// <summary>
    /// 更新参考线（屏幕坐标）。两个方向各自可为 null，表示那个方向没对齐。
    /// </summary>
    /// <param name="vertical">竖线：X + 起止 Y</param>
    /// <param name="horizontal">横线：Y + 起止 X</param>
    public void Update((int X, int Top, int Bottom)? vertical,
                       (int Y, int Left, int Right)? horizontal)
    {
        var old = _lines.ToArray();
        _lines.Clear();

        if (vertical is { } v)
            _lines.Add(new Rectangle(v.X, v.Top, 1, Math.Max(1, v.Bottom - v.Top)));
        if (horizontal is { } h)
            _lines.Add(new Rectangle(h.Left, h.Y, Math.Max(1, h.Right - h.Left), 1));

        if (_lines.Count == 0) { Hide(); return; }

        if (Bounds != SystemInformation.VirtualScreen)
            Bounds = SystemInformation.VirtualScreen;

        if (!Visible) { Visible = true; return; }          // 首次显示会整窗重画

        // 只重画变化过的条带，别整屏刷 ——
        // 虚拟屏 4000×2560 全量重绘一次就是十几 MB 的位图操作，拖动时每帧一次扛不住。
        foreach (var r in old) Invalidate(r, false);
        foreach (var r in _lines) Invalidate(r, false);
        Update();
    }

    /// <summary>收起参考线</summary>
    public void Clear()
    {
        if (_lines.Count == 0 && !Visible) return;
        _lines.Clear();
        Hide();
    }
}

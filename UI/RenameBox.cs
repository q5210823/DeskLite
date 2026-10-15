namespace DeskLite.UI;

/// <summary>
/// 「就地改名」用的输入框 —— 一个**独立的小顶层窗口**。
///
/// <b>为什么不把 TextBox 直接放进格子？</b>
/// 格子窗口（<see cref="BoxForm"/>）是用 <c>UpdateLayeredWindow</c> 提交 32bpp 位图实现的
/// per-pixel alpha 分层窗口。这种窗口的画面**完全**来自我们提交的那张位图 ——
/// Win32 不会再去画它的任何子控件。放进格子的 TextBox 会「窗口存在、也拿到了焦点、
/// 但屏幕上永远看不见」，查起来非常费劲（现象就是「点了重命名没反应」）。
///
/// 单独一个小窗还有个附带好处：中文输入法直接可用。若改成在 <see cref="BoxView"/>
/// 里自绘输入框，就得自己处理 WM_IME_* 一整套，重命名中文文件名会很难受。
///
/// 生命周期：回车 / 失焦 → 提交；Esc → 取消。两条路径都只走一次
/// （<see cref="_finished"/> 挡住 Close 引发的 LostFocus 回灌）。
/// </summary>
internal sealed class RenameBox : Form
{
    private static readonly Color Edge = Color.FromArgb(76, 194, 255);
    private static readonly Color Paper = Color.FromArgb(250, 250, 252);
    private static readonly Color Ink = Color.FromArgb(24, 24, 32);

    private readonly TextBox _text;
    private bool _finished;

    /// <summary>用户确认（返回去掉首尾空白的文本；空串由调用方忽略）</summary>
    internal event Action<string>? Committed;

    /// <summary>用户放弃（Esc）</summary>
    internal event Action? Cancelled;

    internal RenameBox(string initial, Rectangle screenBounds, Font font)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;                       // 格子本身也在最上层，编辑框必须压在它上面
        // 坐标是调用方算好的绝对屏幕像素，别让 WinForms 再按字体给我缩一遍
        // （默认的 Font 自动缩放会把 78px 的框拉成 150px，压在邻居图标上）。
        AutoScaleMode = AutoScaleMode.None;

        // 还得把「最小尺寸」显式写掉：不写的话 WinForms 走默认分支，
        // 按系统的最小跟踪宽度（这台机器上是 136px）把窗口撑大 ——
        // 表现就是一个本该和图标同宽的输入框横着压到邻居身上。
        MinimumSize = new Size(1, 1);

        Bounds = screenBounds;
        BackColor = Paper;
        KeyPreview = true;

        _text = new TextBox
        {
            Text = initial,
            BorderStyle = BorderStyle.None,
            Font = font,
            TextAlign = HorizontalAlignment.Center,
            BackColor = Paper,
            ForeColor = Ink,
        };
        _text.KeyDown += OnTextKeyDown;
        Controls.Add(_text);

        Shown += (_, _) =>
        {
            Activate();
            // 兜底：Show 有时因为前台切换限制没能把窗口推到最前（表现是能看见框、
            // 但敲键盘没反应）。再要一次，拿到就直接把焦点给输入框。
            if (!Focused)
            {
                try { Win32.Native.SetForegroundWindow(Handle); } catch { }
            }
            _text.Focus();
            _text.SelectAll();
            // 挂得晚一点：Show 过程中的焦点变动不该被当成「用户点走了」
            _text.LostFocus += (_, _) => Commit();
        };
    }

    /// <summary>
    /// 单行 TextBox 的高度由字体定死（约 15px），铺满整个框反而贴顶。
    /// 这里手动放到垂直居中，四周留 2px。
    /// </summary>
    private void LayoutText()
    {
        if (_text == null) return;          // 构造里设 Bounds 时会先触发一次尺寸变化
        int h = _text.PreferredHeight;
        _text.Bounds = new Rectangle(
            4,
            Math.Max(0, (ClientSize.Height - h) / 2),
            Math.Max(10, ClientSize.Width - 8),
            h);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayoutText();
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        LayoutText();
    }

    private void OnTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Escape) { Cancel(); e.SuppressKeyPress = true; }
    }

    /// <summary>外部（点开别处的菜单 / 列表刷新）要求收尾 —— 等同于回车</summary>
    internal void CommitNow() => Commit();

    /// <summary>宿主销毁时的兜底收尾，不触发任何回调</summary>
    internal void ForceClose()
    {
        _finished = true;
        try { Close(); } catch { /* 已经关了 */ }
    }

    private void Commit()
    {
        if (_finished) return;
        _finished = true;
        string text = _text.Text.Trim();
        try { Close(); } catch { }
        Committed?.Invoke(text);
    }

    private void Cancel()
    {
        if (_finished) return;
        _finished = true;
        try { Close(); } catch { }
        Cancelled?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Edge);
        var r = ClientRectangle;
        e.Graphics.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
    }
}

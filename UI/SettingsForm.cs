using DeskLite.Core;

namespace DeskLite.UI;

/// <summary>
/// 设置中心（对应 DeskGo 的 <c>DesktopSettings.exe</c>）。
///
/// DeskGo 有 3 个设置页：基础页 / 桌面页 / 壁纸页。
/// 精简版去掉壁纸页，把「右键集成」独立成第三页，共 3 页：
///   基础    → CFG_SYS_THEME、CFG_SYS_GLASS、CFG_ICON_SIZE、CFG_DESKTOPMGR_AUTORUN
///   桌面    → CFG_ENABLED_HIDE_ALL_ICONS、CFG_HIDE_ALL_FILESANDBOXES、
///             CFG_FENCES_AUTO_COLLAPSE、CFG_SEARCH_HOTKEY、CFG_FENCES_DBLCLICK_OPEN
///   右键集成 → CFG_RMENU_DESKTOP、CFG_RMENU_FILE、CFG_RMENU_WIN11、CFG_RMENU_CLEAN
/// </summary>
internal sealed class SettingsForm : Form
{
    // ── 配色（与格子 / 搜索窗一致）──
    private static readonly Color Bg = Color.FromArgb(30, 30, 38);
    private static readonly Color BarBg = Color.FromArgb(38, 38, 47);
    private static readonly Color FieldBg = Color.FromArgb(46, 46, 57);
    private static readonly Color TextColor = Color.FromArgb(238, 238, 244);
    private static readonly Color SubColor = Color.FromArgb(150, 150, 166);
    private static readonly Color Accent = Color.FromArgb(76, 194, 255);
    private static readonly Color BtnBg = Color.FromArgb(52, 52, 64);
    private static readonly Color Danger = Color.FromArgb(235, 128, 128);

    private const int HeaderH = 46;
    private const int TabH = 42;
    private const int FooterH = 78;

    /// <summary>页面内容与底部按钮栏之间留的呼吸距离</summary>
    private const int PagePad = 14;

    /// <summary>三页里最高那一页的内容底部（绝对定位算出来的），用来定窗口高度</summary>
    private int _pageBottom;

    private readonly AppSettings _work;
    private readonly Action<AppSettings> _onApply;

    private readonly Panel _host = new();
    private readonly Button[] _tabs = new Button[3];
    private readonly Panel[] _pages = new Panel[3];

    // 基础页
    private ComboBox _cbTheme = null!, _cbIcon = null!;
    private TrackBar _tbGlass = null!;
    private Label _lblGlass = null!;
    private CheckBox _chkAutoRun = null!;
    private Label _lblMem = null!;

    /// <summary>「运行占用」的自动刷新定时器（仅在窗口打开期间运行）</summary>
    private System.Windows.Forms.Timer? _memTimer;

    /// <summary>当前装在窗口上的圆角 Region（见 <see cref="Shapes.ApplyRoundedRegion"/>）</summary>
    private Region? _frameRegion;

    // 桌面页
    private CheckBox _chkTakeover = null!, _chkEmbed = null!, _chkCollapse = null!, _chkDbl = null!;
    private TextBox _txtHotkey = null!;
    private Label _lblHotkeyCheck = null!;

    // 右键集成页
    private CheckBox _chkMenuDesk = null!, _chkMenuFile = null!;
    private RadioButton _rbWin11 = null!, _rbClassic = null!;
    private Label _lblMenuState = null!;

    public SettingsForm(AppSettings current, Action<AppSettings> onApply, int initialPage = 0)
    {
        _work = current.Clone();
        _onApply = onApply;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(600, 566);
        BackColor = Bg;
        ShowInTaskbar = false;
        KeyPreview = true;
        DoubleBuffered = true;
        Font = new Font("Microsoft YaHei UI", 9F);

        _host.Dock = DockStyle.Fill;
        _host.BackColor = Bg;
        Controls.Add(_host);
        Controls.Add(BuildTabsBar());
        Controls.Add(BuildHeaderBar());
        Controls.Add(BuildFooterBar());

        BuildBasicPage();
        BuildDeskPage();
        BuildMenuPage();
        LoadFromSettings();

        // 三页的内容全是绝对定位。窗口高度直接取「最高的一页 + 一点余量」——
        // 于是既不会出现「最后一段说明被裁掉」，也不会冒出横竖滚动条。
        // 以后往页面里加两行也无需再去手调窗体尺寸。
        Height = HeaderH + TabH + FooterH + _pageBottom + PagePad;

        ShowPage(Math.Clamp(initialPage, 0, 2));

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    // ═══════════════ 外壳 ═══════════════

    private Panel BuildHeaderBar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = HeaderH, BackColor = BarBg };
        bar.MouseDown += DragStart;

        var title = new Label
        {
            Text = "设置",
            ForeColor = TextColor,
            Font = new Font("Microsoft YaHei UI", 11.5F, FontStyle.Bold),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(22, 0),
            Size = new Size(200, HeaderH),
        };
        title.MouseDown += DragStart;

        var sub = new Label
        {
            Text = "桌面整理 · 精简版",
            ForeColor = SubColor,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(74, 1),
            Size = new Size(220, HeaderH),
        };
        sub.MouseDown += DragStart;   // 副标题压在标题右侧，不绑的话那段拖不动

        var close = new Button
        {
            Text = "✕",
            FlatStyle = FlatStyle.Flat,
            ForeColor = SubColor,
            BackColor = BarBg,
            Size = new Size(44, HeaderH),
            Location = new Point(Width - 54, 0),
            TabStop = false,
            Cursor = Cursors.Hand,
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(190, 70, 70);
        close.Click += (_, _) => Close();
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        bar.Controls.Add(title);
        bar.Controls.Add(sub);
        bar.Controls.Add(close);
        return bar;
    }

    private Panel BuildTabsBar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = TabH, BackColor = BarBg };
        string[] names = { "基础", "桌面", "右键集成" };

        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var b = new Button
            {
                Text = names[i],
                FlatStyle = FlatStyle.Flat,
                ForeColor = SubColor,
                BackColor = BarBg,
                Font = new Font("Microsoft YaHei UI", 9.5F),
                Size = new Size(104, TabH),
                Location = new Point(16 + i * 108, 0),
                TabStop = false,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 48, 60);
            b.Click += (_, _) => ShowPage(idx);
            _tabs[i] = b;
            bar.Controls.Add(b);
        }
        return bar;
    }

    private Panel BuildFooterBar()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = FooterH, BackColor = BarBg };
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(58, 58, 70));
            e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
        };

        var cancel = MakeButton("取消", 380, 22, 92, 36, BtnBg, TextColor);
        cancel.Click += (_, _) => Close();

        var save = MakeButton("保存并应用", 482, 22, 102, 36, Accent, Color.FromArgb(16, 22, 30));
        save.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        save.Click += (_, _) => Save();

        var note = new Label
        {
            Text = "设置保存到 %APPDATA%\\DeskLite\\ConFile.dat",
            ForeColor = SubColor,
            Font = new Font("Microsoft YaHei UI", 8F),
            AutoSize = false,
            Location = new Point(24, 30),
            Size = new Size(340, 20),
        };

        bar.Controls.Add(note);
        bar.Controls.Add(cancel);
        bar.Controls.Add(save);
        return bar;
    }

    private void ShowPage(int index)
    {
        for (int i = 0; i < 3; i++)
        {
            _pages[i].Visible = i == index;
            _tabs[i].ForeColor = i == index ? Accent : SubColor;
            _tabs[i].Font = new Font("Microsoft YaHei UI", 9.5F,
                i == index ? FontStyle.Bold : FontStyle.Regular);
        }
        if (index == 2) RefreshMenuState();
    }

    // ═══════════════ 基础页 ═══════════════

    private void BuildBasicPage()
    {
        var p = NewPage();
        int y = 26;

        AddLabel(p, "外观主题", 28, y + 2);
        _cbTheme = AddCombo(p, 150, y - 2, 180, new[] { "深色", "浅色" });
        y += 56;

        AddLabel(p, "格子毛玻璃", 28, y + 2);
        _tbGlass = new TrackBar
        {
            Minimum = 30, Maximum = 100,
            TickFrequency = 10,
            SmallChange = 1, LargeChange = 5,
            Location = new Point(146, y - 8),
            Size = new Size(250, 44),
            BackColor = Bg,
        };
        _tbGlass.ValueChanged += (_, _) => _lblGlass.Text = $"{_tbGlass.Value}%";
        p.Controls.Add(_tbGlass);

        _lblGlass = new Label
        {
            ForeColor = Accent,
            Font = new Font("Consolas", 10.5F),
            AutoSize = false,
            Location = new Point(404, y + 4),
            Size = new Size(80, 24),
        };
        p.Controls.Add(_lblGlass);

        AddSub(p, "30 = 最透明，100 = 完全不透明（对应 DeskGo「毛玻璃效果配置」）", 150, y + 34);
        y += 72;

        AddLabel(p, "图标大小", 28, y + 2);
        _cbIcon = AddCombo(p, 150, y - 2, 180, new[] { "32", "40", "48", "64" });
        AddSub(p, "像素（影响格子内图标与间距）", 344, y + 4);
        y += 58;

        _chkAutoRun = AddCheck(p, "随 Windows 登录自动启动", 28, y, 420);
        y += 44;

        AddDivider(p, 28, y, 544);
        y += 14;

        // ── 运行占用（可直接核对「小而轻」）──
        //
        // 这里刻意**不放「释放内存」按钮**：回收是自动的（启动后整理一次，
        // 之后只在空闲时动手），给个手动按钮反而会让人以为平时在漏内存。
        // 数值由 RefreshMemoryLabel 定时刷新，用户能直接看到常驻占用。
        AddLabel(p, "运行占用", 28, y + 2, 110);
        _lblMem = new Label
        {
            ForeColor = TextColor,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            AutoSize = false,
            Location = new Point(146, y),
            Size = new Size(410, 24),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        p.Controls.Add(_lblMem);
        y += 30;

        AddSub(p, "内存全自动回收：启动完成后整理一次，之后只在空闲时（静默 4 秒）才动手，\n" +
                   "忙的时候绝不插手 —— 所以不需要手动释放。",
            146, y, 426, 2);
        y += 34;

        AddDivider(p, 28, y, 544);
        y += 12;

        // 这段说明按「显式三行」写：之前靠自动换行，字长一超就被 Label 裁掉最后一行，
        // 看着就像「选项没显示全」。宽度给足、行数说清，不再依赖换行结果。
        AddSub(p, "精简版去掉：魔镜壁纸 / 挂件、云与 AI（一键上云、AI 问答、AI 便签）、\n" +
                   "内嵌内容（CEF 浏览器、TBS、下载中心）。\n" +
                   "保留：格子分类规则、类型码、序列化格式与滚动备份策略。",
            28, y, 544, 3);
        NotePage(y + 54);

        _host.Controls.Add(p);
        _pages[0] = p;
    }

    // ═══════════════ 桌面页 ═══════════════

    private void BuildDeskPage()
    {
        var p = NewPage();
        int y = 22;

        _chkTakeover = AddCheck(p, "启动后接管桌面图标（隐藏原生图标，由格子呈现）", 28, y, 540);
        AddSub(p, "若「腾讯桌面整理」正在运行，其覆盖层会盖住原生图标，此功能视觉上会失效", 50, y + 26);
        y += 62;

        _chkEmbed = AddCheck(p, "把格子嵌入桌面层（随桌面显示 / 隐藏，不占 Alt+Tab）", 28, y, 540);
        y += 38;

        _chkCollapse = AddCheck(p, "记住格子的折叠状态", 28, y, 540);
        y += 38;

        _chkDbl = AddCheck(p, "双击打开项目（取消则改为单击打开）", 28, y, 540);
        y += 52;

        AddDivider(p, 28, y, 544);
        y += 18;

        AddLabel(p, "桌面搜索快捷键", 28, y + 2);
        _txtHotkey = new TextBox
        {
            BackColor = FieldBg,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 10.5F),
            Location = new Point(178, y),
            Size = new Size(160, 26),
        };
        _txtHotkey.TextChanged += (_, _) => CheckHotkey();
        p.Controls.Add(_txtHotkey);

        _lblHotkeyCheck = new Label
        {
            ForeColor = SubColor,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            AutoSize = false,
            Location = new Point(350, y + 3),
            Size = new Size(220, 22),
        };
        p.Controls.Add(_lblHotkeyCheck);

        y += 40;
        AddSub(p, "格式示例：Ctrl+Alt+S、Ctrl+Shift+Space、Win+D。留空表示不注册全局热键。\n" +
                   "保存后立即生效；若已被其他程序占用，会在托盘提示。", 28, y, 544, 2);
        NotePage(y + 38);

        _host.Controls.Add(p);
        _pages[1] = p;
    }

    // ═══════════════ 右键集成页 ═══════════════

    private void BuildMenuPage()
    {
        var p = NewPage();
        int y = 22;

        _chkMenuDesk = AddCheck(p, "桌面空白处右键菜单（一键整理桌面 / 桌面搜索）", 28, y, 540);
        y += 38;
        _chkMenuFile = AddCheck(p, "文件 / 文件夹右键菜单（送入格子 / 桌面搜索）", 28, y, 540);
        y += 50;

        AddLabel(p, "菜单风格", 28, y + 2);
        y += 30;

        _rbWin11 = AddRadio(p, "Win11 新式菜单（默认）", 46, y, 300);
        y += 32;
        _rbClassic = AddRadio(p, "恢复经典菜单（净化）—— 对应 DeskGo「净化右键菜单配置」", 46, y, 460);
        y += 42;

        AddDivider(p, 28, y, 544);
        y += 14;

        _lblMenuState = new Label
        {
            ForeColor = SubColor,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            AutoSize = false,
            Location = new Point(28, y),
            Size = new Size(544, 52),
        };
        p.Controls.Add(_lblMenuState);
        y += 58;

        var bInstall = MakeButton("注册 / 更新右键菜单", 28, y, 178, 36, Accent, Color.FromArgb(16, 22, 30));
        bInstall.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        bInstall.Click += (_, _) => DoInstall();
        p.Controls.Add(bInstall);

        var bRemove = MakeButton("移除右键菜单", 216, y, 130, 36, BtnBg, TextColor);
        bRemove.Click += (_, _) => DoRemove();
        p.Controls.Add(bRemove);

        var bRestart = MakeButton("重启资源管理器", 356, y, 140, 36, BtnBg, TextColor);
        bRestart.Click += (_, _) => DoRestartExplorer();
        p.Controls.Add(bRestart);

        AddSub(p, "注册表方式（HKCU，无需管理员、不注入 explorer）；卸载即删键可完全回滚。\n" +
                   "Win11 上菜单位于「显示更多选项」；切换菜单风格后需重启资源管理器生效。",
            28, y + 44, 544, 2);
        NotePage(y + 44 + 38);

        _host.Controls.Add(p);
        _pages[2] = p;
    }

    // ═══════════════ 数据绑定 ═══════════════

    private void LoadFromSettings()
    {
        _cbTheme.SelectedIndex = _work.IsLight ? 1 : 0;
        _tbGlass.Value = Math.Clamp(_work.GlassOpacity, 30, 100);
        _lblGlass.Text = $"{_tbGlass.Value}%";

        int iconIdx = Array.IndexOf(new[] { 32, 40, 48, 64 }, _work.IconSize);
        _cbIcon.SelectedIndex = iconIdx >= 0 ? iconIdx : 2;

        _chkAutoRun.Checked = _work.AutoRun || Startup.IsEnabled;

        _chkTakeover.Checked = _work.TakeoverOnStart;
        _chkEmbed.Checked = _work.EmbedOnStart;
        _chkCollapse.Checked = _work.RememberCollapse;
        _chkDbl.Checked = _work.DoubleClickOpen;
        _txtHotkey.Text = _work.SearchHotkey;

        _chkMenuDesk.Checked = _work.MenuDesktop;
        _chkMenuFile.Checked = _work.MenuFile;
        _rbWin11.Checked = !_work.CleanMenu;
        _rbClassic.Checked = _work.CleanMenu;

        CheckHotkey();
        RefreshMenuState();
        RefreshMemoryLabel();
    }

    /// <summary>刷新「运行占用」一行：工作集 / 私有内存 / 线程 / GDI 对象</summary>
    private void RefreshMemoryLabel()
    {
        if (_lblMem == null) return;
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            p.Refresh();
            uint gdi = 0;
            try { gdi = Win32.Native.GetGuiResources(p.Handle, Win32.Native.GR_GDIOBJECTS); } catch { }
            _lblMem.Text =
                $"工作集 {p.WorkingSet64 / 1048576.0:N1} MB｜私有 {p.PrivateMemorySize64 / 1048576.0:N1} MB" +
                $"｜线程 {p.Threads.Count}｜GDI {gdi}";
        }
        catch { _lblMem.Text = "读取失败"; }
    }

    private void CheckHotkey()
    {
        var s = _txtHotkey.Text.Trim();
        if (s.Length == 0)
        {
            _lblHotkeyCheck.Text = "未启用";
            _lblHotkeyCheck.ForeColor = SubColor;
        }
        else if (CmdBus.TryParse(s, out _, out _))
        {
            _lblHotkeyCheck.Text = "✓ 格式有效";
            _lblHotkeyCheck.ForeColor = Color.FromArgb(120, 210, 140);
        }
        else
        {
            _lblHotkeyCheck.Text = "✗ 格式无法识别";
            _lblHotkeyCheck.ForeColor = Danger;
        }
    }

    private void RefreshMenuState()
    {
        var lines = new List<string>
        {
            $"桌面背景菜单：{(ShellIntegration.DesktopInstalled ? "已注册" : "未注册")}",
            $"文件/文件夹菜单：{(ShellIntegration.FileInstalled ? "已注册" : "未注册")}",
            $"当前菜单风格：{(ShellIntegration.IsClassicMenu ? "Win10 经典（净化）" : "Win11 新式")}",
        };
        _lblMenuState.Text = string.Join("\n", lines);
    }

    private void DoInstall()
    {
        Collect();
        string exe = ShellIntegration.ExePath;
        if (string.IsNullOrEmpty(exe))
        {
            MessageBox.Show(this, "无法定位程序自身路径，注册失败。", "右键集成",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ShellIntegration.Install(_work, exe);
        ShellIntegration.ApplyMenuStyle(_work);
        RefreshMenuState();

        string msg = $"已注册：\n" +
                     $"  桌面背景菜单  {(ShellIntegration.DesktopInstalled ? "✓" : "✗")}\n" +
                     $"  文件/文件夹菜单  {(ShellIntegration.FileInstalled ? "✓" : "✗")}\n";
        if (_work.CleanMenu && !_work.Win11Menu)
            msg += "\n已切换为经典菜单，需重启资源管理器后生效。";
        MessageBox.Show(this, msg, "右键集成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void DoRemove()
    {
        ShellIntegration.Uninstall();
        _chkMenuDesk.Checked = false;
        _chkMenuFile.Checked = false;
        RefreshMenuState();
        MessageBox.Show(this, "已移除本程序写入的全部右键菜单项（注册表键已删除）。",
            "右键集成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void DoRestartExplorer()
    {
        var r = MessageBox.Show(this,
            "将结束并重启 explorer.exe。\n桌面与任务栏会短暂消失约 1 秒，随后自动恢复。\n\n继续吗？",
            "重启资源管理器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes) return;

        bool ok = ShellIntegration.RestartExplorer();
        MessageBox.Show(this, ok ? "已重启资源管理器。" : "重启失败，请手动在任务管理器中重启。",
            "重启资源管理器", MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // ═══════════════ 保存 ═══════════════

    private void Collect()
    {
        _work.Theme = _cbTheme.SelectedIndex == 1 ? "Light" : "Dark";
        _work.GlassOpacity = _tbGlass.Value;

        int[] sizes = { 32, 40, 48, 64 };
        _work.IconSize = sizes[Math.Clamp(_cbIcon.SelectedIndex, 0, 3)];

        _work.AutoRun = _chkAutoRun.Checked;

        _work.TakeoverOnStart = _chkTakeover.Checked;
        _work.EmbedOnStart = _chkEmbed.Checked;
        _work.RememberCollapse = _chkCollapse.Checked;
        _work.DoubleClickOpen = _chkDbl.Checked;
        _work.SearchHotkey = _txtHotkey.Text.Trim();

        _work.MenuDesktop = _chkMenuDesk.Checked;
        _work.MenuFile = _chkMenuFile.Checked;
        _work.Win11Menu = _rbWin11.Checked;
        _work.CleanMenu = _rbClassic.Checked;
    }

    private void Save()
    {
        Collect();

        if (_work.SearchHotkey.Length > 0 && !CmdBus.TryParse(_work.SearchHotkey, out _, out _))
        {
            MessageBox.Show(this, $"搜索快捷键「{_work.SearchHotkey}」格式无法识别，请修正或留空。",
                "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowPage(1);
            return;
        }

        _work.Save();
        Startup.Apply(_work.AutoRun);

        // 右键菜单设置发生变化时，直接同步到注册表
        ShellIntegration.Install(_work);

        try { _onApply?.Invoke(_work); } catch { }

        Close();
    }

    // ═══════════════ 布局小工具 ═══════════════

    /// <summary>
    /// 新建一页。
    ///
    /// **刻意不开 AutoScroll**：滚动条在这个尺寸的设置面板里既难看又没用武之地
    /// （一旦出现，说明窗口本来就该长高）。真正的解法是让窗口按内容自适应 ——
    /// 见构造函数里用 <see cref="NotePage"/> 收上来的高度。
    /// </summary>
    private Panel NewPage()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            Visible = false,
            AutoScroll = false,
        };
    }

    /// <summary>报告某一页的内容底部（含最后一行文字的实际高度），窗口取三页里的最大值</summary>
    private void NotePage(int bottom)
    {
        if (bottom > _pageBottom) _pageBottom = bottom;
    }

    private static Label AddLabel(Control host, string text, int x, int y, int w = 130)
    {
        var l = new Label
        {
            Text = text,
            ForeColor = TextColor,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(x, y),
            Size = new Size(w, 24),
        };
        host.Controls.Add(l);
        return l;
    }

    private static void AddSub(Control host, string text, int x, int y, int w = 400, int lines = 1)
    {
        var l = new Label
        {
            Text = text,
            ForeColor = SubColor,
            Font = new Font("Microsoft YaHei UI", 8F),
            AutoSize = false,
            Location = new Point(x, y),
            Size = new Size(w, 16 * lines + 6),
        };
        host.Controls.Add(l);
    }

    private static void AddDivider(Control host, int x, int y, int w)
    {
        var d = new Panel { BackColor = Color.FromArgb(56, 56, 68), Location = new Point(x, y), Size = new Size(w, 1) };
        host.Controls.Add(d);
    }

    private static ComboBox AddCombo(Control host, int x, int y, int w, string[] items)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = FieldBg,
            ForeColor = TextColor,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            Location = new Point(x, y),
            Size = new Size(w, 28),
        };
        c.Items.AddRange(items);
        c.SelectedIndex = 0;
        host.Controls.Add(c);
        return c;
    }

    private static CheckBox AddCheck(Control host, string text, int x, int y, int w)
    {
        var c = new CheckBox
        {
            Text = text,
            ForeColor = TextColor,
            Font = new Font("Microsoft YaHei UI", 9F),
            AutoSize = false,
            Location = new Point(x, y),
            Size = new Size(w, 26),
            FlatStyle = FlatStyle.Standard,
            BackColor = Color.Transparent,
        };
        host.Controls.Add(c);
        return c;
    }

    private static RadioButton AddRadio(Control host, string text, int x, int y, int w)
    {
        var r = new RadioButton
        {
            Text = text,
            ForeColor = TextColor,
            Font = new Font("Microsoft YaHei UI", 9F),
            AutoSize = false,
            Location = new Point(x, y),
            Size = new Size(w, 26),
            FlatStyle = FlatStyle.Standard,
            BackColor = Color.Transparent,
        };
        host.Controls.Add(r);
        return r;
    }

    private static Button MakeButton(string text, int x, int y, int w, int h, Color back, Color fore)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Font = new Font("Microsoft YaHei UI", 9F),
            Location = new Point(x, y),
            Size = new Size(w, h),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.15f);
        return b;
    }

    // ── 拖动窗口 ──
    //
    // 早先的写法是自己记 Location 跟着鼠标算（MouseDown 置标志 + MouseMove 挪窗口）。
    // 它有一个很隐蔽的缺陷：复位标志靠的是窗体的 MouseUp，而**鼠标在窗口外松手时
    // 窗体根本收不到这个事件**（拖快一点就必然发生）——标志于是永远停在「正在拖动」，
    // 此后鼠标不按键、只是经过标题栏，整个窗口就跟着鼠标飘。
    //
    // 现在改为把这次按下交给系统的标题栏拖动：系统负责捕获鼠标、负责结束，
    // 没有任何状态残留在我们这边。
    private void DragStart(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        try
        {
            Win32.Native.ReleaseCapture();
            Win32.Native.SendMessage(Handle, Win32.Native.WM_NCLBUTTONDOWN,
                (IntPtr)Win32.Native.HTCAPTION, IntPtr.Zero);
        }
        catch { /* 拖动失败不影响窗口本身 */ }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 下拉框一旦拿到焦点，DropDownList 会把选中项画成系统高亮的亮蓝色块，
        // 打开设置第一眼就像是「已经选中/被框住」。把初始焦点交给没有高亮的轨道条。
        ActiveControl = _tbGlass;

        RefreshMemoryLabel();

        // 窗口开着的时候让「运行占用」自己刷 —— 用户点进设置就是想看这个数，
        // 而且没了手动释放按钮之后，这里必须能自证「回收是自动发生的」。
        _memTimer ??= new System.Windows.Forms.Timer { Interval = 1000 };
        _memTimer.Tick -= OnMemTick;      // 防重复挂钩
        _memTimer.Tick += OnMemTick;
        _memTimer.Start();
    }

    private void OnMemTick(object? sender, EventArgs e) => RefreshMemoryLabel();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _memTimer?.Stop(); _memTimer?.Dispose(); } catch { }
            _memTimer = null;

            // 窗口自己不会释放赋给 Region 属性的 GDI 对象，得由我们收尾
            _frameRegion?.Dispose();
            _frameRegion = null;
        }
        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 见 Shapes.ApplyRoundedRegion：必须复用同一个 holder，否则每次改尺寸漏一个 GDI Region
        Shapes.ApplyRoundedRegion(this, 12, ref _frameRegion);
    }
}

using System.Diagnostics;
using System.Drawing;
using DeskLite.Core;
using DeskLite.Win32;

namespace DeskLite.UI;

/// <summary>
/// 应用主控制器（托盘常驻）。
///
/// 精简版功能面（相对 DeskGo 已去掉壁纸 / 云与 AI / 内嵌内容）：
///   · 桌面整理：按类型码自动分格、格子拖拽、折叠收展、一键隐藏、变更监听
///   · 桌面搜索：文件名 + 拼音首字母，支持全局热键与右键菜单唤起
///   · 设置    ：基础 / 桌面 / 右键集成 三页（对应 DesktopSettings.exe）
///   · 右键集成：桌面背景、文件/文件夹右键级联菜单（HKCU 注册表，无需管理员）
///   · 支撑层  ：同结构键名 + 日/周/月滚动备份、单实例、命令总线、冲突检测
/// </summary>
internal sealed class MainContext : ApplicationContext, IBoxSnapper
{
    private readonly DataStore _store;
    private readonly DesktopHost _host = new();
    private readonly DeskData _data;
    private readonly CmdBus _bus;

    private AppSettings _settings;
    private IconCache _icons;

    private readonly Dictionary<int, BoxForm> _forms = new();
    private readonly NotifyIcon _tray;
    /// <summary>托盘图标：来自嵌入资源 app-tray.ico（独立持有，退出时释放）；失败则退回系统默认</summary>
    private readonly Icon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _miTakeover;
    private readonly ToolStripMenuItem _miStatus;
    private readonly ToolStripMenuItem _miEmbed;
    private readonly ToolStripMenuItem _miHideBoxes;
    private readonly ToolStripMenuItem _miAutoHidden;
    private readonly ToolStripMenuItem _miConflict;
    private readonly ToolStripMenuItem _miHotkey;

    /// <summary>桌面目录监听器（用户桌面 + 公共桌面各一个）</summary>
    private readonly List<FileSystemWatcher> _watchers = new();
    /// <summary>UI 线程上下文：FileSystemWatcher 的事件来自线程池线程，需要弹回 UI 线程</summary>
    private SynchronizationContext? _ui;
    private System.Windows.Forms.Timer? _debounce;
    private System.Windows.Forms.Timer? _keepAlive;
    private SearchForm? _search;
    private SettingsForm? _settingsForm;

    private bool _takeover;      // 是否已隐藏原生桌面图标
    private bool _embedded;      // 格子是否嵌入桌面层
    private IntPtr _embedHwnd;   // 当前嵌入的父窗口（Progman）；explorer 重启后会变
    private bool _arrangedOnce;  // 首次灌入内容后是否已做过「重叠则重排」
    private bool _boxesHidden;   // 是否一键隐藏了所有格子

    /// <summary>因「里面是空的」而自动隐藏的内置格子（见 <see cref="ApplyBoxVisibility"/>）</summary>
    private readonly HashSet<int> _autoHidden = new();

    private List<(string Name, int Pid)> _deskGo = new();   // 检测到的 DeskGo 进程
    private IntPtr _overlayHwnd = IntPtr.Zero;              // DeskGo 覆盖层窗口

    /// <summary>诊断用：构造过程的分步回调（正常运行时为 null）</summary>
    private Action<string>? _onStep;

    private void Mark(string tag) { try { _onStep?.Invoke(tag); } catch { } }

    public MainContext(bool autoTakeover = true, string? initialCmd = null, string? initialArg = null,
        Action<string>? onStep = null, bool readOnlyData = false)
    {
        // 只读模式（诊断命令用）：整条存储链路都退化成「只看不写」，
        // 无论后面走到哪条 _store.Save()，用户的数据文件都不会被碰。
        _store = new DataStore(readOnly: readOnlyData);

        _onStep = onStep;
        if (onStep != null) BoxView.Trace = onStep;
        void Mark(string tag) { try { _onStep?.Invoke(tag); } catch { } }

        Mark("start");
        Pinyin.Init();
        Mark("Pinyin.Init（CodePages/GB2312 表）");

        _settings = AppSettings.Load();
        Mark("AppSettings.Load");
        Trace($"启动：IconSize 设置={_settings.IconSize} TakeoverOnStart={_settings.TakeoverOnStart} " +
              $"EmbedOnStart={_settings.EmbedOnStart} RememberCollapse={_settings.RememberCollapse}");

        _icons = new IconCache(Math.Clamp(_settings.IconSize, 16, 128));

        _data = _store.Load();
        Mark("DataStore.Load（格子模型）");

        // 老格式（DeskGo 原生 / 早期 DeskLite 落盘的 boxScreen 行没有版本号）读进来后
        // 已在 Parse 里补齐到当前 schema，这里回写一次把它固化到磁盘 ——
        // 一次性动作，之后每次加载都不会再判为「需要迁移」。
        if (_data.Migrated)
        {
            SaveNow();
            Mark($"数据格式迁移 → schema v{DeskBox.CurrentSchemaVersion}");
        }

        // 老数据文件里没有「系统格子」—— 早期版本还没实现虚拟项，只建了 101/102/104。
        // 这里补一个，并且必须挑一个**不与现有格子相交**的位置：否则紧接着的
        // ArrangeIfOverlapping() 会判定重叠、把用户拖好的布局整体重排。
        if (_data.EnsureSystemBox(Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080)))
        {
            SaveNow();
            Mark("补齐系统格子（承载 此电脑 / 回收站 / 网络）");
        }

        bool located = _host.Locate();
        Mark("DesktopHost.Locate");

        // 冲突检测：DeskGo 若在运行，其 TXMiniSkin 覆盖层会盖住原生图标层
        _deskGo = Conflict.RunningDeskGo();
        bool overlay = Conflict.OverlayPresent(out _overlayHwnd, out _);
        Mark("Conflict 检测");

        _miStatus = new ToolStripMenuItem(located
            ? $"桌面窗口已定位（0x{_host.ListView:X}）"
            : "桌面窗口未定位 ⚠")
        { Enabled = false };

        _miConflict = new ToolStripMenuItem("")
        {
            Enabled = false,
            ForeColor = System.Drawing.Color.FromArgb(200, 120, 0),
        };
        UpdateConflictText();

        _miHotkey = new ToolStripMenuItem("") { Enabled = false };

        _miTakeover = new ToolStripMenuItem("接管桌面图标（隐藏原生图标）", null, (_, _) => ToggleTakeover());
        _miEmbed = new ToolStripMenuItem("格子嵌入桌面层（实验）", null, (_, _) => ToggleEmbed());
        _miHideBoxes = new ToolStripMenuItem("隐藏所有格子", null, (_, _) => ToggleHideBoxes());

        // 只读状态行：内置格子被「空则自动隐藏」收起来时露个面，免得用户以为格子丢了
        _miAutoHidden = new ToolStripMenuItem("") { Enabled = false, Visible = false };

        Mark("托盘菜单项对象创建完成（尚未创建 ContextMenuStrip）");

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_miStatus);
        _menu.Items.Add(_miHotkey);
        _menu.Items.Add(_miConflict);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("新建格子…", null, (_, _) => CreateBoxInteractive());
        _menu.Items.Add("一键整理桌面", null, (_, _) => AutoSort());
        _menu.Items.Add("重新扫描", null, (_, _) => RefreshAll());
        _menu.Items.Add("重新检测桌面整理冲突", null, (_, _) => RecheckConflict());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_miTakeover);
        _menu.Items.Add(_miEmbed);
        _menu.Items.Add(_miHideBoxes);
        _menu.Items.Add(_miAutoHidden);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("桌面搜索…", null, (_, _) => ShowSearch());
        _menu.Items.Add("设置…", null, (_, _) => ShowSettings(0));
        _menu.Items.Add("右键菜单集成…", null, (_, _) => ShowSettings(2));
        _menu.Items.Add("打开数据目录", null, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(_store.Root) { UseShellExecute = true }); } catch { }
        });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出并还原桌面", null, (_, _) => ExitApp());

        Mark("托盘菜单项构建完成");

        _tray = new NotifyIcon
        {
            Icon = _trayIcon = CreateTrayIcon(),
            Text = "桌面整理 · 精简版",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (_, _) => ShowSearch();
        Mark("NotifyIcon 创建（会拉起 shell 依赖 DLL）");

        // 命令总线：接收「右键菜单 / 全局热键」下发的命令
        _bus = new CmdBus();
        _bus.CommandReceived += OnShellCommand;
        _bus.HotkeyPressed += () => ShowSearch();
        Mark("托盘 + 菜单 + 命令总线");

        CreateBoxes();
        Mark("CreateBoxes（4 个格子窗口）");

        RefreshAll();
        Mark("RefreshAll（扫描 + 首次取图标）");

        StartWatcher();
        StartKeepAlive();
        ApplyHotkey();
        Mark("变更监听 + 保活 + 热键");

        ScheduleStartupSettle();

        // 默认即接管（与 DeskGo 行为一致：启动后原生图标隐藏，由格子接管呈现）
        // 但若 DeskGo 覆盖层在位，则接管「视觉上无效」且两套格子会叠加 —— 此时不自动接管。
        if (autoTakeover && located && !overlay && _settings.TakeoverOnStart) ToggleTakeover();
        if (located && _settings.EmbedOnStart) ToggleEmbed();

        // 由右键菜单启动时携带的命令（--cmd sort / search / sendto）
        if (!string.IsNullOrEmpty(initialCmd)) QueueInitialCommand(initialCmd!, initialArg);

        // 项数直接用 RefreshAll 刚扫出来的结果 —— 这里早先又调了一次 Scan() 只为取个计数
        _tray.BalloonTipTitle = "桌面整理 · 精简版";

        if (CrashGuard.RecoveredFromCrash)
        {
            // 上一次是崩溃 / 强杀退出的，桌面原生图标可能还停在隐藏状态
            _tray.BalloonTipText =
                "上次未正常退出，桌面原生图标可能还处于隐藏状态 —— 已自动还原。\n" +
                "崩溃详情见「打开数据目录」下的 logs 目录。";
            _tray.BalloonTipIcon = ToolTipIcon.Warning;
        }
        else if (_deskGo.Count > 0)
        {
            _tray.BalloonTipText =
                $"检测到「腾讯桌面整理」正在运行（{_deskGo[0].Name}.exe）。\n" +
                "两者会互相冲突：它的桌面覆盖层会盖住原生图标，导致接管失效。\n" +
                "建议退出 DeskGo 后右键托盘 → 重新检测并接管。";
            _tray.BalloonTipIcon = ToolTipIcon.Warning;
        }
        else
        {
            _tray.BalloonTipText = located
                ? $"已就绪：{_data.Boxes.Count} 个格子，桌面上 {_lastDirScanCount} 项。双击托盘搜索，右键托盘更多操作。"
                : "未定位桌面窗口，接管功能不可用（其余功能仍可预览）。";
            _tray.BalloonTipIcon = ToolTipIcon.Info;
        }
        if (!Program.Silent) _tray.ShowBalloonTip(6000);
    }

    // ───────── 启动命令 ─────────

    private void QueueInitialCommand(string cmd, string? arg)
    {
        var sc = cmd.ToLowerInvariant() switch
        {
            "sort" => ShellCommand.Sort,
            "search" => ShellCommand.Search,
            "sendto" => ShellCommand.SendTo,
            _ => (ShellCommand?)null,
        };
        if (sc == null) return;

        var t = new System.Windows.Forms.Timer { Interval = 700 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            OnShellCommand(sc.Value, arg);
        };
        t.Start();
    }

    /// <summary>右键菜单 / 热键命令入口（由 CmdBus 在 UI 线程回调）</summary>
    private void OnShellCommand(ShellCommand cmd, string? arg)
    {
        switch (cmd)
        {
            case ShellCommand.Sort:
                AutoSort();
                break;
            case ShellCommand.Search:
                ShowSearch(arg);
                break;
            case ShellCommand.SendTo:
                SendToBox(arg);
                break;
            case ShellCommand.Quit:
                ExitApp();
                break;
        }
    }

    /// <summary>把外部文件/文件夹收入桌面格子（对应右键菜单「送入桌面格子」）</summary>
    private void SendToBox(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return;

        var path = rawPath.Trim().Trim('"');
        if (!File.Exists(path) && !Directory.Exists(path)) return;

        var desktop = DesktopScanner.DesktopDirectories().FirstOrDefault(Directory.Exists);
        if (desktop == null) return;

        bool onDesktop = string.Equals(
            Path.GetDirectoryName(path)?.TrimEnd('\\'),
            desktop.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

        if (!onDesktop)
        {
            if (Program.Silent) return;

            var r = MessageBox.Show(
                $"把「{Path.GetFileName(path)}」移动到桌面并归入对应格子？\n\n目标：{desktop}",
                "送入桌面格子", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            var target = Path.Combine(desktop, Path.GetFileName(path));
            if (File.Exists(target) || Directory.Exists(target))
            {
                MessageBox.Show("桌面上已存在同名项目，未执行移动。", "送入桌面格子",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                if (Directory.Exists(path)) Directory.Move(path, target);
                else File.Move(path, target);
                path = target;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "移动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        var (type, boxId) = Rules.Resolve(path);
        RefreshAll();

        _tray.BalloonTipTitle = "已送入格子";
        _tray.BalloonTipText = $"「{Path.GetFileName(path)}」→ {Rules.TypeName(type)}（BoxID {boxId}）";
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        if (!Program.Silent) _tray.ShowBalloonTip(2500);
    }

    // ───────── 热键 ─────────

    private void ApplyHotkey()
    {
        bool ok = _bus.Register(_settings.SearchHotkey);

        _miHotkey.Text = string.IsNullOrWhiteSpace(_settings.SearchHotkey)
            ? "全局热键：未启用"
            : ok
                ? $"全局热键：{_settings.SearchHotkey}"
                : $"全局热键：{_settings.SearchHotkey}（注册失败）";

        if (!string.IsNullOrWhiteSpace(_settings.SearchHotkey) && !ok && !Program.Silent)
        {
            _tray.BalloonTipTitle = "桌面整理 · 精简版";
            _tray.BalloonTipText = $"全局热键「{_settings.SearchHotkey}」注册失败（可能已被其他程序占用）。可在设置中更换。";
            _tray.BalloonTipIcon = ToolTipIcon.Warning;
            _tray.ShowBalloonTip(4000);
        }
    }

    /// <summary>刷新状态栏里那条冲突提示文字</summary>
    private void UpdateConflictText()
    {
        if (_deskGo.Count > 0)
        {
            string procs = string.Join("、", _deskGo.Select(d => $"{d.Name}({d.Pid})"));
            _miConflict.Text = _overlayHwnd != IntPtr.Zero
                ? $"⚠ 与腾讯桌面整理冲突：{procs}"
                : $"⚠ 检测到腾讯桌面整理进程：{procs}";
            _miConflict.Visible = true;
        }
        else
        {
            _miConflict.Visible = false;
        }
    }

    /// <summary>重新检测 DeskGo 是否还在运行</summary>
    private void RecheckConflict()
    {
        _deskGo = Conflict.RunningDeskGo();
        Conflict.OverlayPresent(out _overlayHwnd, out _);
        UpdateConflictText();

        _tray.BalloonTipTitle = "桌面整理 · 精简版";
        if (_deskGo.Count == 0)
        {
            _tray.BalloonTipText = "未检测到腾讯桌面整理，接管功能可正常使用。";
            _tray.BalloonTipIcon = ToolTipIcon.Info;
        }
        else
        {
            _tray.BalloonTipText = "腾讯桌面整理仍在运行，请先退出它，否则接管无效。";
            _tray.BalloonTipIcon = ToolTipIcon.Warning;
        }
        _tray.ShowBalloonTip(4000);
    }

    // ───────── 数据落盘（带去抖）─────────

    /// <summary>
    /// 拖拽 / 改尺寸 / 内容变动触发的落盘去抖窗口。
    ///
    /// 为什么需要：<c>onLayoutChanged</c> 在拖动格子、拉伸四边的过程中被**每帧**调用，
    /// 每次都 <c>Save()</c> 的话，一次拖拽就是几百次「写临时文件 → 删主文件 → 改名 →
    /// 再写一份当日备份」，纯属拿磁盘赌手感。这里合并成「停手 300ms 后写一次」。
    ///
    /// 选择 300ms 的依据：比人手连续拖拽的帧间隔长得多（能合并整段拖拽），
    /// 又短到「松手后立刻强杀进程」也很可能已经落盘；且 <see cref="ExitApp"/> 会强制 flush，
    /// 所以不存在「拖完就退出、位置丢了」的窗口。
    /// </summary>
    private System.Windows.Forms.Timer? _saveDebounce;

    private const int SaveDebounceMs = 300;

    /// <summary>请求一次延迟落盘（幂等：窗口期内多次调用只写一次）</summary>
    private void SaveDebounced()
    {
        if (_store.ReadOnly) return;

        if (_saveDebounce == null)
        {
            _saveDebounce = new System.Windows.Forms.Timer { Interval = SaveDebounceMs };
            _saveDebounce.Tick += (_, _) =>
            {
                _saveDebounce!.Stop();
                SaveNow();
            };
        }

        _saveDebounce.Stop();   // 重新计时：以「最后一次变更」为起点
        _saveDebounce.Start();
    }

    /// <summary>立即落盘，并取消挂起的去抖写入（结构变更 / 退出时用）</summary>
    private void SaveNow()
    {
        try { _saveDebounce?.Stop(); } catch { }
        if (_store.ReadOnly) return;
        try { _store.Save(_data); } catch { }
    }

    // ───────── 格子 ─────────

    private void CreateBoxes()
    {
        foreach (var box in _data.Boxes) AddBoxForm(box);

        // ⚠ 这里**不能**调 ArrangeIfOverlapping：此刻每个格子都还是 0 项，
        //   量出来的尺寸是塌陷的「一行」尺寸，会误判重叠并把用户布局写盘覆盖。
        //   真正的重排由 RefreshAll() 在灌完内容后触发。
    }

    /// <summary>为一个格子模型建窗口并显示（新建格子与启动时共用同一条路径）</summary>
    private void AddBoxForm(DeskBox box)
    {
        if (!_settings.RememberCollapse) box.Collapsed = false;

        int id = box.Id;   // 闭包捕获，勿直接用 foreach 变量
        var form = new BoxForm(box, _icons, _settings,
            // 拖拽 / 改尺寸会在整个过程中反复触发 → 走去抖，停手 300ms 才落盘一次
            onLayoutChanged: () => { NoteActivity(); SaveDebounced(); },
            onItemsChanged: () => { NoteActivity(); RefreshAll(); SaveDebounced(); },
            onMoveToBox: MoveItemsToBox,
            onFilesDropped: files => DropFilesIntoBox(files, id),
            onClearAssign: ClearAssign,
            onRenameBox: RenameBoxById,
            onDeleteBox: DeleteBoxById,
            onCreateBox: CreateBoxInteractive,
            onReorderBox: ReorderBoxItems,
            onClearAssignMany: ClearAssignMany,
            snapper: this);

        _forms[box.Id] = form;

        // 建窗阶段（句柄创建 → 非客户区样式修正 → 首次尺寸）实测要白渲染 3 次，
        // 而紧接着 RefreshAll 就会灌内容并渲染一次 —— 让首帧延后到那时一起画。
        form.DeferFirstRender();

        // 内置格子先不显示：它此刻还是 0 项，而「空则自动隐藏」要等 RefreshAll 灌完内容
        // 才知道它到底该不该出现（否则每次启动都会闪一下空的系统格子）。
        if (!box.IsBuiltIn) form.Show();
    }

    // ───────── 格子的显隐 ─────────

    /// <summary>
    /// 应用一个格子的可见性。
    ///
    /// 内置格子（系统 / 我的文件夹 / 我的文件 / APP）**删不掉**，这不是疏漏而是必然：
    /// 它们的固定 ID 就是 <see cref="Rules.ResolveBoxId"/> 的落点（特殊项 → 系统格子、
    /// 文件夹 → 101、普通文件 → 102、快捷方式与可执行 → 104），删掉之后下一次扫描照样
    /// 算出同一个 ID，而 <see cref="DeskData.EnsureSystemBox"/> 还会在启动时把它造回来。
    /// 也就是说「删除」这个动作本身没有意义，只会让里面的项在重建之前无家可归。
    ///
    /// 所以换成「隐藏」来表达同一个诉求：**空着就不显示，桌面上一旦出现该类项就自动回来**。
    /// 位置与尺寸始终留在模型里，隐藏不会丢布局，也不参与对齐吸附（吸附只看 Visible 的格子）。
    /// </summary>
    private void ApplyBoxVisibility(int id, BoxForm form, bool autoHide)
    {
        if (autoHide) _autoHidden.Add(id);
        else _autoHidden.Remove(id);

        bool show = !_boxesHidden && !autoHide;
        if (form.Visible == show) return;

        form.Visible = show;
        // 隐藏期间，格子握着的那对 32bpp 位图（GDI+ 画布 + DIB section）纯属白占内存，
        // 等重新显示时按需重建。
        if (!show) form.FreeBuffers();
    }

    /// <summary>托盘上的只读状态行：内置格子是被「空则隐藏」收起来的，不是丢了</summary>
    private void UpdateAutoHiddenStatus()
    {
        var names = _autoHidden
            .Select(id => _data.FindBox(id)?.Name ?? $"#{id}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        _miAutoHidden.Visible = names.Count > 0;
        if (names.Count > 0)
            _miAutoHidden.Text = $"已隐藏空格子：{string.Join("、", names)}（有内容时自动出现）";
    }

    // ───────── 格子的新建 / 删除 / 重命名 ─────────

    /// <summary>交互式新建格子：问名字 → 自动挑不重叠的落点 → 建窗 → 落盘</summary>
    private void CreateBoxInteractive()
    {
        var name = PromptText("新建格子", "格子名称：", SuggestedBoxName());
        if (string.IsNullOrWhiteSpace(name)) return;

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var box = _data.CreateBox(name!, wa);
        SaveNow();

        AddBoxForm(box);
        RefreshAll();

        if (!Program.Silent)
        {
            _tray.BalloonTipTitle = "已新建格子";
            _tray.BalloonTipText = $"「{box.Name}」已创建。把桌面图标拖进去即可归入该格子。";
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(3000);
        }
    }

    private string SuggestedBoxName()
    {
        for (int i = 1; ; i++)
        {
            var n = "新建格子" + (i == 1 ? "" : i.ToString());
            if (_data.Boxes.All(b => !string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase)))
                return n;
        }
    }

    /// <summary>重命名格子：改模型 + 改窗口标题 + 落盘</summary>
    private void RenameBoxById(int id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!_data.RenameBox(id, name)) return;

        if (_forms.TryGetValue(id, out var f)) f.SetTitle(_data.FindBox(id)?.Name ?? name);
        SaveNow();
    }

    /// <summary>
    /// 删除格子：关窗口 + 移除模型 + 落盘。
    /// 格子里手工指派过的项已在 <see cref="DeskData.DeleteBox"/> 里一并释放，
    /// 刷新后它们会回到规则自动归类 —— 不会凭空消失。
    /// </summary>
    private void DeleteBoxById(int id)
    {
        if (!_data.DeleteBox(id)) return;

        if (_forms.TryGetValue(id, out var f))
        {
            _forms.Remove(id);
            DeferCloseForm(f);
        }

        SaveNow();
        RefreshAll();
    }

    /// <summary>
    /// 把「销毁格子窗体」推迟到当前消息处理完之后再做。
    ///
    /// 原因：这个动作通常是**从格子右键菜单的菜单项里同步调进来的**，此刻该
    /// <c>ContextMenuStrip</c> 还处在自己的关闭流程中，<c>ToolStripManager</c> 的
    /// 菜单过滤器仍把「下拉菜单 + 宿主窗体」当作活动对象。若就地 <c>Dispose()</c>
    /// 宿主窗体，过滤器稍后访问到已释放对象就会抛 <c>ObjectDisposedException</c>，
    /// 而且能**绕过** UI 线程异常兜底，直接把进程带走 —— 表现就是「删了格子就闪退」。
    ///
    /// 通过窗体自身的 <c>BeginInvoke</c> 投递：等当前消息（含菜单关闭）走完，
    /// 再回到 UI 线程销毁，此时已经没有任何人持有它。
    /// </summary>
    private static void DeferCloseForm(Form f)
    {
        try
        {
            if (f.IsDisposed) return;

            if (!f.IsHandleCreated) { f.Close(); f.Dispose(); return; }

            f.BeginInvoke((Action)(() => { try { f.Close(); f.Dispose(); } catch { } }));
        }
        catch
        {
            try { f.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 极简输入对话框（不引 Microsoft.VisualBasic，也不拖设计器）。
    /// 返回 null 表示用户取消。
    /// </summary>
    private static string? PromptText(string title, string label, string def)
    {
        using var f = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
            ClientSize = new Size(380, 118),
            BackColor = SystemColors.Window,
            Font = SystemFonts.MessageBoxFont,
        };

        var lb = new Label { Text = label, AutoSize = true, Location = new Point(14, 16) };
        var tb = new TextBox { Text = def, Location = new Point(14, 40), Width = 352 };
        var ok = new Button
        {
            Text = "确定", DialogResult = DialogResult.OK,
            Location = new Point(210, 76), Size = new Size(76, 28),
        };
        var cancel = new Button
        {
            Text = "取消", DialogResult = DialogResult.Cancel,
            Location = new Point(290, 76), Size = new Size(76, 28),
        };

        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.Controls.AddRange(new Control[] { lb, tb, ok, cancel });

        tb.SelectAll();
        return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
    }

    /// <summary>
    /// 格子尺寸改为「贴合内容」后，旧的位置数据可能让格子互相压住。
    /// 检测到重叠时按「列优先、纵向堆叠、超出工作区高度就换列」重排一次；
    /// 没有重叠则完全尊重用户拖出来的位置。
    ///
    /// ⚠ 前置条件：必须在格子内容灌好之后再调用。
    ///   CreateBoxes() 刚建完窗口时每个格子都是 0 项，此时 MeasureContent 量出来的是
    ///   「一行」的塌陷尺寸；拿它去判重叠会得出错误结论，而且本函数会 _store.Save()
    ///   把重排结果写盘 —— 等于把用户拖好的布局永久抹掉。
    ///   所以真正的调用点在 RefreshAll() 末尾（见 <see cref="RefreshAll"/>）。
    /// </summary>
    private void ArrangeIfOverlapping()
    {
        var boxes = _data.Boxes;
        if (boxes.Count < 2) return;

        bool overlap = false;
        for (int i = 0; i < boxes.Count && !overlap; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                var a = new Rectangle(boxes[i].X, boxes[i].Y, boxes[i].Width, boxes[i].Height);
                var b = new Rectangle(boxes[j].X, boxes[j].Y, boxes[j].Width, boxes[j].Height);
                if (a.IntersectsWith(b)) { overlap = true; break; }
            }
        if (!overlap) return;

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        const int margin = 40, gap = 16;

        int colX = wa.Left + margin;
        int colY = wa.Top + margin;
        int colW = 0;

        foreach (var box in boxes)
        {
            int w = Math.Max(120, box.Width);
            int h = Math.Max(60, box.Height);

            // 本列放不下就换一列
            if (colY > wa.Top + margin && colY + h > wa.Bottom - margin)
            {
                colX += colW + gap;
                colY = wa.Top + margin;
                colW = 0;
            }

            // 横向兜底：放不下就回到第一列
            if (colX + w > wa.Right - margin) { colX = wa.Left + margin; colY = wa.Top + margin; }

            box.X = colX;
            box.Y = colY;

            colY += h + gap;
            colW = Math.Max(colW, w);
        }

        SaveNow();
        foreach (var box in boxes)
            if (_forms.TryGetValue(box.Id, out var f)) f.MoveTo(box.X, box.Y);
    }

    /// <summary>诊断：打印格子模型坐标与窗口实际屏幕坐标（排查 DPI 缩放 / 定位偏差）</summary>
    public void DumpLayout()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea;
        var bo = Screen.PrimaryScreen?.Bounds;
        Console.WriteLine($"主屏 Bounds={bo}  WorkingArea={wa}");
        foreach (var (id, f) in _forms.OrderBy(kv => kv.Key))
        {
            var m = f.Model;
            Console.WriteLine(
                $"  Box {id,-4} 模型=({m.X},{m.Y}) {m.Width}×{m.Height} 折叠={m.Collapsed} " +
                $"| 窗口={f.Bounds} DeviceDpi={f.DeviceDpi} Opacity={f.Opacity:0.00}");
        }
    }

    // ───────── 手工整理（拖拽换格）─────────

    /// <summary>
    /// 把一项或一批项改派到另一个格子 —— 即 DeskGo 里「把图标拖进另一个格子」。
    /// 批量（多选拖拽 / 菜单「以此格子为准」）也走这里：全部改完只落盘 / 刷新一次。
    /// </summary>
    private void MoveItemsToBox(IReadOnlyList<string> paths, int boxId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (!seen.Add(p)) continue;
            var (type, _) = Rules.Resolve(p);
            _data.AssignBox(p, Rules.DisplayName(p), type, boxId);
        }
        SaveNow();
        RefreshAll();
    }

    /// <summary>
    /// 记录某个格子内的新顺序（格子内拖动排序落盘）。
    ///
    /// 不触发 <see cref="RefreshAll"/>：视图上图标已经摆成新顺序了，
    /// 再刷一遍纯属白烧 CPU；而且内容指纹不含顺序，非强制刷新本来也会被跳过。
    /// </summary>
    private void ReorderBoxItems(int boxId, IReadOnlyList<string> orderedPaths)
    {
        _data.ReorderBox(boxId, orderedPaths);
        SaveNow();
    }

    /// <summary>清除手工指派，回到规则自动归类</summary>
    private bool ClearAssign(string path)
    {
        _data.ClearAssign(path);
        SaveNow();
        return true;
    }

    // ───────── 拖动吸附与对齐参考线 ─────────

    /// <summary>对齐参考线覆盖层 —— 第一次真正用到才建，免得白起一个全屏窗口</summary>
    private SnapOverlay? _snapOverlay;

    /// <summary>
    /// 请求吸附：把格子的「想去位置」和【其它可见格子 + 当前屏工作区】的
    /// 左 / 中 / 右、上 / 中 / 下三组锚点两两配对，取位移最小的那对（阈值内）。
    /// </summary>
    public Point Snap(int selfId, Rectangle proposed)
    {
        double scale = _forms.TryGetValue(selfId, out var self) ? self.DeviceDpi / 96.0 : 1.0;
        int tol = Math.Max(4, (int)Math.Round(8 * scale));

        var refs = _forms
            .Where(kv => kv.Key != selfId && kv.Value.Visible)
            .Select(kv => kv.Value.Bounds)
            .ToList();
        refs.Add(Screen.FromPoint(proposed.Location).WorkingArea);

        int[] selfX = { proposed.Left, proposed.Left + proposed.Width / 2, proposed.Right };
        int[] selfY = { proposed.Top, proposed.Top + proposed.Height / 2, proposed.Bottom };

        int dx = 0, distX = tol + 1, lineX = 0;
        Rectangle hitX = Rectangle.Empty;
        foreach (var r in refs)
        {
            foreach (int a in new[] { r.Left, r.Left + r.Width / 2, r.Right })
                foreach (int b in selfX)
                {
                    int ad = Math.Abs(a - b);
                    if (ad > tol || ad >= distX) continue;
                    distX = ad; dx = a - b; lineX = a; hitX = r;
                }
        }

        int dy = 0, distY = tol + 1, lineY = 0;
        Rectangle hitY = Rectangle.Empty;
        foreach (var r in refs)
        {
            foreach (int a in new[] { r.Top, r.Top + r.Height / 2, r.Bottom })
                foreach (int b in selfY)
                {
                    int ad = Math.Abs(a - b);
                    if (ad > tol || ad >= distY) continue;
                    distY = ad; dy = a - b; lineY = a; hitY = r;
                }
        }

        var snapped = new Rectangle(proposed.Left + dx, proposed.Top + dy,
            proposed.Width, proposed.Height);

        // 参考线跨度取「被对齐的两者之外包」，两端各留 10px 出头，看着像一条完整的对齐轴
        (int X, int Top, int Bottom)? v = distX <= tol
            ? (lineX, Math.Min(hitX.Top, snapped.Top) - 10,
                      Math.Max(hitX.Bottom, snapped.Bottom) + 10)
            : null;

        (int Y, int Left, int Right)? h = distY <= tol
            ? (lineY, Math.Min(hitY.Left, snapped.Left) - 10,
                      Math.Max(hitY.Right, snapped.Right) + 10)
            : null;

        if (v is null && h is null) { _snapOverlay?.Clear(); return snapped.Location; }

        _snapOverlay ??= new SnapOverlay();
        _snapOverlay.Update(v, h);
        return snapped.Location;
    }

    public void EndSnap() => _snapOverlay?.Clear();

    /// <summary>批量清除手工指派（多选后「按规则自动归类」）</summary>
    private void ClearAssignMany(IReadOnlyList<string> paths)
    {
        foreach (var p in paths) _data.ClearAssign(p);
        SaveNow();
        RefreshAll();
    }

    /// <summary>从资源管理器把文件拖进格子：先落到桌面，再指派到该格子</summary>
    private void DropFilesIntoBox(IReadOnlyList<string> files, int boxId)
    {
        var desktop = DesktopScanner.DesktopDirectories().FirstOrDefault(Directory.Exists);
        if (desktop == null) return;

        int ok = 0, skip = 0;
        foreach (var raw in files)
        {
            var path = raw.Trim().Trim('"');
            bool isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) continue;

            var target = Path.Combine(desktop, Path.GetFileName(path));
            if (!string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(target) || Directory.Exists(target)) { skip++; continue; }
                try
                {
                    if (isDir) Directory.Move(path, target);
                    else File.Move(path, target);
                }
                catch { skip++; continue; }
            }

            var (type, _) = Rules.Resolve(target);
            _data.AssignBox(target, Rules.DisplayName(target), type, boxId);
            ok++;
        }

        SaveNow();
        RefreshAll();

        if (Program.Silent) return;
        _tray.BalloonTipTitle = "桌面整理 · 精简版";
        _tray.BalloonTipText = skip == 0
            ? $"已收纳 {ok} 项到「{_data.FindBox(boxId)?.Name ?? "格子"}」。"
            : $"已收纳 {ok} 项，{skip} 项因同名或权限被跳过。";
        _tray.BalloonTipIcon = skip == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning;
        _tray.ShowBalloonTip(3000);
    }

    private void ToggleHideBoxes()
    {
        NoteActivity();
        _boxesHidden = !_boxesHidden;

        foreach (var (id, f) in _forms)
        {
            // 「显示所有格子」要尊重「空则自动隐藏」：空的系统格子不该被这一下顺带拽出来
            bool show = !_boxesHidden && !_autoHidden.Contains(id);
            f.Visible = show;
            // 隐藏期间每个格子握着的一对 32bpp 位图（GDI+ 画布 + DIB section）纯属浪费，
            // 直接释放；重新显示时 OnPaint 会按需重建。
            if (!show) f.FreeBuffers();
        }

        _miHideBoxes.Text = _boxesHidden ? "显示所有格子" : "隐藏所有格子";
    }

    /// <summary>一键整理：按规则重新扫描并分格</summary>
    private void AutoSort()
    {
        NoteActivity();
        RefreshAll();
        foreach (var id in _forms.Keys) _data.Reindex(id);
        SaveNow();

        var (total, folders, shortcuts, files, special) = DesktopScanner.Summary(DesktopScanner.Scan());
        _tray.BalloonTipTitle = "整理完成";
        _tray.BalloonTipText =
            $"共 {total} 项 → 文件夹 {folders} · 快捷方式 {shortcuts} · 文件 {files} · 特殊 {special}";
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        if (!Program.Silent) _tray.ShowBalloonTip(3000);
    }

    /// <summary>
    /// 上次灌入格子时的内容指纹。
    ///
    /// 为什么要它：桌面文件监听（含 <c>NotifyFilters.LastWrite</c>）触发很频繁 ——
    /// 随便复制一个大文件、改个文件内容都会来一发。旧实现每次都老老实实分组、
    /// 重建图标列表、重排、重绘一遍整屏，白白烧 CPU。
    /// 指纹相同说明「项和归属都没变」，这一次刷新就可以整个跳过。
    /// </summary>
    private string _contentFingerprint = "";

    /// <summary>虚拟项发现结果的缓存（<see cref="CollectVirtualItems"/>）</summary>
    private List<(string Path, string Name)> _virtualCache = new();
    private long _virtualTick;
    private const long VirtualTtlMs = 30_000;

    /// <summary>
    /// 最近一次 <see cref="RefreshAll"/> 枚举到的**磁盘项数**（不含虚拟项）。
    /// 托盘气泡要用这个数；早先那里又调了一次 <c>DesktopScanner.Scan()</c> ——
    /// 启动时白扫一遍整个桌面目录（两次 DirectoryInfo 枚举 + 逐项分类）。
    /// </summary>
    private int _lastDirScanCount;

    /// <summary>
    /// 重新扫描桌面并灌入格子。
    /// </summary>
    /// <param name="force">
    /// true = 无论如何都重建（设置变更、手动「重新扫描」、建/删格子等）；
    /// false = 内容指纹没变就跳过重排与重绘（文件监听触发的常规刷新走这一档）。
    /// </param>
    /// <summary>
    /// 布局 / 刷新追踪（诊断用，默认关闭、零开销）。
    /// 打开方式：设环境变量 <c>DESKLITE_TRACE_LAYOUT=1</c> 再启动，
    /// 追踪写进 <c>%APPDATA%\DeskLite\logs</c>，用来定位「格子空 / 尺寸不对」这类问题。
    /// </summary>
    private static readonly bool TraceLayout =
        Environment.GetEnvironmentVariable("DESKLITE_TRACE_LAYOUT") == "1";

    private static void Trace(string s)
    {
        if (TraceLayout) CrashGuard.LogText("[layout] " + s);
    }

    private void RefreshAll(bool force = true)
    {
        NoteActivity();

        var items = DesktopScanner.Scan();
        _lastDirScanCount = items.Count;   // 托盘气泡复用这个数，不再重复扫一遍目录
        Mark("  ├ 扫描桌面目录");

        int virtualCount = CollectVirtualItems(items, force);
        if (virtualCount > 0) Mark($"  ├ 补入虚拟项 {virtualCount} 个");

        // 手工指派优先于规则自动归类（对应 DeskGo 里被拖进别的格子的项）
        foreach (var it in items)
            if (_data.BoxOverride(it.Path) is int overridden) it.BoxId = overridden;

        if (!force)
        {
            var fp = Fingerprint(items);
            if (string.Equals(fp, _contentFingerprint, StringComparison.Ordinal))
            {
                Mark("  └ 内容无变化，跳过重排/重绘");
                Trace($"RefreshAll force=False 指纹未变（{items.Count} 项），跳过");
                return;
            }
            Trace($"RefreshAll force=False 指纹变化 → 继续（{items.Count} 项）");
            _contentFingerprint = fp;
        }
        else
        {
            _contentFingerprint = Fingerprint(items);
        }

        var grouped = DesktopScanner.GroupByBox(items);
        Mark("  ├ 按规则分格");

        // 格子内顺序：用户拖过的位次优先，从没排过的项排在后面并保持扫描顺序。
        //
        // 用 OrderBy 而不用 List.Sort —— 后者是不稳定排序，会让「没排过」的那批项
        // 每次刷新都换一个顺序，看起来像格子自己乱跳。键是 (0, 位次) / (1, 原下标)。
        foreach (var id in grouped.Keys.ToList())
        {
            var order = _data.RecordedOrder(id);
            if (order.Count == 0) continue;

            grouped[id] = grouped[id]
                .Select((it, idx) => (it, idx))
                .OrderBy(t => order.TryGetValue(t.it.Path, out var p) ? (0, p) : (1, t.idx))
                .Select(t => t.it)
                .ToList();
        }

        // 逐个格子灌内容：任何一个格子出错都不该让整轮刷新失败
        // （以前这里没有隔离，一个格子抛异常就会把整轮刷新连根带走）
        foreach (var (id, form) in _forms)
        {
            try
            {
                var list = grouped.TryGetValue(id, out var l) ? l : new List<DiscoveredItem>();

                // 内置格子空则自动隐藏（它删不掉，详见 ApplyBoxVisibility 的说明）。
                // 这一步必须排在 SetItems 之前：可见性先定下来，灌内容时才能一次画到位。
                ApplyBoxVisibility(id, form, autoHide: _data.FindBox(id)?.IsBuiltIn == true && list.Count == 0);

                Trace($"灌入 格子{id} ← {list.Count} 项（窗体 {form.Width}×{form.Height}，可见={form.Visible}）");
                form.SetItems(list);

                if (!form.Visible) form.FreeBuffers();
            }
            catch (Exception ex)
            {
                CrashGuard.Log(ex, $"RefreshAll → 格子 {id} 灌入内容失败");
            }
        }

        UpdateAutoHiddenStatus();

        Trace($"RefreshAll force={force} 扫描={items.Count} 虚拟={virtualCount} " +
              $"分组=[{string.Join(", ", grouped.OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Value.Count}"))}] " +
              $"窗体=[{string.Join(",", _forms.Keys.OrderBy(k => k))}]");

        // 内容灌好了，模型里的 Width/Height 已经是真实尺寸 —— 这时判重叠才准。
        // 只在首次灌入后做一次：之后如果用户自己把格子叠在一起，那是他的选择，不该再被重排。
        if (!_arrangedOnce)
        {
            _arrangedOnce = true;
            try { ArrangeIfOverlapping(); }
            catch (Exception ex) { CrashGuard.Log(ex, "ArrangeIfOverlapping"); }
        }

        Mark("  └ 灌入格子（建图标 + 重绘）");
    }

    /// <summary>
    /// 内容指纹：路径 + 类型码 + 归属格子，按路径排序后拼接。
    /// 排序是为了让「扫描顺序不同但内容相同」也判为一致（目录枚举顺序并不稳定）。
    /// </summary>
    private static string Fingerprint(List<DiscoveredItem> items)
    {
        if (items.Count == 0) return "";

        var sb = new System.Text.StringBuilder(items.Count * 24);
        foreach (var it in items.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
            sb.Append(it.Path).Append('').Append((int)it.Type).Append('').Append(it.BoxId).Append('');
        return sb.ToString();
    }

    /// <summary>
    /// 补入 Shell 虚拟项（此电脑 / 回收站 / 网络），带 30 秒缓存。
    ///
    /// 直接调 <see cref="DesktopScanner.AppendVirtualItems"/> 会去读 explorer 的
    /// 原生 ListView —— 那是**跨进程**的 LVM_GETITEMTEXT（每项一次 SendMessage + 
    /// 跨进程内存读写），是整条刷新链路里最贵的一步。
    /// 而虚拟项集合几乎不变（只有用户在「桌面图标设置」里开关时才变），
    /// 所以缓存 30 秒；手动「重新扫描」和其它 force 刷新会绕过缓存。
    /// </summary>
    private int CollectVirtualItems(List<DiscoveredItem> items, bool force)
    {
        long now = Environment.TickCount64;

        if (!force && _virtualCache.Count > 0 && now - _virtualTick < VirtualTtlMs)
        {
            var real = new HashSet<string>(items.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var (p, name) in _virtualCache)
            {
                if (real.Contains(name)) continue;   // 磁盘上真有同名项时以真实项为准
                items.Add(new DiscoveredItem
                {
                    Path = p, Name = name,
                    Type = ItemType.Special, BoxId = BoxIds.System, IsVirtual = true,
                });
                n++;
            }
            return n;
        }

        int added = DesktopScanner.AppendVirtualItems(items, _host);
        if (added > 0)
        {
            _virtualCache = items.Where(i => i.IsVirtual)
                                 .Select(i => (i.Path, i.Name))
                                 .ToList();
            _virtualTick = now;
        }
        return added;
    }

    // ───────── 接管 ─────────

    private void ToggleTakeover()
    {
        // explorer 可能重启过 —— 先确认句柄链还是有效的，失效就重新定位
        if (!_host.EnsureLocated())
        {
            if (!Program.Silent)
                MessageBox.Show("未定位到桌面窗口链，接管功能暂不可用。",
                    "桌面整理 · 精简版", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 首次接管前若发现 DeskGo 覆盖层在位，先提醒（接管会「看起来无效」）
        if (!_takeover && _overlayHwnd != IntPtr.Zero && !Program.Silent)
        {
            var r = MessageBox.Show(
                "检测到「腾讯桌面整理」的桌面覆盖层正在运行。\n\n" +
                "它的覆盖层会把原生桌面图标完整盖住，因此本程序隐藏原生图标后" +
                "画面上不会有变化（实际上已生效），两套格子还会叠在一起。\n\n" +
                "建议：先退出腾讯桌面整理（托盘右键退出），再使用本程序接管。\n\n" +
                "仍要继续接管吗？",
                "桌面整理 · 精简版",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
        }

        _takeover = !_takeover;
        if (_takeover) _host.HideNativeIcons();
        else _host.ShowNativeIcons();

        // 刚接管的一段时间最容易被 explorer 打回，重置为高频巡检
        _cleanTicks = 0;
        SetKeepAliveInterval(KeepAliveFastMs);

        _miTakeover.Text = _takeover ? "✔ 已接管（隐藏原生图标）" : "接管桌面图标（隐藏原生图标）";
    }

    private void ToggleEmbed()
    {
        if (!_host.Located) _host.Locate();
        _embedded = !_embedded;
        _embedHwnd = _embedded ? _host.Progman : IntPtr.Zero;

        foreach (var f in _forms.Values) f.EmbedInto(_embedded ? _host.Progman : IntPtr.Zero);
        _miEmbed.Text = _embedded ? "✔ 格子已嵌入桌面层" : "格子嵌入桌面层（实验）";
    }

    /// <summary>诊断用：把「格子嵌入桌面层」切到指定状态（已是该状态则不动）</summary>
    public void SetEmbedForTest(bool on)
    {
        if (on != _embedded) ToggleEmbed();
    }

    // ───────── 搜索 ─────────

    private void ShowSearch(string? keyword = null)
    {
        NoteActivity();
        if (_search == null || _search.IsDisposed)
        {
            _search = new SearchForm(_icons, () => DesktopScanner.Scan());
            // 搜索框关掉意味着「用户这一轮操作结束了」，顺手把垃圾收掉
            _search.FormClosed += (_, _) => NoteActivity();
        }

        _search.Reload();
        if (!string.IsNullOrEmpty(keyword)) _search.SetKeyword(keyword);

        _search.Show();
        _search.Activate();
        _search.BringToFront();
    }

    // ───────── 设置 ─────────

    private void ShowSettings(int page)
    {
        NoteActivity();
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            _settingsForm.BringToFront();
            return;
        }

        _settingsForm = new SettingsForm(_settings, ApplySettings, page);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
    }

    /// <summary>设置保存后回灌到运行中的各组件</summary>
    private void ApplySettings(AppSettings s)
    {
        _settings = s;

        // 图标尺寸变化 → 重建图标缓存
        if (_icons.Size != Math.Clamp(s.IconSize, 16, 128))
        {
            var old = _icons;
            _icons = new IconCache(Math.Clamp(s.IconSize, 16, 128));

            // 先把新缓存交给所有格子，再释放旧的 —— 顺序反了会让视图抱着已释放的位图
            foreach (var f in _forms.Values) f.SetIconCache(_icons);
            try { old.Dispose(); } catch { }
        }

        foreach (var f in _forms.Values) f.ApplySettings(s);
        RefreshAll();

        // RefreshAll 里已经把新尺寸的图标都提取完了 —— 中间位图池这一轮也用不上了，还掉
        try { IconCache.ReleasePool(); } catch { }

        ApplyHotkey();

        if (!s.RememberCollapse)
        {
            foreach (var box in _data.Boxes) box.Collapsed = false;
            SaveNow();
        }
    }

    // ───────── 文件变更监听 ─────────

    private void StartWatcher()
    {
        StopWatchers();   // 重建路径（OnWatcherError）会二次进入，先清干净
        try { _debounce?.Stop(); _debounce?.Dispose(); } catch { }

        _debounce = new System.Windows.Forms.Timer { Interval = 900 };
        _debounce.Tick += (_, _) =>
        {
            _debounce!.Stop();
            // 监听触发的刷新走「无变化就跳过」这一档：桌面上一有风吹草动就全量重排太费了
            RefreshAll(force: false);
        };

        // FileSystemWatcher 的事件跑在**线程池线程**上，而 WinForms 定时器必须由 UI 线程驱动。
        // 这里在 UI 线程先 Start 一次让定时器把隐式宿主窗口建在 UI 线程上（随后立刻停掉），
        // 否则首次 Start 发生在工作线程时，那个窗口会被建在没有消息循环的线程上，
        // 症状就是「文件变动事件收到了，去抖却永远不触发」。
        _debounce.Start();
        _debounce.Stop();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        // 监听范围必须与扫描范围一致：Scan() 会同时扫「用户桌面」和「公共桌面」，
        // 只监听第一个的话，往公共桌面丢东西就不会实时反映。
        foreach (var dir in DesktopScanner.DesktopDirectories().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var w = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                w.Created += OnFsChanged;
                w.Deleted += OnFsChanged;
                w.Renamed += OnFsChanged;
                w.Changed += OnFsChanged;

                // ⚠ 必须处理 Error：短时间内大量文件变动会把监听缓冲区打满，
                //   此时 .NET 会在**线程池线程**上抛 InternalBufferOverflowException ——
                //   那是未捕获异常，会直接把整个进程带走（表现就是莫名闪退 + 桌面图标消失）。
                //   这里的标准做法是记一笔日志、把监听器丢掉重建。
                w.Error += OnWatcherError;

                w.EnableRaisingEvents = true;
                _watchers.Add(w);
                Trace($"文件监听已启用：{dir}");
            }
            catch (Exception ex)
            {
                CrashGuard.Log(ex, $"启用文件监听失败：{dir}");
            }
        }
    }

    private void OnFsChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            Trace($"fs {e.ChangeType} {e.FullPath}（去抖 {_debounce?.Interval}ms）");

            // 事件来自线程池线程，一律弹回 UI 线程再碰定时器 / 刷新
            if (_ui != null && _ui != SynchronizationContext.Current)
                _ui.Post(_ => KickDebounce(), null);
            else
                KickDebounce();
        }
        catch (Exception ex)
        {
            CrashGuard.Log(ex, "OnFsChanged");
        }
    }

    /// <summary>重启桌面变更的去抖计时器（必须在 UI 线程调用）</summary>
    private void KickDebounce()
    {
        try
        {
            _debounce?.Stop();
            _debounce?.Start();
        }
        catch (Exception ex)
        {
            CrashGuard.Log(ex, "KickDebounce");
        }
    }

    /// <summary>关掉全部桌面监听器（重建 / 退出时用）</summary>
    private void StopWatchers()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
    }

    /// <summary>监听器自身出错（最常见的是缓冲区溢出）：重建一个，别让进程陪葬</summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        CrashGuard.Log(e.GetException(), "FileSystemWatcher.Error（监听缓冲区可能已溢出）");

        void Rebuild()
        {
            try
            {
                StopWatchers();
                StartWatcher();      // 重建：新监听器带一块干净的缓冲区
            }
            catch (Exception ex)
            {
                CrashGuard.Log(ex, "重建 FileSystemWatcher 失败");
            }
        }

        // Error 也来自线程池线程。Dispose + 重建必须在 UI 线程做：StartWatcher 会重新
        // 创建 WinForms 定时器，在工作线程建出来的定时器收不到 WM_TIMER。
        if (_ui != null && _ui != SynchronizationContext.Current) _ui.Post(_ => Rebuild(), null);
        else Rebuild();
    }

    // ───────── 接管保活 ─────────

    /// <summary>
    /// explorer 有时会异步把桌面 ListView 恢复为可见，导致接管失效。
    /// 这里低频巡检保活，并做**自适应退避**以压低空闲开销：
    ///   · 刚接管 / 刚发生过「被恢复」→ 800ms 高频（这段时间最容易被打回）
    ///   · 连续 20 次巡检都无事 → 退到 3000ms（每 3 秒 2 次极轻量的 API 调用）
    ///   · 一旦真的发生恢复，立即回到高频
    /// 每次巡检只是 IsWindowVisible + 必要的 ShowWindow，不做任何分配。
    /// </summary>
    private const int KeepAliveFastMs = 800;
    private const int KeepAliveSlowMs = 3000;
    private int _cleanTicks;

    private void StartKeepAlive()
    {
        _keepAlive = new System.Windows.Forms.Timer { Interval = KeepAliveFastMs };
        _keepAlive.Tick += (_, _) =>
        {
            try
            {
                // explorer 重启（或桌面被重建）会让 Progman / ListView 句柄全部作废。
                // 不处理的话表现是「接管偶尔自己掉了、嵌入的格子不见了」——
                // 这里先重新定位，再把接管与嵌入重新施加一遍。
                if (!_host.EnsureLocated())
                {
                    if (++_cleanTicks >= 20) SetKeepAliveInterval(KeepAliveSlowMs);
                    return;
                }
                ReapplyAfterRelocate();

                if (_host.EnsureIconsHidden())
                {
                    // 真的被 explorer 恢复了 → 回到高频盯一会儿
                    _cleanTicks = 0;
                    SetKeepAliveInterval(KeepAliveFastMs);
                }
                else if (++_cleanTicks == 20)
                {
                    SetKeepAliveInterval(KeepAliveSlowMs);
                }
            }
            catch (Exception ex)
            {
                // 巡检绝不能把进程带走 —— 记一场日志继续跑
                CrashGuard.Log(ex, "KeepAlive");
            }
        };
        _keepAlive.Start();
    }

    /// <summary>
    /// 桌面窗口链换了一套句柄之后，把「嵌入桌面层」按当前状态重新施加一次。
    /// 只在父窗口真的变了的时候动手（正常运行时只是一次 IntPtr 比较）。
    /// 接管的重新隐藏由 <see cref="DesktopHost.EnsureIconsHidden"/> 自己负责。
    /// </summary>
    private void ReapplyAfterRelocate()
    {
        if (!_embedded || _host.Progman == _embedHwnd) return;

        _embedHwnd = _host.Progman;
        foreach (var f in _forms.Values)
        {
            try { f.EmbedInto(_host.Progman); } catch { }
        }
    }

    private void SetKeepAliveInterval(int ms)
    {
        if (_keepAlive == null || _keepAlive.Interval == ms) return;
        _keepAlive.Stop();
        _keepAlive.Interval = ms;
        _keepAlive.Start();
    }

    // ───────── 资源占用：全自动，界面上没有也不需要「释放内存」按钮 ─────────
    //
    // 设计原则：**空闲时才整理，忙的时候绝不插手**。
    // 反复地 GC + TrimWorkingSet 会把正在用的页面丢掉再换入，反而更慢更费 CPU；
    // 所以这里用「静默 4 秒 + 冷却 30 秒 + 低于水位线不动」三道闸门把它约束住，
    // 用户平时根本感知不到，而常驻占用始终贴着下限。

    /// <summary>最近一次「有实质动作」的时刻（Environment.TickCount64，毫秒）</summary>
    private long _lastActivity;

    /// <summary>自上次整理以来是否产生过值得回收的垃圾</summary>
    private bool _memDirty = true;

    private long _lastSettleTick;
    private System.Windows.Forms.Timer? _idleTimer;

    private const long IdleAfterMs = 4_000;        // 静默多久算「空闲」
    private const long SettleCooldownMs = 30_000;  // 两次整理之间的最小间隔
    private const long SettleMaxIdleMs = 180_000;  // 一直空闲时，至多每 3 分钟补一次
    private const long LowWaterBytes = 26L * 1024 * 1024;   // 工作集低于此值就别折腾了
    private const int IdlePollMs = 5_000;          // 巡检间隔（一次计数器读取，开销可忽略）

    /// <summary>记一次「活动」：推迟下一次整理，并标记为「有垃圾可回收」</summary>
    private void NoteActivity()
    {
        _lastActivity = Environment.TickCount64;
        _memDirty = true;
    }

    /// <summary>
    /// 首屏渲染完成、界面稳定之后做一次**彻底**整理（压缩 + 交还工作集）。
    /// 必须等所有格子第一次画完再做，否则刚交还的页面马上又被首屏渲染换回来。
    /// </summary>
    private void ScheduleStartupSettle()
    {
        var t = new System.Windows.Forms.Timer { Interval = 2000 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();

            // 首屏已经画完 —— 图标提取用的那些中间位图再也不会被用到，
            // 先把复用池整个还掉（GDI+ 的私有堆只涨不缩，不主动释放就永远收不回），
            // 再做一次彻底回收 + 交还工作集，两步合成一次整理。
            try { IconCache.ReleasePool(); } catch { }

            SettleMemory(full: true);
            _lastSettleTick = Environment.TickCount64;
            _memDirty = false;

            // 之后交给空闲巡检常驻维持
            StartIdleSettle();
        };
        t.Start();
    }

    private void StartIdleSettle()
    {
        if (_idleTimer != null) return;
        _idleTimer = new System.Windows.Forms.Timer { Interval = IdlePollMs };
        _idleTimer.Tick += (_, _) => TryIdleSettle();
        _idleTimer.Start();
    }

    /// <summary>空闲巡检：三道闸门都过了才动手</summary>
    private void TryIdleSettle()
    {
        long now = Environment.TickCount64;

        if (now - _lastActivity < IdleAfterMs) return;              // ① 还在忙
        if (now - _lastSettleTick < SettleCooldownMs) return;       // ② 冷却未过
        if (!_memDirty && now - _lastSettleTick < SettleMaxIdleMs) return;   // ③ 没垃圾就不用管

        var (ws, _) = MemInfo.Counters();
        if (ws > 0 && ws < LowWaterBytes)
        {
            // 已经很小了：再 trim 只会制造缺页，不划算
            _lastSettleTick = now;
            _memDirty = false;
            return;
        }

        SettleMemory(full: false);
        _lastSettleTick = now;
        _memDirty = false;
    }

    /// <summary>
    /// 回收托管堆垃圾 + 把用不到的物理页面交还系统（幂等，可安全多次调用）。
    ///
    /// <paramref name="full"/> = false 时只收 0/1 代且不压缩：常驻对象都在第 2 代，
    /// 不会被搬动，耗时在亚毫秒级 —— 这是空闲巡检用的档位。
    /// = true 时做完整的两轮压缩回收（启动后 / 用户手动触发时用）。
    /// </summary>
    public static void SettleMemory(bool full = true)
    {
        try
        {
            if (full)
            {
                // 启动阶段产生的临时对象（扫描结果、候选图标位图、序列化缓冲）此时都已不可达，
                // 收两轮（第二轮处理第一轮终结器释放出来的对象）后再交给系统更彻底。
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            else
            {
                GC.Collect(1, GCCollectionMode.Forced, blocking: true, compacting: false);
            }
        }
        catch { }

        try { Native.TrimWorkingSet(); } catch { }
    }

    /// <summary>
    /// 诊断用：等价于在设置面板里拖动「毛玻璃」滑块并确定 ——
    /// 只改 <see cref="AppSettings.GlassOpacity"/> 并走完整的 <see cref="BoxForm.ApplySettings"/>，
    /// 用来在真实运行路径（已接管 + 已嵌入桌面层）下观察透明度对格子的实际影响。
    /// </summary>
    public void SetGlassForTest(int glass)
    {
        _settings.GlassOpacity = Math.Clamp(glass, 30, 100);
        foreach (var f in _forms.Values)
        {
            try { f.ApplySettings(_settings); } catch { }
        }
    }

    /// <summary>
    /// 诊断用：报告各格子的分层窗口状态与真实屏幕矩形。
    ///
    /// 注意 <c>Opacity</c> 现在恒为 1.00 —— 逐像素 alpha 改造后不再使用整窗不透明度，
    /// 底板的透明度改由像素的 alpha 通道表达，所以这里额外把 <c>GlassOpacity</c> 打出来。
    /// </summary>
    public string DescribeForms()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (id, f) in _forms)
        {
            long ex = Win32.Native.GetWindowLongPtr(f.Handle, Win32.Native.GWL_EXSTYLE).ToInt64();
            bool layered = (ex & Win32.Native.WS_EX_LAYERED) != 0;
            Win32.Native.GetWindowRect(f.Handle, out var r);
            IntPtr parent = Win32.Native.GetParent(f.Handle);
            var (ulwOk, ulwErr, bmpSize) = f.LayerState;
            sb.AppendLine($"  格子 {id,-4} 玻璃={_settings.GlassOpacity}%  Opacity={f.Opacity:0.00}  " +
                          $"LAYERED={(layered ? "有" : "无")}  逐像素alpha={(f.UsesLayeredWindow ? "开" : "关")}  " +
                          $"ULW={(ulwOk ? "成功" : "失败 err=" + ulwErr)}  位图={bmpSize.Width}×{bmpSize.Height}  " +
                          $"父窗口=0x{parent.ToInt64():X}({(parent == _host.Progman ? "Progman" : parent == IntPtr.Zero ? "无" : "其它")})  " +
                          $"屏幕矩形=({r.Left},{r.Top})-({r.Right},{r.Bottom})  Visible={f.Visible}");
        }
        return sb.ToString();
    }

    /// <summary>诊断用：返回各格子在屏幕上的真实矩形（GetWindowRect，嵌入后依然正确）</summary>
    public IReadOnlyList<(int Id, Rectangle Rect)> BoxRects()
    {
        var list = new List<(int, Rectangle)>();
        foreach (var (id, f) in _forms.OrderBy(kv => kv.Key))
        {
            if (!f.IsHandleCreated) continue;
            if (!Win32.Native.GetWindowRect(f.Handle, out var r)) continue;
            list.Add((id, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)));
        }
        return list;
    }

    /// <summary>
    /// 诊断用：在指定格子内选中第一项并进入就地改名。
    /// 用来验证「分层窗口里子控件（TextBox）还能不能正常显示」——
    /// 这是 UpdateLayeredWindow 最容易踩的坑。
    /// 返回（是否进入编辑态，发生改名的格子 ID）。
    /// </summary>
    public (bool Started, int BoxId) ProbeRename(int boxId = -1)
    {
        var target = boxId >= 0 ? boxId : (_forms.Count > 0 ? _forms.Keys.Min() : -1);
        if (target < 0) return (false, -1);

        if (!_forms.TryGetValue(target, out var f)) return (false, target);
        if (f.ItemCount <= 0) return (false, target);

        f.ProbeRename(0);
        return (f.IsRenaming, target);
    }

    // ───────── 退出 ─────────

    /// <summary>关闭并还原桌面（退出前必须调用，避免原生图标一直隐藏）</summary>
    public void Shutdown() => ExitApp();

    /// <summary>
    /// 构造托盘图标。优先读嵌入资源 <c>DeskLite.Resources.app-tray.ico</c>
    /// （16/20/24/32 四档，<see cref="Icon"/> 构造器按 <see cref="SystemInformation.SmallIconSize"/>
    /// 挑最接近的一档，高 DPI 托盘不糊、不碰 shell）；失败退回从 exe 提取；再失败用系统默认。
    /// </summary>
    private static Icon CreateTrayIcon()
    {
        try
        {
            using var rs = typeof(Program).Assembly
                .GetManifestResourceStream("DeskLite.Resources.app-tray.ico");
            if (rs != null)
            {
                var sz = SystemInformation.SmallIconSize;
                return new Icon(rs, sz.Width, sz.Height);
            }
        }
        catch { }
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                return Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application;
        }
        catch { }
        return SystemIcons.Application;
    }

    private void ExitApp()
    {
        try { _idleTimer?.Stop(); _idleTimer?.Dispose(); } catch { }
        try { _keepAlive?.Stop(); _keepAlive?.Dispose(); } catch { }
        try { StopWatchers(); } catch { }
        try { _bus.Dispose(); } catch { }

        // 还原桌面原生图标，并**无论 ShowNativeIcons 是否真的执行**都清掉崩溃标记 ——
        // 否则下次启动会被误判成「上次崩溃」，白跑一次还原。
        try { _host.ShowNativeIcons(); } catch { }
        try { CrashGuard.MarkDesktopShown(); } catch { }

        // 退出前把「还挂在去抖窗口里」的那次变更强制写掉 —— 用户刚拖完格子
        // 立刻选「退出」，位置必须留住（这正是去抖唯一可能丢数据的窗口）。
        SaveNow();
        try { _saveDebounce?.Dispose(); } catch { }
        _saveDebounce = null;

        foreach (var f in _forms.Values)
        {
            try { f.Close(); f.Dispose(); } catch { }
        }
        _forms.Clear();

        // 参考线覆盖层是置顶的全屏窗口，退出时必须显式收掉
        try { _snapOverlay?.Clear(); _snapOverlay?.Dispose(); } catch { }
        _snapOverlay = null;

        _search?.Close();
        _settingsForm?.Close();
        _icons.Dispose();

        _tray.Visible = false;
        _tray.Dispose();
        if (!ReferenceEquals(_trayIcon, SystemIcons.Application)) _trayIcon.Dispose();

        ExitThread();
    }
}

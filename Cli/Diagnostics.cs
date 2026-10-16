using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using DeskLite.Core;
using DeskLite.Win32;

namespace DeskLite.Cli;

/// <summary>
/// 全部命令行自检 / 诊断工具（<c>--probe</c>、<c>--mem</c>、<c>--iconprobe</c>、…）。
///
/// 这些工具原本和程序入口一起挤在 Program.cs 里，占了两千多行 ——
/// 而它们**和主流程没有任何关系**，只是「出问题时用来定位」的一次性入口。
/// 拆到这里之后，Program.cs 只剩「怎么启动」，读代码的人一眼就能看完全貌。
///
/// 约定：
///   · 每个 <c>RunXxx</c> 对应一个命令行开关，互相独立、随用随跑；
///   · 一律只在被显式调用时执行，正常运行路径不会碰到这里的任何代码；
///   · 输出同时写控制台与文件（便于归档比对）。
/// </summary>
internal static class Diagnostics
{
    // ───────────────────────── 右键菜单命令 ─────────────────────────

    /// <summary>
    /// 处理 <c>--cmd</c>：右键菜单注册的是「启动一个新进程」，但主程序单实例常驻，
    /// 因此新进程把意图广播给已运行实例；若没有实例则自行启动并执行。
    /// </summary>
    private static void RunShellCommand(string[] args)
    {
        string cmd = args.Length > 1 ? args[1].ToLowerInvariant() : "search";
        string? arg = args.Length > 2 ? args[2] : null;

        using var mutex = new Mutex(true, Program.MutexName, out bool createdNew);

        if (!createdNew)
        {
            var sc = cmd switch
            {
                "sort" => ShellCommand.Sort,
                "sendto" => ShellCommand.SendTo,
                _ => ShellCommand.Search,
            };
            CmdBus.Broadcast(sc, arg);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new UI.MainContext(autoTakeover: true, initialCmd: cmd, initialArg: arg));
    }

    // ───────────────────────── 设置模块自检 ─────────────────────────

    private static void RunSettingsTest()
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;

        var path = AppSettings.DefaultPath;
        Console.WriteLine("══════ 设置模块自检 ══════");
        Console.WriteLine($"存储位置: {path}");
        Console.WriteLine($"文件存在: {File.Exists(path)}");
        Console.WriteLine();

        var s = AppSettings.Load();
        Console.WriteLine("── 当前设置（CFG_* 键）──");
        Console.WriteLine(s.Serialize());

        // 序列化 → 解析 往返一致性
        var text = s.Serialize();
        var tmp = Path.Combine(Path.GetTempPath(), "DeskLite_settings_roundtrip.dat");
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        var s2 = AppSettings.Load(tmp);
        try { File.Delete(tmp); } catch { }
        Console.WriteLine($"── 序列化往返 ── {(s2.Serialize() == text ? "✓ 一致" : "✗ 不一致")}");

        // 热键解析
        Console.WriteLine();
        Console.WriteLine("── 热键解析 ──");
        foreach (var spec in new[] { "Ctrl+Alt+S", "Ctrl+Shift+Space", "Win+D", "Alt+F1", "Ctrl+Alt", "随便写" })
        {
            bool ok = CmdBus.TryParse(spec, out uint m, out uint v);
            Console.WriteLine($"  {spec,-18} → {(ok ? $"识别 (mod=0x{m:X}, vk=0x{v:X2})" : "无法识别")}");
        }

        Console.WriteLine();
        Console.WriteLine($"── 开机自启 ── {(Startup.IsEnabled ? "已登记" : "未登记")}");
    }

    // ───────────────────────── 设置界面截图（验证用）─────────────────────────

    private static void RunSettingsShot(string outPath, int page)
    {
        Program.Silent = true;
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;

        ApplicationConfiguration.Initialize();

        var s = AppSettings.Load();
        var f = new UI.SettingsForm(s, _ => { }, page)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(60, 60),
        };
        f.Show();
        f.Activate();

        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            RunShot(outPath);
            Console.WriteLine($"已截图: {outPath}（第 {page} 页）");
            f.Close();
            Application.ExitThread();
        };
        t.Start();

        Application.Run();
    }

    // ─────────────────── 圆角 Region 成本量化（--gdiresize）───────────────────

    /// <summary>
    /// 圆角 Region 的成本量化探针。
    ///
    /// 背景：三个自绘窗口（格子 / 设置 / 搜索）的圆角都靠 <c>Control.Region</c> 实现。
    /// 旧写法 <c>Region = new Region(path)</c> 挂在 <c>OnResize</c> 上，每改一次尺寸就
    /// 造一个新的 Region，而 <c>Control.Region</c> 的 setter 不接管所有权、旧的不会被释放。
    ///
    /// <b>注意这不是「GDI 句柄泄漏」</b>：<c>System.Drawing.Region</c> 底层是 GDI+ 的
    /// <c>GpRegion</c>，不计入 GDI 句柄表（<c>GetGuiResources</c> 看不见），
    /// 而且它有终结器兜底 —— 真实表现是「非托管内存被推高、且 GDI+ 私有堆水位不回落」。
    /// 所以本探针同时量 GDI 句柄（应恒定）、私有内存与托管堆。
    ///
    /// 还必须拿一个 <b>OnResize 什么都不做的裸窗口</b>做对照：单单改窗口尺寸本身
    /// （SetWindowPos / 重排 / 重绘）也会推高私有内存，不做对照就会把这份开销
    /// 误记到 Region 头上 —— 这是本探针第一版踩过的坑。
    ///
    /// 用法：<c>DeskLite.exe --gdiresize [轮数=20000] [region|bare]</c>
    ///   · region（默认）= 带圆角 Region 的设置窗
    ///   · bare          = 裸窗口对照组
    /// 两组要<b>分进程</b>跑：同进程里前一组推高的堆水位会污染后一组的基线。
    /// </summary>
    private static void RunGdiResize(int rounds, string mode)
    {
        Program.Silent = true;
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;

        ApplicationConfiguration.Initialize();

        using var self = System.Diagnostics.Process.GetCurrentProcess();
        int Gdi() => (int)Native.GetGuiResources(self.Handle, Native.GR_GDIOBJECTS);

        Form w;
        if (mode == "bare")
        {
            w = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),   // 屏幕外：测试时不要真闪在用户眼前
                Size = new Size(560, 580),
            };
        }
        else
        {
            w = new UI.SettingsForm(AppSettings.Load(), _ => { }, 0)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
            };
        }

        w.Show();
        Application.DoEvents();

        // 预热：把「建窗这批一次性 GDI 对象」算进基线，否则会被误记成泄漏
        for (int i = 0; i < 8; i++)
        {
            w.ClientSize = new Size(560 + i * 3, 580 + i * 2);
            Application.DoEvents();
        }

        int gdi0 = Gdi();
        long man0 = GC.GetTotalMemory(false);
        var (_, pv0) = MemInfo.Counters();

        for (int i = 0; i < rounds; i++)
        {
            // 尺寸在 560~616 × 580~622 之间来回变 —— 必须真的不同才会触发 WM_SIZE → OnResize
            w.ClientSize = new Size(560 + (i % 9) * 7, 580 + (i % 7) * 6);
            Application.DoEvents();
        }

        long man1 = GC.GetTotalMemory(false);
        var (_, pv1) = MemInfo.Counters();
        int gdi1 = Gdi();

        // 收干净再读：Region 的非托管侧靠终结器释放，必须「收 → 等终结 → 再收」
        CollectAll();
        int gdi2 = Gdi();
        long man2 = GC.GetTotalMemory(false);
        var (_, pv2) = MemInfo.Counters();

        Console.WriteLine($"窗口改尺寸 × {rounds} 次  模式={mode}");
        Console.WriteLine("                       GDI对象   私有(MB)   托管堆(KB)");
        Console.WriteLine($"  基线                {gdi0,8}  {pv0 / 1048576.0,9:F1}  {man0 / 1024.0,10:F0}");
        Console.WriteLine($"  跑完（未收）        {gdi1,8}  {pv1 / 1048576.0,9:F1}  {man1 / 1024.0,10:F0}");
        Console.WriteLine($"  收干净后            {gdi2,8}  {pv2 / 1048576.0,9:F1}  {man2 / 1024.0,10:F0}");
        Console.WriteLine($"  Δ（收干净 − 基线）  {(gdi2 - gdi0):+#;-#;0}       " +
                          $"{(pv2 - pv0) / 1024.0:+#;-#;0} KB    {(man2 - man0) / 1024.0:+#;-#;0} KB");
        Console.WriteLine($"RESULT|mode={mode}|dgdi={gdi2 - gdi0}|dpriv_kb={(pv2 - pv0) / 1024}" +
                          $"|dman_kb={(man2 - man0) / 1024}");

        w.Close();
    }

    /// <summary>把托管堆连同终结器队列一起清干净（Region 这类对象靠终结器释放非托管资源）</summary>
    private static void CollectAll()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Application.DoEvents();
    }

    // ───────────────────────── 右键集成自检 ─────────────────────────

    /// <summary>
    /// 右键集成「安装 → 验证 → 回滚」闭环自检。
    /// 使用内存中的探测设置（不读也不写用户的 ConFile.dat）；结束后删掉本次写入的全部键，
    /// 并恢复原有菜单风格，保证零残留。
    /// </summary>
    private static void RunShellVerify()
    {
        string exe = ShellIntegration.ExePath;
        if (string.IsNullOrEmpty(exe))
        {
            Console.WriteLine("无法定位程序自身路径，跳过。");
            return;
        }

        bool wasClassic = ShellIntegration.IsClassicMenu;

        Console.WriteLine("══════ 右键集成 安装 → 验证 → 回滚 ══════");
        Console.WriteLine();
        Console.WriteLine("【1】安装前");
        Console.WriteLine(ShellIntegration.Describe());

        var probe = new AppSettings
        {
            MenuDesktop = true,
            MenuFile = true,
            Win11Menu = false,
            CleanMenu = false,
        };
        ShellIntegration.Install(probe, exe);

        Console.WriteLine();
        Console.WriteLine("【2】安装后");
        Console.WriteLine(ShellIntegration.Describe());

        ShellIntegration.Uninstall();
        if (wasClassic) ShellIntegration.ApplyMenuStyle(classic: true);

        Console.WriteLine();
        Console.WriteLine("【3】回滚后");
        Console.WriteLine(ShellIntegration.Describe());
    }

    private static void RunShellTest(string mode)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;

        if (mode == "--shellverify")
        {
            RunShellVerify();
            return;
        }

        if (mode == "--shellinstall")
        {
            var s = AppSettings.Load();
            ShellIntegration.Install(s);
            Console.WriteLine("已按 ConFile.dat 中的设置写入注册表。");
            Console.WriteLine();
        }
        else if (mode == "--shelluninstall")
        {
            ShellIntegration.Uninstall();
            Console.WriteLine("已移除本程序写入的全部右键菜单注册表项。");
            Console.WriteLine();
        }

        Console.WriteLine("══════ 右键集成状态 ══════");
        Console.WriteLine(ShellIntegration.Describe());
    }

    // ───────────────────────── 演示 + 截图 ─────────────────────────

    // ───────────────────────── 样式预览（离屏渲染）─────────────────────────

    /// <summary>
    /// 把各格子离屏渲染并合成到**真实桌面壁纸**上，输出一张干净的样式预览图。
    ///
    /// 为什么要离屏渲染：演示截图会被前台窗口（浏览器等）遮挡，无法核对格子本身的
    /// 圆角 / 描边 / 图标网格 / 标题居中。这里直接渲染控件位图并叠加真实壁纸，
    /// 并用 ColorMatrix 复现 <c>Form.Opacity</c> 的毛玻璃效果，所以和实际显示一致。
    /// </summary>
    private static void RunUiShot(string outPath, int iconSize, int glass, bool light)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        Program.Silent = true;

        var settings = new AppSettings
        {
            IconSize = Math.Clamp(iconSize, 16, 128),
            GlassOpacity = Math.Clamp(glass, 30, 100),
            Theme = light ? "Light" : "Dark",
        };

        using var icons = new UI.IconCache(settings.IconSize);

        var items = DesktopScanner.Scan();
        // 把 此电脑 / 回收站 / 网络 也纳入预览 —— 它们由 Shell 命名空间提供，目录枚举看不到
        var dh = new DesktopHost();
        if (dh.Locate()) DesktopScanner.AppendVirtualItems(items, dh);

        var grouped = DesktopScanner.GroupByBox(items);
        var defs = DeskData.CreateDefault();

        // 摆法：左列 APP（高）+ 系统，右列 我的文件夹 / 我的文件
        var order = new[] { BoxIds.Apps, BoxIds.System, BoxIds.MyFolders, BoxIds.MyFiles };
        var rendered = new List<(Bitmap Bitmap, Size Size)>();

        using var host = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(10, 10),
        };
        host.CreateControl();

        foreach (var id in order)
        {
            var box = defs.FindBox(id);
            if (box == null) continue;

            var view = new UI.BoxView(id, icons, settings)
            {
                IdealColumns = id == BoxIds.Apps ? 5 : 3,
                Title = box.Name,
            };
            grouped.TryGetValue(id, out var list);
            view.SetItems(list ?? new List<DiscoveredItem>());

            var size = view.MeasureContent();
            view.Size = size;
            host.Controls.Add(view);

            var bmp = new Bitmap(size.Width, size.Height);
            view.DrawToBitmap(bmp, new Rectangle(0, 0, size.Width, size.Height));
            rendered.Add((bmp, size));

            Console.WriteLine($"  {box.Name,-10} {list?.Count ?? 0,3} 项  {size.Width}×{size.Height}");
        }

        host.Controls.Clear();

        // ── 排版：两列（左列 APP + 系统，右列 我的文件夹 + 我的文件）──
        const int pad = 40, gap = 24, vgap = 20;

        var col0 = rendered.Take(2).ToList();
        var col1 = rendered.Skip(2).Take(2).ToList();

        int col0W = col0.Count > 0 ? col0.Max(r => r.Size.Width) : 0;
        int col1W = col1.Count > 0 ? col1.Max(r => r.Size.Width) : 0;
        int col0H = col0.Sum(r => r.Size.Height) + Math.Max(0, col0.Count - 1) * vgap;
        int col1H = col1.Sum(r => r.Size.Height) + Math.Max(0, col1.Count - 1) * vgap;

        int canvasW = pad * 2 + col0W + (col1W > 0 ? gap + col1W : 0);
        int canvasH = pad * 2 + Math.Max(col0H, col1H);

        var layout = new List<(Bitmap Bitmap, Rectangle Rect)>();

        int y0 = pad;
        foreach (var (bmp, sz) in col0)
        {
            layout.Add((bmp, new Rectangle(pad, y0, sz.Width, sz.Height)));
            y0 += sz.Height + vgap;
        }

        int x1 = pad + col0W + gap;
        int y1 = pad;
        foreach (var (bmp, sz) in col1)
        {
            layout.Add((bmp, new Rectangle(x1, y1, sz.Width, sz.Height)));
            y1 += sz.Height + vgap;
        }

        // ── 合成 ──
        using var canvas = new Bitmap(canvasW, canvasH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        PaintBackdrop(g, canvasW, canvasH, settings.IsLight);

        // Form.Opacity 是「整窗 alpha」，这里用 ColorMatrix 精确复现
        using var attrs = new ImageAttributes();
        var cm = new ColorMatrix { Matrix33 = (float)settings.Opacity };
        attrs.SetColorMatrix(cm);

        foreach (var (bmp, rect) in layout)
        {
            using var clip = UI.Shapes.RoundedPath(rect, 8);
            var old = g.Clip;
            g.SetClip(clip, CombineMode.Replace);
            g.DrawImage(bmp, rect, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
            g.Clip = old;
        }

        canvas.Save(outPath, ImageFormat.Png);
        foreach (var (bmp, _) in layout) bmp.Dispose();

        Console.WriteLine($"已输出样式预览: {outPath}  ({canvasW}×{canvasH}, 图标 {settings.IconSize}px, 毛玻璃 {settings.GlassOpacity}%, {(light ? "浅色" : "深色")})");
        Console.WriteLine("说明：背景取自当前桌面壁纸；格子为离屏渲染，非屏幕截图。");
    }

    /// <summary>预览背景：优先用真实桌面壁纸（等比裁切填满），失败则退回深紫渐变</summary>
    private static void PaintBackdrop(Graphics g, int w, int h, bool light)
    {
        var wp = Native.DesktopWallpaper();
        if (wp != null)
        {
            try
            {
                using var img = Image.FromFile(wp);
                float scale = Math.Max(w / (float)img.Width, h / (float)img.Height);
                int dw = (int)Math.Ceiling(img.Width * scale);
                int dh = (int)Math.Ceiling(img.Height * scale);
                g.DrawImage(img, new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh));
                return;
            }
            catch { /* 壁纸读不出来就退回渐变 */ }
        }

        using var brush = new LinearGradientBrush(
            new Rectangle(0, 0, w, h),
            light ? Color.FromArgb(238, 240, 248) : Color.FromArgb(26, 16, 44),
            light ? Color.FromArgb(214, 220, 236) : Color.FromArgb(74, 22, 68),
            LinearGradientMode.ForwardDiagonal);
        g.FillRectangle(brush, new Rectangle(0, 0, w, h));
    }

    // ───────────────────────── 演示 ─────────────────────────

    private static void RunDemo(string shotPath, int seconds)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.WriteLine($"[demo] 启动，{seconds} 秒后截图 -> {shotPath}");

        static void Shoot(string path)
        {
            try
            {
                var b = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                using var bmp = new Bitmap(b.Width, b.Height);
                using var g = Graphics.FromImage(bmp);
                g.CopyFromScreen(b.X, b.Y, 0, 0, bmp.Size);
                bmp.Save(path, ImageFormat.Png);
                Console.WriteLine("[demo] 已截图: " + path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[demo] 截图失败: " + ex.Message);
                try { File.WriteAllText(path + ".err.txt", ex.ToString()); } catch { }
            }
        }

        ApplicationConfiguration.Initialize();
        Program.Silent = true;   // 演示模式不弹交互对话框

        // 若检测到 DeskGo 覆盖层，演示期间临时隐藏它 —— 否则截到的是 DeskGo 的画面
        IntPtr ovlHwnd = IntPtr.Zero;
        if (Conflict.OverlayPresent(out var oh, out _))
        {
            ovlHwnd = oh;
            Native.ShowWindow(oh, Native.SW_HIDE);
            Thread.Sleep(900);
            Console.WriteLine($"[demo] 已临时隐藏 DeskGo 覆盖层 0x{oh:X}");
        }

        // 接管前基线截图
        var probe = new DesktopHost();
        probe.Locate();
        Console.WriteLine($"[demo] 接管前 ListView 可见 = {probe.NativeIconsVisible}");
        Shoot(shotPath.Replace(".png", "-before.png"));

        // 接管
        var ctx = new UI.MainContext(autoTakeover: true, readOnlyData: true);
        Console.WriteLine($"[demo] 接管后 ListView 可见 = {probe.NativeIconsVisible}");

        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(2, seconds) * 1000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Console.WriteLine($"[demo] 截图时刻 ListView 可见 = {probe.NativeIconsVisible}");
            Shoot(shotPath);

            ctx.Shutdown();
            Application.ExitThread();
        };
        timer.Start();

        Application.Run(ctx);

        // 还原覆盖层
        if (ovlHwnd != IntPtr.Zero)
        {
            Native.ShowWindow(ovlHwnd, Native.SW_SHOW);
            Console.WriteLine("[demo] 已还原 DeskGo 覆盖层");
        }

        Console.WriteLine($"[demo] 退出后 ListView 可见 = {probe.NativeIconsVisible}");
    }

    // ───────────────────────── 截屏 ─────────────────────────

    private static void RunShot(string path)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        try
        {
            var b = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            using var bmp = new Bitmap(b.Width, b.Height);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(b.X, b.Y, 0, 0, bmp.Size);
            bmp.Save(path, ImageFormat.Png);
            Console.WriteLine("已截图: " + path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("截图失败: " + ex.Message);
        }
    }

    // ───────────────────────── 桌面 ListView 复位 ─────────────────────────

    /// <summary>
    /// 把桌面 SysListView32 的窗口位置复位到 Progman 的矩形。
    /// 用于清除诊断测试（移出屏幕）留下的位置偏移。
    /// </summary>
    private static void RunFixDesktop()
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);

        var host = new DesktopHost();
        if (!host.Locate()) { Console.WriteLine("未定位到桌面窗口链"); return; }

        Native.GetWindowRect(host.Progman, out var pr);
        Native.GetWindowRect(host.ListView, out var before);

        Console.WriteLine($"Progman  rect = ({pr.Left},{pr.Top})-({pr.Right},{pr.Bottom})");
        Console.WriteLine($"ListView before = ({before.Left},{before.Top})-({before.Right},{before.Bottom})");

        // 复位到父窗口客户区原点 (0,0) 并恢复可见，保持原有尺寸
        Native.SetWindowPos(host.ListView, IntPtr.Zero,
            0, 0, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Native.ShowWindow(host.ListView, Native.SW_SHOW);

        Thread.Sleep(500);
        Native.GetWindowRect(host.ListView, out var after);
        Console.WriteLine($"ListView after  = ({after.Left},{after.Top})-({after.Right},{after.Bottom})");
        Console.WriteLine($"可见 = {Native.IsWindowVisible(host.ListView)}");
        Console.WriteLine("已完成复位。");
    }

    // ───────────────────────── 窗口树枚举 ─────────────────────────

    private static string Cls(IntPtr h)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// 枚举完整窗口树，找出所有 Progman / WorkerW / SHELLDLL_DefView / SysListView32 / DirectUIHWND。
    /// 目的：确认 Win11 上桌面图标到底由哪个窗口承载（可能存在多个候选）。
    /// </summary>
    private static void RunListWnd(string? outPath)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);

        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        int defCount = 0, lvCount = 0, duiCount = 0;

        void Walk(IntPtr h, int depth)
        {
            var cn = Cls(h);
            bool interesting = cn is "Progman" or "WorkerW" or "SHELLDLL_DefView"
                                  or "SysListView32" or "DirectUIHWND" or "SHELLDLL_DefViewParent";
            if (interesting)
            {
                Native.GetWindowRect(h, out var r);
                var pad = new string(' ', depth * 2);
                W($"{pad}{cn,-20} hwnd=0x{h:X6} vis={Native.IsWindowVisible(h),-5} " +
                  $"rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) size={r.Right - r.Left}x{r.Bottom - r.Top}");

                if (cn == "SHELLDLL_DefView") defCount++;
                if (cn == "SysListView32") lvCount++;
                if (cn == "DirectUIHWND") duiCount++;
                depth++;
            }

            Native.EnumChildWindows(h, (c, _) => { Walk(c, depth); return true; }, IntPtr.Zero);
        }

        W("══════ 桌面相关窗口树 ══════");
        Native.EnumWindows((h, _) => { Walk(h, 0); return true; }, IntPtr.Zero);
        W("");
        W($"合计: SHELLDLL_DefView={defCount}  SysListView32={lvCount}  DirectUIHWND={duiCount}");
        W("");
        W("══════ 桌面相关进程的全部顶层窗口（含不可见）══════");
        var rows = new List<string>();
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            string pname;
            try { pname = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { pname = "?"; }

            if (pname is "explorer" or "DesktopMgr64" or "DesktopService64" or "WorkBuddy")
            {
                Native.GetWindowRect(h, out var r);
                var cn = Cls(h);
                var tb = new StringBuilder(300);
                Native.GetWindowText(h, tb, tb.Capacity);
                rows.Add($"  [{(Native.IsWindowVisible(h) ? "VIS" : "hid")}] pid={pid,-6} {pname,-18} " +
                         $"cls={cn,-26} ({r.Left},{r.Top})-({r.Right},{r.Bottom}) '{tb}'");
            }
            return true;
        }, IntPtr.Zero);
        foreach (var r in rows) W(r);
        W("");
        W("══════ 所有可见大窗口 ══════");

        var others = new List<string>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            Native.GetWindowRect(h, out var r);
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w < 300 || ht < 200) return true;   // 只关心大窗口

            Native.GetWindowThreadProcessId(h, out uint pid);
            var cn = Cls(h);
            var tb = new StringBuilder(300);
            Native.GetWindowText(h, tb, tb.Capacity);
            string pname;
            try { pname = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { pname = "?"; }

            others.Add($"  hwnd=0x{h:X6} pid={pid,-6} {pname,-22} cls={cn,-26} " +
                       $"({r.Left},{r.Top})-({r.Right},{r.Bottom}) '{tb}'");
            return true;
        }, IntPtr.Zero);

        foreach (var o in others) W(o);
        W("");

        var target = outPath ?? Path.Combine(AppContext.BaseDirectory, "listwnd-result.txt");
        try { File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false)); } catch { }
    }

    // ───────────────────────── 像素归属测试 ─────────────────────────

    /// <summary>
    /// 对桌面上每个图标所在的屏幕坐标调用 WindowFromPoint，
    /// 判定这些像素到底由哪个窗口（哪个进程）绘制。
    /// 若返回 SysListView32/explorer → 图标是原生渲染，隐藏应当生效；
    /// 若返回其它窗口/DLL 宿主 → 说明有第三方（如 DeskGo）在接管渲染。
    /// </summary>
    private static void RunPointTest(string? outPath)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);

        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        var host = new DesktopHost();
        if (!host.Locate())
        {
            W("❌ 未定位到桌面窗口链");
            return;
        }

        Native.GetWindowRect(host.ListView, out var lvRect);
        W($"桌面链: Progman=0x{host.Progman:X}  DefView=0x{host.DefView:X}  ListView=0x{host.ListView:X}");
        W($"ListView 屏幕矩形: ({lvRect.Left},{lvRect.Top})-({lvRect.Right},{lvRect.Bottom})");
        W("");

        var icons = host.EnumerateIcons(80);
        W($"枚举到桌面图标 {icons.Count} 个，逐一探测像素归属：");
        W("");

        // 统计归属分布
        var tally = new Dictionary<string, int>();
        var samples = new List<string>();
        var hits = new HashSet<IntPtr>();

        foreach (var ic in icons)
        {
            // LVM_GETITEMPOSITION 返回客户区坐标，客户区原点 = ListView 左上角
            int sx = lvRect.Left + ic.X + 24;   // 取图标中心附近
            int sy = lvRect.Top + ic.Y + 24;

            var pt = new Native.POINT(sx, sy);
            IntPtr w = Native.WindowFromPoint(pt);
            if (w == IntPtr.Zero) continue;

            Native.GetWindowThreadProcessId(w, out uint pid);
            string cls = Cls(w);

            // 逐级向上追到顶层窗口，判断是否有中间覆盖层
            IntPtr root = w;
            while (true)
            {
                IntPtr p = Native.GetParent(root);
                if (p == IntPtr.Zero) break;
                root = p;
            }
            Native.GetWindowThreadProcessId(root, out uint rootPid);
            string rootCls = Cls(root);

            string pname;
            try { pname = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { pname = "?"; }
            string rootName;
            try { rootName = System.Diagnostics.Process.GetProcessById((int)rootPid).ProcessName; }
            catch { rootName = "?"; }

            string key = $"{pname}/{cls}";
            tally[key] = tally.GetValueOrDefault(key) + 1;
            hits.Add(w);

            if (samples.Count < 25)
            {
                samples.Add($"  「{Trunc(ic.Text, 18),-18}」 屏幕({sx,5},{sy,5})  " +
                            $"命中 pid={pid,-6} {pname,-16} cls={cls,-22} " +
                            $"| 顶层 pid={rootPid,-6} {rootName,-14} cls={rootCls}");
            }
        }

        foreach (var s in samples) W(s);
        W("");
        W("══════ 归属分布统计 ══════");
        foreach (var kv in tally.OrderByDescending(k => k.Value))
            W($"  {kv.Key,-46} × {kv.Value}");
        W("");

        // ── 命中窗口的详细参数 ──
        W("══════ 命中窗口详细参数 ══════");
        foreach (var hw in hits)
        {
            Native.GetWindowRect(hw, out var r);
            long style = Native.GetWindowLongPtr(hw, Native.GWL_STYLE).ToInt64();
            long ex = Native.GetWindowLongPtr(hw, Native.GWL_EXSTYLE).ToInt64();
            IntPtr parent = Native.GetParent(hw);
            IntPtr owner = Native.GetWindow(hw, 4 /*GW_OWNER*/);
            Native.GetWindowThreadProcessId(hw, out uint pid);
            string pname;
            try { pname = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { pname = "?"; }
            W($"  hwnd=0x{hw:X6}  cls={Cls(hw)}  pid={pid} ({pname})");
            W($"      rect=({r.Left},{r.Top})-({r.Right},{r.Bottom})  size={r.Right - r.Left}x{r.Bottom - r.Top}");
            W($"      style=0x{style:X8}  exStyle=0x{ex:X8}");
            W($"      WS_CHILD={(style & 0x40000000) != 0}  WS_POPUP={(style & 0x80000000L) != 0}  WS_VISIBLE={(style & 0x10000000) != 0}");
            W($"      WS_EX_TOOLWINDOW={(ex & 0x80) != 0}  WS_EX_LAYERED={(ex & 0x80000) != 0}  WS_EX_TRANSPARENT={(ex & 0x20) != 0}  WS_EX_NOACTIVATE={(ex & 0x8000000) != 0}");
            W($"      parent=0x{parent:X}  owner=0x{owner:X}");
            W($"      visible={Native.IsWindowVisible(hw)}");
        }
        W("");
        W($"桌面 ListView 当前可见: {Native.IsWindowVisible(host.ListView)}");

        var target = outPath ?? Path.Combine(AppContext.BaseDirectory, "pointtest-result.txt");
        try { File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false)); } catch { }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    // ───────────────────────── 覆盖层验证 ─────────────────────────

    /// <summary>
    /// 验证「DeskGo 的 TXMiniSkin 覆盖层是否就是屏幕上图标的真正绘制者」。
    /// 步骤（全程可逆，逐帧截图）：
    ///   ① 截图 A：当前状态
    ///   ② 隐藏 TXMiniSkin（DeskGo 覆盖层）→ 截图 B
    ///   ③ 再隐藏原生 SysListView32 → 截图 C
    ///   ④ 恢复两者 → 截图 D
    /// 对比 A/B 可知覆盖层影响；对比 B/C 可知原生 ListView 是否仍在承载图标。
    /// </summary>
    private static void RunOverlayTest(string prefix)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);

        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        var host = new DesktopHost();
        if (!host.Locate()) { W("❌ 未定位到桌面"); return; }

        // 用「图标坐标 → WindowFromPoint」反查覆盖层窗口（比 EnumWindows 可靠）
        var overlayHits = new HashSet<IntPtr>();
        Native.GetWindowRect(host.ListView, out var lvRect);
        foreach (var ic in host.EnumerateIcons(20))
        {
            var pt = new Native.POINT(lvRect.Left + ic.X + 24, lvRect.Top + ic.Y + 24);
            IntPtr w = Native.WindowFromPoint(pt);
            if (w != IntPtr.Zero && w != host.ListView && Cls(w) != "SysListView32")
                overlayHits.Add(w);
        }

        W($"覆盖层候选窗口 {overlayHits.Count} 个：");
        foreach (var h in overlayHits) W($"  0x{h:X6} cls={Cls(h)}");
        W("");

        var overlays = overlayHits.ToList();
        var hidden = new List<IntPtr>();

        try
        {
            // ① 截图 A
            RunShot($"{prefix}-A-当前.png");
            W("① 已截图 A（当前状态）");

            // ② 隐藏覆盖层
            foreach (var h in overlays)
            {
                Native.ShowWindow(h, Native.SW_HIDE);
                hidden.Add(h);
            }
            Thread.Sleep(1500);
            RunShot($"{prefix}-B-仅隐藏覆盖层.png");
            W($"② 隐藏覆盖层 {overlays.Count} 个 → 已截图 B");
            W($"    覆盖层可见性: {string.Join(", ", overlays.Select(h => Native.IsWindowVisible(h)))}");

            // ③ 再隐藏原生 ListView
            Native.ShowWindow(host.ListView, Native.SW_HIDE);
            Thread.Sleep(1500);
            RunShot($"{prefix}-C-隐藏覆盖层+ListView.png");
            W($"③ 再隐藏原生 ListView → 已截图 C (ListView可见={Native.IsWindowVisible(host.ListView)})");
        }
        finally
        {
            // ④ 恢复
            Native.ShowWindow(host.ListView, Native.SW_SHOW);
            foreach (var h in hidden) Native.ShowWindow(h, Native.SW_SHOW);
            Thread.Sleep(1200);
            RunShot($"{prefix}-D-恢复后.png");
            W("④ 已恢复现场 → 已截图 D");
            W($"    ListView可见={Native.IsWindowVisible(host.ListView)}");
        }

        var target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(prefix)) ?? ".", "overlaytest-result.txt");
        try { File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false)); } catch { }
    }

    // ───────────────────────── 冲突检测自检 ─────────────────────────

    private static void RunConflictCheck()
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("══════ DeskGo 冲突检测 ══════");

        var procs = Conflict.RunningDeskGo();
        Console.WriteLine($"进程检测 : {(procs.Count == 0 ? "未发现 DeskGo" : string.Join("、", procs.Select(p => $"{p.Name}({p.Pid})")))}");

        bool ovl = Conflict.OverlayPresent(out IntPtr h, out int pid);
        if (ovl)
            Console.WriteLine($"覆盖层   : 发现 TXMiniSkin  0x{h:X}  pid={pid}  ← 桌面正在被 DeskGo 接管");
        else
            Console.WriteLine("覆盖层   : 未发现");

        Console.WriteLine();
        Console.WriteLine(procs.Count > 0 || ovl
            ? "结论：与腾讯桌面整理冲突。请先退出 DeskGo 再用本程序接管。"
            : "结论：环境干净，接管功能可正常使用。");
    }

    // ───────────────────────── 图标提取自检 ─────────────────────────


    /// <summary>
    /// 把桌面各项的图标按三种方式提取并拼成对照表，用来区分
    /// 「图标提取有问题」还是「ListView 渲染有问题」。
    ///   A = 旧方法（Icon.ToBitmap，会丢 alpha）
    ///   B = SHGetFileInfo(SHGFI_ICON) + DrawIconEx
    ///   C = 系统 Imagelist（SHGFI_SYSICONINDEX + SHGetImageList）
    /// 实测结论：B 对 .lnk 快捷方式大量返回纯黑图标，C 才是可靠途径。
    /// </summary>
    private static void RunIconTest(string outPath)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;

        var items = DesktopScanner.Scan();
        Console.WriteLine($"桌面共 {items.Count} 项，取前 30 项做图标对照表");

        var sample = items.Take(30).ToList();
        const int half = 32, pad = 8, cols = 6;
        int cellW = half * 3 + 6;
        int cellH = half + 26;
        int rows = (sample.Count + cols - 1) / cols;

        using var sheet = new Bitmap(cols * (cellW + pad) + pad, rows * (cellH + pad) + pad);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(70, 70, 70));
            using var f = new Font("Microsoft YaHei UI", 7F);

            for (int i = 0; i < sample.Count; i++)
            {
                var it = sample[i];
                int cx = pad + (i % cols) * (cellW + pad);
                int cy = pad + (i / cols) * (cellH + pad);

                g.FillRectangle(new SolidBrush(Color.FromArgb(45, 45, 45)), cx, cy, cellW, half);

                bool ex = Directory.Exists(it.Path) || File.Exists(it.Path);
                DrawCell(g, cx, cy, () => OldExtract(it.Path, half));
                DrawCell(g, cx + half + 3, cy,
                    () => UI.IconCache.ViaShellIcon(it.Path, half, ex) ?? new Bitmap(half, half));
                DrawCell(g, cx + (half + 3) * 2, cy,
                    () => UI.IconCache.ViaImageList(it.Path, half, ex, Native.SHIL_EXTRALARGE)
                          ?? new Bitmap(half, half));

                string label = it.Name.Length > 11 ? it.Name[..11] : it.Name;
                g.DrawString(label, f, Brushes.White, cx, cy + half + 1);
                g.DrawString(it.Type.ToString(), f, Brushes.LightGray, cx, cy + half + 12);
            }
        }

        sheet.Save(outPath, ImageFormat.Png);
        Console.WriteLine($"已输出对照表: {outPath}");
        Console.WriteLine("每格三张：A=旧(ToBitmap)  B=DrawIconEx  C=系统Imagelist");
    }

    // ───────────────────────── 资源占用自检 ─────────────────────────

    /// <summary>
    /// 让程序像平时一样常驻（建格子、接管、挂热键、装监听），
    /// 稳定后统计：工作集 / 私有内存 / 提交内存 / 线程 / 句柄 / GDI+USER 对象 / 空闲 CPU 占用。
    /// 用于和「腾讯桌面整理（DesktopMgr64 + DesktopService64）」做同口径对比。
    /// </summary>
    private static void RunMemProbe(int seconds, string? outPath)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        Program.Silent = true;

        seconds = Math.Clamp(seconds, 5, 120);
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        ApplicationConfiguration.Initialize();

        var t0 = DateTime.Now;
        var ctx = new UI.MainContext(autoTakeover: true, readOnlyData: true);
        W($"已启动常驻（建格子 + 接管 + 热键 + 变更监听），{DateTime.Now - t0} 内完成初始化");
        W("");

        void Sample(string tag)
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            p.Refresh();

            uint gdi = 0, usr = 0;
            try
            {
                gdi = Native.GetGuiResources(p.Handle, Native.GR_GDIOBJECTS);
                usr = Native.GetGuiResources(p.Handle, Native.GR_USEROBJECTS);
            }
            catch { }

            W($"[{tag}]");
            W($"  工作集(Working Set)   : {p.WorkingSet64 / 1048576.0:N1} MB");
            W($"  私有内存(Private)     : {p.PrivateMemorySize64 / 1048576.0:N1} MB");
            W($"  提交内存(Commit)      : {p.PagedMemorySize64 / 1048576.0:N1} MB");
            W($"  峰值工作集(Peak WS)   : {p.PeakWorkingSet64 / 1048576.0:N1} MB");
            W($"  线程数                : {p.Threads.Count}");
            W($"  句柄数                : {p.HandleCount}");
            W($"  GDI 对象              : {gdi}");
            W($"  USER 对象             : {usr}");
            W($"  累计 CPU 时间         : {p.TotalProcessorTime.TotalSeconds:N2} s / 运行 {(DateTime.Now - t0).TotalSeconds:N1} s");
            W("");

            try { W(MemInfo.Format(MemInfo.Snapshot())); }
            catch (Exception ex) { W($"（内存构成分析失败：{ex.Message}）"); }
        }

        double cpuStart = 0;

        // ① 启动 3 秒（首屏已渲染完成）
        var t1 = new System.Windows.Forms.Timer { Interval = 3000 };
        t1.Tick += (_, _) =>
        {
            t1.Stop(); t1.Dispose();
            Sample("启动 3 秒后（未整理）");

            // ② 触发一次内存整理（GC + 交还工作集）
            UI.MainContext.SettleMemory();
            W("→ 已执行一次内存整理（强制 GC ×2 + TrimWorkingSet）");
            W("");

            var t2 = new System.Windows.Forms.Timer { Interval = 1500 };
            t2.Tick += (_, _) =>
            {
                t2.Stop(); t2.Dispose();
                Sample("整理后（1.5 秒稳态）");

                using (var p0 = System.Diagnostics.Process.GetCurrentProcess())
                {
                    p0.Refresh();
                    cpuStart = p0.TotalProcessorTime.TotalSeconds;
                }

                var t3 = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
                t3.Tick += (_, _) =>
                {
                    t3.Stop(); t3.Dispose();

                    double cpuEnd;
                    using (var p1 = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        p1.Refresh();
                        cpuEnd = p1.TotalProcessorTime.TotalSeconds;
                    }

                    Sample($"再空闲 {seconds} 秒后");

                    int cores = Environment.ProcessorCount;
                    double used = cpuEnd - cpuStart;
                    double pctTotal = used / seconds / cores * 100.0;
                    double pctOneCore = used / seconds * 100.0;

                    W($"── 空闲期 CPU（{seconds} 秒采样窗口）──");
                    W($"  消耗 CPU 时间         : {used:N3} s");
                    W($"  占全部核心            : {pctTotal:N2} %");
                    W($"  占单个核心            : {pctOneCore:N2} %");
                    W($"  逻辑核心数            : {cores}");

                    var t = outPath ?? Path.Combine(AppContext.BaseDirectory, "memprobe.txt");
                    try { File.WriteAllText(t, sb.ToString(), new UTF8Encoding(false)); } catch { }

                    ctx.Shutdown();
                    Application.ExitThread();
                };
                t3.Start();
            };
            t2.Start();
        };
        t1.Start();

        Application.Run(ctx);
    }

    // ───────────────────────── 子步骤内存差分 ─────────────────────────

    /// <summary>
    /// 把 <see cref="UI.MainContext"/> 构造过程逐节点采样，找出「到底哪一步吃掉了内存」。
    /// 每步输出：私有提交 / 工作集 / GC 已提交 / 线程 / 句柄 / GDI / 模块数，
    /// 以及本步新出现的私有内存块（≥512KB）和它们的零填充比例。
    /// </summary>
    private static void RunMemStep(string? outPath)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        Program.Silent = true;

        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        W("══════ MainContext 构造过程内存差分 ══════");
        W("QueryWorkingSetEx 自检: " + MemInfo.SelfTest());
        W($"ConserveMemory={AppContext.GetData("System.GC.ConserveMemory") ?? "(未设置)"}  " +
          $"Concurrent={AppContext.GetData("System.GC.Concurrent") ?? "(默认)"}  " +
          $"RetainVM={AppContext.GetData("System.GC.RetainVM") ?? "(默认)"}  " +
          $"IsServerGC={System.Runtime.GCSettings.IsServerGC}");

        // 把当前生效的诊断开关打出来，避免「以为改了、其实没生效」
        var sw = new List<string>();
        foreach (var n in new[] { "DESKLITE_ICONSTUB", "DESKLITE_ICONSTUB2", "DESKLITE_ICONREALN",
                                  "DESKLITE_NOGFX", "DESKLITE_NODRAWICON", "DESKLITE_NOLOCK",
                                  "DESKLITE_NONORMALIZE", "DESKLITE_NOSCALE", "DESKLITE_NOPOOL",
                                  "DESKLITE_NOICON", "DESKLITE_NOTEXT", "DESKLITE_NOFILL",
                                  "DESKLITE_NOOPACITY" })
        {
            var v = Environment.GetEnvironmentVariable(n);
            if (!string.IsNullOrEmpty(v)) sw.Add($"{n}={v}");
        }
        W("生效开关: " + (sw.Count == 0 ? "(无，走默认路径)" : string.Join("  ", sw)));
        W("");

        UI.IconCache.ResetCounters();

        // ── 预热：先把测量代码本身跑几遍（JIT + 首次模块枚举 + 缓冲分配），
        //    否则「第一步」的读数会把「度量工具的初始化成本」算到业务代码头上。
        for (int i = 0; i < 2; i++) { try { _ = MemInfo.Snapshot(); } catch { } }
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        catch { }

        W($"{"步骤",-34} {"私有提交",-10} {"Δ",-10} {"工作集",-10} {"GC已提交",-10} {"线程",-5} {"句柄",-6} {"GDI",-5} {"模块",-5}");
        W(new string('─', 110));

        static string M(long b) => $"{b / 1048576.0:N1} MB";
        static string K(long b) => b < 1048576 ? $"{b / 1024.0:N0} KB" : M(b);

        MemInfo.Report? prev = null;
        long prevPriv = 0;
        var known = new HashSet<long>();
        var snapshots = new List<(string Tag, MemInfo.Report R)>();

        ApplicationConfiguration.Initialize();

        var ctx = new UI.MainContext(autoTakeover: true, readOnlyData: true, onStep: tag =>
        {
            bool sub = tag.StartsWith('~');        // '~' 前缀 = 绘制/布局路径的细分埋点
            if (sub) tag = "  " + tag[1..];

            try
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            catch { }

            if (sub)
            {
                var q = MemInfo.Quick();
                if (prevPriv != 0 && Math.Abs(q.Priv - prevPriv) < 64 * 1024) return;   // 变化 <64KB 不打印
                W($"{tag,-34} {M(q.Priv),-10} {(prevPriv == 0 ? "—" : Signed(q.Priv - prevPriv)),-10} {M(q.Ws),-10} {K(q.GcCommit),-10} {q.Thr,-5} {q.Handles,-6} {q.Gdi,-5} {q.Mods,-5}");
                prevPriv = q.Priv;
                return;
            }

            var r = MemInfo.Snapshot();
            prevPriv = r.PrivateBytes;
            string delta = prev == null ? "—" : Signed(r.PrivateBytes - prev.PrivateBytes);
            W($"{tag,-34} {M(r.PrivateBytes),-10} {delta,-10} {M(r.WorkingSet),-10} {K(r.GcCommitted),-10} {r.Threads,-5} {r.Handles,-6} {r.Gdi,-5} {r.ModuleCount,-5}");

            if (prev != null)
            {
                foreach (var x in r.TopRegions.Where(x => x.Type == MemInfo.TYPE_PRIVATE))
                {
                    if (known.Contains(x.Base)) continue;
                    known.Add(x.Base);
                    W($"    + 新块 {x.Size / 1048576.0,7:N2} MB {MemInfo.ProtectName(x.Protect),-9} @0x{x.Base:X}");
                    W($"      {MemInfo.ProbeRegion(x.Base, x.Size)}");
                }
            }
            else
            {
                foreach (var x in r.TopRegions.Where(x => x.Type == MemInfo.TYPE_PRIVATE)) known.Add(x.Base);
            }

            snapshots.Add((tag, r));
            prev = r;
        });

        W("");
        W("图标管线统计: " + UI.IconCache.CounterSummary());
        W("构造完成。再跑 2.5 秒让首屏绘制，然后整理一次。");

        var t1 = new System.Windows.Forms.Timer { Interval = 2500 };
        t1.Tick += (_, _) =>
        {
            t1.Stop(); t1.Dispose();

            var r1 = MemInfo.Snapshot();
            W($"{"④ 首屏绘制完成",-34} {M(r1.PrivateBytes),-10} {Signed(r1.PrivateBytes - prev!.PrivateBytes),-10} {M(r1.WorkingSet),-10} {K(r1.GcCommitted),-10} {r1.Threads,-5} {r1.Handles,-6} {r1.Gdi,-5} {r1.ModuleCount,-5}");

            UI.MainContext.SettleMemory();

            var r2 = MemInfo.Snapshot();
            W($"{"⑤ 内存整理后",-34} {M(r2.PrivateBytes),-10} {Signed(r2.PrivateBytes - r1.PrivateBytes),-10} {M(r2.WorkingSet),-10} {K(r2.GcCommitted),-10} {r2.Threads,-5} {r2.Handles,-6} {r2.Gdi,-5} {r2.ModuleCount,-5}");
            W("");

            W("── 最终构成 ──");
            W(MemInfo.Format(r2));

            var t = outPath ?? Path.Combine(AppContext.BaseDirectory, "memstep.txt");
            try { File.WriteAllText(t, sb.ToString(), new UTF8Encoding(false)); } catch { }

            ctx.Shutdown();
            Application.ExitThread();
        };
        t1.Start();

        Application.Run(ctx);
    }

    // ───────────────────────── 分阶段内存差分 ─────────────────────────

    /// <summary>
    /// 把进程内存增长按「阶段」拆开：
    ///   ① 仅 runtime（还没碰 WinForms）→ ② WinForms 初始化 → ③ 建格子+接管+监听
    ///   → ④ 首屏绘制完成 → ⑤ 一次内存整理之后
    /// 每个阶段记录私有提交 / 工作集 / GC 堆 / 线程 / 句柄 / GDI / 模块数，
    /// 并对比相邻阶段「新增或变大了哪些私有内存块」——直接指出内存是谁分配的。
    /// </summary>
    private static void RunMemDiff(string? outPath)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        Program.Silent = true;

        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        W("══════ 分阶段内存差分 ══════");
        W("QueryWorkingSetEx 自检: " + MemInfo.SelfTest());
        W("");

        var steps = new List<(string Tag, MemInfo.Report R)>();

        void Step(string tag)
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            catch { }
            steps.Add((tag, MemInfo.Snapshot()));
        }

        Step("① 仅 runtime（未碰 WinForms）");

        ApplicationConfiguration.Initialize();
        Step("② WinForms 初始化后");

        var ctx = new UI.MainContext(autoTakeover: true, readOnlyData: true);
        Step("③ 建格子 + 接管 + 热键 + 变更监听后");

        void Flush()
        {
            var t = outPath ?? Path.Combine(AppContext.BaseDirectory, "memdiff.txt");
            try { File.WriteAllText(t, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }

        var t1 = new System.Windows.Forms.Timer { Interval = 2500 };
        t1.Tick += (_, _) =>
        {
            t1.Stop(); t1.Dispose();
            Step("④ 首屏绘制完成（2.5s）");

            UI.MainContext.SettleMemory();
            var t2 = new System.Windows.Forms.Timer { Interval = 1500 };
            t2.Tick += (_, _) =>
            {
                t2.Stop(); t2.Dispose();
                Step("⑤ 内存整理后（1.5s）");

                // ── 汇总表 ──
                W("── 各阶段指标 ──");
                W($"{"阶段",-30} {"私有提交",-10} {"工作集",-10} {"GC已提交",-10} {"GC存活",-10} {"线程",-6} {"句柄",-6} {"GDI",-6} {"USER",-6} {"模块",-6}");
                static string M(long b) => $"{b / 1048576.0:N1} MB";
                static string K(long b) => b < 1048576 ? $"{b / 1024.0:N0} KB" : M(b);
                foreach (var (tag, r) in steps)
                {
                    W($"{tag,-30} {M(r.PrivateBytes),-10} {M(r.WorkingSet),-10} {K(r.GcCommitted),-10} {K(r.GcHeapBytes),-10} {r.Threads,-6} {r.Handles,-6} {r.Gdi,-6} {r.User,-6} {r.ModuleCount,-6}");
                }
                W("");

                // ── 相邻阶段增量 ──
                for (int i = 1; i < steps.Count; i++)
                {
                    var a = steps[i - 1].R;
                    var b = steps[i].R;
                    W($"[{steps[i - 1].Tag}] → [{steps[i].Tag}]");
                    W($"  私有提交 {Signed(b.PrivateBytes - a.PrivateBytes)}   " +
                      $"工作集 {Signed(b.WorkingSet - a.WorkingSet)}   " +
                      $"GC已提交 {Signed(b.GcCommitted - a.GcCommitted)}   " +
                      $"线程 {b.Threads - a.Threads:+#;-#;0}   句柄 {b.Handles - a.Handles:+#;-#;0}   " +
                      $"GDI {b.Gdi - a.Gdi:+#;-#;0}   USER {b.User - a.User:+#;-#;0}   模块 {b.ModuleCount - a.ModuleCount:+#;-#;0}");

                    // 新增/变大的私有块
                    var prev = a.TopRegions.Where(x => x.Type == MemInfo.TYPE_PRIVATE)
                                          .ToDictionary(x => x.Base, x => x.Size);
                    var grew = new List<string>();
                    foreach (var cur in b.TopRegions.Where(x => x.Type == MemInfo.TYPE_PRIVATE))
                    {
                        if (!prev.TryGetValue(cur.Base, out long was))
                            grew.Add($"    + 新出现 {cur.Size / 1048576.0,7:N2} MB  PRIVATE {MemInfo.ProtectName(cur.Protect),-10} @0x{cur.Base:X}");
                        else if (cur.Size != was)
                            grew.Add($"    ↑ 变大   {was / 1048576.0,7:N2} → {cur.Size / 1048576.0:N2} MB  @0x{cur.Base:X}");
                    }
                    foreach (var g in grew) W(g);
                    W("");
                }

                // ── 最终构成 ──
                W("══════ 最终构成 ══════");
                W(MemInfo.Format(steps[^1].R));

                Flush();
                ctx.Shutdown();
                Application.ExitThread();
            };
            t2.Start();
        };
        t1.Start();

        Application.Run(ctx);
    }

    private static string Signed(long v) => v >= 0 ? $"+{v / 1048576.0:N1} MB" : $"-{-v / 1048576.0:N1} MB";

    // ───────────────────────── 图标像素级诊断 ─────────────────────────

    /// <summary>
    /// 逐项拆解图标提取，回答三个问题：
    ///   ① 每一档 Imagelist 的 HICON 内部位图**真实像素尺寸**是多少（16? 32? 48? 256?）；
    ///   ② 渲染出来后有效内容占画布多大（判定图标资源是否把图形画在大画布角落）；
    ///   ③ 旧算法（DrawIconEx 直接缩放）与新算法（原样渲染 + GDI+ 高质量缩放）的视觉差异。
    /// 输出文本报告 + 并排对照图。
    /// </summary>
    /// <summary>
    /// 虚拟项（此电脑 / 回收站 / 网络）自检。
    ///
    /// 背景：这几个项由 explorer 的命名空间扩展提供，**不是磁盘上的文件**，
    /// 「枚举桌面目录」这种做法天生看不到它们 —— 实测原生 ListView 60 项、目录枚举 58 项，
    /// 差的就是它们。本模式验证整条补救链路：
    ///   ① 能否从原生 ListView 里按显示名认出来；
    ///   ② 能否拿到它们在图**系统图标列表**里的索引（LVIF_IMAGE）；
    ///   ③ 能否按该索引真的渲染出图标（并输出对照图）。
    /// </summary>
    /// <summary>
    /// UI 自检（<c>--uiprobe [秒]</c>）：真实启动 MainContext，
    /// 间隔打印每个格子的项数 / 模型尺寸 / 窗口位置，最后优雅退出。
    /// 观察点放在多次采样上 —— 能看出「项是什么时候被清空的」。
    /// </summary>
    private static void RunUiProbe(int seconds)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        Program.Silent = true;
        ApplicationConfiguration.Initialize();

        var ctx = new UI.MainContext(autoTakeover: true, readOnlyData: true);

        var ctxType = ctx.GetType();
        var formsField = ctxType.GetField("_forms", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var forms = (Dictionary<int, UI.BoxForm>)formsField!.GetValue(ctx)!;

        UI.BoxView.Trace = s => Console.WriteLine("    ~ " + s);

        int n = 0;
        var t = new System.Windows.Forms.Timer { Interval = 2000 };
        t.Tick += (_, _) =>
        {
            n++;
            Console.WriteLine($"── t+{n * 2}s ──");
            foreach (var (id, f) in forms.OrderBy(kv => kv.Key))
            {
                var m = f.Model;
                Console.WriteLine($"  格子 {id,-4} 项数={f.ItemCount,-3} 模型=({m.X},{m.Y}) {m.Width}×{m.Height} " +
                                  $"折叠={m.Collapsed} 手工尺寸={m.ManualSize} 窗口={f.Bounds}");
            }

            if (n * 2 >= seconds)
            {
                t.Stop(); t.Dispose();
                Console.WriteLine("── 退出 ──");
                ctx.Shutdown();
            }
        };
        t.Start();

        Application.Run(ctx);
    }

    /// <summary>
    /// 优雅退出（<c>--quit</c>）：向已运行实例广播 <see cref="ShellCommand.Quit"/>。
    ///
    /// 为什么需要它：主程序是托盘常驻的单实例应用，没有可见主窗口，
    /// 用 taskkill 强杀会跳过退出流程里的「恢复原生桌面图标」这一步 ——
    /// 结果是 explorer 的桌面 ListView 一直隐藏着，桌面图标全消失。
    /// 走命令总线让实例自己执行 <c>MainContext.ExitApp()</c> 才是安全的收尾方式。
    /// </summary>
    private static void RunQuit()
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;

        using var mutex = new Mutex(true, Program.MutexName, out bool createdNew);
        if (createdNew)
        {
            Console.WriteLine("没有正在运行的实例，无需退出。");
            return;
        }

        Console.WriteLine("已向运行中的实例发出退出指令，等待其还原桌面…");
        bool sent = CmdBus.Broadcast(ShellCommand.Quit);
        Console.WriteLine(sent ? "指令已投递。" : "指令投递失败（可能实例已退出）。");

        // 退出是异步的：等一会儿让它把原生图标恢复完，再回报结果
        for (int i = 0; i < 40; i++)
        {
            Thread.Sleep(150);
            using var m2 = new Mutex(true, Program.MutexName, out bool alone);
            if (alone) { Console.WriteLine("实例已退出。"); return; }
        }
        Console.WriteLine("等待超时：实例仍未退出（桌面图标可能已被还原）。");
    }

    /// <summary>开机自启开关 —— 同时写 Run 键与启动文件夹快捷方式，并回报两处状态</summary>
    private static void RunStartup(bool enable)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;

        bool ok = Startup.Apply(enable);

        Console.WriteLine($"开机自启 → {(enable ? "登记" : "取消")}  结果={(ok ? "成功" : "失败")}");
        Console.WriteLine($"  将登记的目标 exe : {Startup.TargetExeForDiagnostics ?? "(未解析到)"}");
        Console.WriteLine($"  Run 键           : {(Startup.InRegistry ? "已登记" : "无")}");
        Console.WriteLine($"  启动文件夹        : {(Startup.InStartupFolder ? Startup.ShortcutPath : "无")}");
        Console.WriteLine($"  综合 IsEnabled    : {Startup.IsEnabled}");
    }

    /// <summary>
    /// 拉伸自检 —— 两件事：
    ///   ① <b>回归</b>：自动尺寸（<c>MeasureContent</c> 给的宽）反推出的列数，
    ///      必须等于旧的固定列数 <c>min(IdealColumns, 项数)</c>，否则就是改坏了原有行为；
    ///   ② <b>新行为</b>：窗口宽度变化时列数跟随、图标重排，且不会排到可视区外。
    /// </summary>
    private static void RunResizeTest()
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        Program.Silent = true;

        var settings = new AppSettings { IconSize = 40, GlassOpacity = 92, Theme = "Dark" };
        using var icons = new UI.IconCache(settings.IconSize);

        var items = DesktopScanner.Scan();
        var defs = DeskData.CreateDefault();

        bool allOk = true;

        foreach (var id in new[] { BoxIds.System, BoxIds.MyFolders, BoxIds.MyFiles, BoxIds.Apps })
        {
            var box = defs.FindBox(id);
            if (box == null) continue;

            var list = items.Where(i => i.BoxId == id).ToList();
            if (list.Count == 0) list = items.Take(7).ToList();

            var view = new UI.BoxView(id, icons, settings)
            {
                IdealColumns = id == BoxIds.Apps ? 5 : 3,
                Title = box.Name,
            };
            view.SetItems(list);

            var want = view.MeasureContent();
            view.Size = want;                 // 自动尺寸
            int autoCols = view.CurrentColumns;
            int expected = Math.Min(view.IdealColumns, Math.Max(1, list.Count));

            bool ok = autoCols == expected;
            allOk &= ok;

            Console.WriteLine($"[格子 {id} {box.Name}]  项数 {list.Count}  理想列数 {view.IdealColumns}");
            Console.WriteLine($"   自动尺寸 {want.Width}×{want.Height}  单元格 {view.CurrentCellSize.Width}×{view.CurrentCellSize.Height}");
            Console.WriteLine($"   ① 回归：反推列数 {autoCols} = 期望 {expected}  {(ok ? "✓" : "✗ 破坏原有行为！")}");
            Console.WriteLine($"   ② 拉伸：");
            Console.WriteLine($"      {"宽度",-8}{"列数",-8}{"内容高",-10}{"可视高",-10}{"需滚动",-8}图标是否溢出");
            foreach (var w in new[] { 90, 140, 200, 300, 420, 600, 900, 1400 })
            {
                view.Size = new Size(w, 400);
                int cols = view.CurrentColumns;
                int contentH = view.CurrentContentHeight;

                // 用到的总宽（左右内边距 + 列 × 单元格宽），不能超出客户区。
                // 窗口比最小宽度还窄时列数已被 clamp 到 1，属于用户拖过头，不算失败。
                int pad = (view.MinWindowWidth - view.CurrentCellSize.Width) / 2;
                int usedW = pad * 2 + cols * view.CurrentCellSize.Width;
                bool fits = w < view.MinWindowWidth || usedW <= w + 1;
                allOk &= fits;

                bool scroll = contentH > 400;
                Console.WriteLine($"      {w,-8}{cols,-8}{contentH,-10}{400,-10}{(scroll ? "是" : "否"),-8}{(fits ? "否 ✓" : $"是 ✗（用到 {usedW}px）")}");
            }
            Console.WriteLine();
        }

        Console.WriteLine(allOk ? "总判定：通过 ✓" : "总判定：存在失败 ✗");
    }

    /// <summary>
    /// 透明度诊断 —— 回答「为什么调了透明度，图标却不变」。
    ///
    /// 做法：拿同一个格子，在「顶层窗口（未嵌入）」与「嵌入桌面层 Progman」两种状态下，
    /// 各用 1.00 / 0.67 / 0.35 三档 <c>Form.Opacity</c> 显示，抓屏后统计区域亮度。
    ///
    /// 判读方式：
    ///   · 若「平均亮度」与「高亮 P99」都随 alpha 同步上升/下降 → 分层 alpha 整体生效（图标也变）；
    ///   · 若只有「平均亮度」变、而「高亮 P99」几乎不动 → 背景透了但图标没透（就是用户看到的现象）；
    ///   · 若两者都不动 → <c>SetParent</c> 嵌入后分层窗口整条失效（背景也没真透）。
    /// </summary>
    private static void RunGlassTest(string? outDir)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        Program.Silent = true;

        void W(string s) => Console.WriteLine(s);

        outDir ??= ".";
        Directory.CreateDirectory(outDir);

        var settings = new AppSettings { IconSize = 40, GlassOpacity = 100, Theme = "Dark" };
        using var icons = new UI.IconCache(settings.IconSize);

        // 挑内容最多的一个格子，图标 / 文字都够显眼
        var all = DesktopScanner.Scan();
        var box = DeskData.CreateDefault().FindBox(BoxIds.MyFiles)!;
        var list = all.Where(i => i.BoxId == BoxIds.MyFiles).ToList();
        if (list.Count < 3) list = all.Take(Math.Min(8, all.Count)).ToList();

        var alphas = new[] { 1.0, 0.67, 0.35 };
        var progman = Native.FindWindow("Progman", null);

        // Progman 客户区原点在屏幕上的偏移。SetParent 之后所有坐标都相对它。
        int dx = 0, dy = 0;
        if (progman != IntPtr.Zero && Native.GetWindowRect(progman, out var pr))
        {
            dx = pr.Left; dy = pr.Top;
        }

        W($"Progman 句柄 : 0x{progman.ToInt64():X}  屏幕矩形偏移 ({dx},{dy})");
        W($"测试项数     : {list.Count}");
        W("");
        W($"{"状态",-10}{"alpha",-8}{"平均亮度",-10}{"高亮P99",-10}{"位置",-14}分层窗口状态");

        var form = new UI.BoxForm(box, icons, settings, () => { }, () => { });
        form.SetItems(list);
        form.Location = new Point(80, 80);
        form.Show();

        // 每个动作跑一个 tick —— 必须让 WinForms 消息循环正常转起来。
        // 用 DoEvents() 硬撑的话，SetParent / Close 这类同步消息会死锁（已实测卡死）。
        var queue = new Queue<Action>();

        void Snap(string state, double alpha)
        {
            queue.Enqueue(() =>
            {
                form.Opacity = alpha;
                form.Refresh();
                form.Update();

                // ── 分层窗口状态探测：决定「透明到底生没生效」──
                long ex = Native.GetWindowLongPtr(form.Handle, Native.GWL_EXSTYLE).ToInt64();
                bool layered = (ex & Native.WS_EX_LAYERED) != 0;
                bool lwaOk = Native.SetLayeredWindowAttributes(
                    form.Handle, 0, (byte)Math.Round(alpha * 255), Native.LWA_ALPHA);

                var rect = RectOf(form);
                using var bmp = new Bitmap(Math.Max(1, rect.Width), Math.Max(1, rect.Height));
                using (var gg = Graphics.FromImage(bmp))
                    gg.CopyFromScreen(rect.X, rect.Y, 0, 0, bmp.Size);
                bmp.Save(Path.Combine(outDir, $"glass-{state}-{alpha:0.00}.png"), ImageFormat.Png);

                var (avg, p99) = BrightnessStats(bmp);
                W($"{state,-10}{alpha,-8:0.00}{avg,-10:0.0}{p99,-10:0.0}{rect.X + "," + rect.Y,-14}" +
                  $"Opacity={form.Opacity:0.00} LAYERED={(layered ? "有" : "无")} LWA={(lwaOk ? "成功" : "失败")}");
            });
        }

        // ── 第一组：顶层窗口（未嵌入）──
        foreach (var a in alphas) Snap("顶层窗口", a);

        // ── 切到嵌入桌面层 ──
        queue.Enqueue(() =>
        {
            if (progman == IntPtr.Zero) { W(""); W("Progman 未找到，跳过嵌入测试"); return; }

            form.EmbedInto(progman);

            // SetParent 之后 SetWindowPos 用的是**父窗口客户区**坐标。
            // 实测 Progman 客户区原点并不在屏幕原点，所以必须反算，
            // 否则窗口会被摆到屏幕外，截图全是黑的。
            Native.SetWindowPos(form.Handle, IntPtr.Zero, 80 - dx, 80 - dy,
                form.Width, form.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            W("");
            W($"父窗口 = Progman 0x{progman.ToInt64():X}（客户区偏移 {dx},{dy}）");
        });

        // ── 第二组：嵌入桌面层 ──
        foreach (var a in alphas) Snap("嵌入桌面", a);

        queue.Enqueue(() =>
        {
            form.EmbedInto(IntPtr.Zero);
            form.Hide();
            Application.ExitThread();
        });

        var timer = new System.Windows.Forms.Timer { Interval = 330 };
        timer.Tick += (_, _) =>
        {
            if (queue.Count == 0) { timer.Stop(); Application.ExitThread(); return; }
            try { queue.Dequeue()(); }
            catch (Exception ex) { W("  步骤异常: " + ex.Message); }
        };
        timer.Start();
        Application.Run();

        W("");
        W("判读：平均亮度≈背景，高亮P99≈图标/文字。两者随 alpha 同步变化 = 整体透明生效。");
        W("      只有平均亮度变、P99 不动 = 背景透了图标没透；两者都不动 = 分层整条失效。");
    }

    /// <summary>
    /// 透明度诊断（完整路径）—— 走 <see cref="UI.MainContext"/> 真实启动（接管桌面 + 嵌入 Progman），
    /// 再逐档改玻璃不透明度并全屏截图。这测的是用户实际看到的场景，
    /// 而不是「造一个孤立窗口」那种近似环境（后者不会被桌面层与其它工具影响）。
    /// </summary>
    /// <summary>
    /// 逐像素 alpha 自检 —— 直接回答用户的第 3 条反馈：「调整透明度，图标的透明度不变」。
    ///
    /// 做法：真实启动（接管桌面 + 嵌入 Progman），逐档改玻璃不透明度后抓全屏，
    /// 再**只对格子矩形内部**取样：
    ///   · 底板色 = 该区域内出现频率最高的颜色（格子面积以底板为主）
    ///   · 图标/文字 = 亮度 99.5 分位（图标高光与白字就是最亮的那批像素）
    ///
    /// 判定：
    ///   · 底板色随玻璃档位明显变化    → 透明度确实生效
    ///   · 图标亮度在几档之间几乎不动  → 图标不受影响（旧做法下会同步变淡）
    ///
    /// 最后再跑一次「就地改名」探针，确认分层窗口里的子控件（TextBox）仍能正常显示 ——
    /// 这是 UpdateLayeredWindow 最容易踩的坑。
    /// </summary>
    private static void RunAlphaProbe(string? outDir)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        Program.Silent = true;

        void W(string s) => Console.WriteLine(s);

        outDir ??= ".";
        Directory.CreateDirectory(outDir);

        ApplicationConfiguration.Initialize();

        W("══════ 逐像素 alpha 自检 ══════");
        W("");

        var ctx = new UI.MainContext(autoTakeover: true, readOnlyData: true);

        // 计划表：先「顶层」跑三档玻璃 + 改名探针，再「嵌入桌面层」跑三档 ——
        // 两种状态都要覆盖，因为 UpdateLayeredWindow 对子窗口的解释可能不同。
        var plan = new List<(bool Embed, int Glass, bool Rename)>();
        foreach (var g in new[] { 100, 67, 40 }) plan.Add((false, g, false));
        plan.Add((false, 0, true));                              // 顶层态下的改名探针
        foreach (var g in new[] { 100, 67, 40 }) plan.Add((true, g, false));

        var rows = new List<(bool Embed, int Glass, Color Panel, double PanelLum, double InkLum)>();

        int i = 0;
        bool apply = true;          // true = 这一拍设置，false = 这一拍抓屏
        int renameBoxId = -1;       // 改名发生在哪个格子 —— 取样必须取同一个
        double renameBase = -1;
        var whiteByBox = new Dictionary<int, double>();   // 格子 → 改名前近白像素占比

        var timer = new System.Windows.Forms.Timer { Interval = 780 };

        timer.Tick += (_, _) =>
        {
            // ── 抓屏取样 ──
            if (!apply)
            {
                apply = true;
                try
                {
                    var boxes = ctx.BoxRects();
                    if (boxes.Count == 0) { W("   （没有可见格子，跳过取样）"); return; }

                    var step = plan[i];

                    // 改名阶段取「发生改名的那个格子」，否则取面积最大的格子（内容最多、底板占比最稳）
                    var pick = step.Rename
                        ? boxes.FirstOrDefault(t => t.Id == renameBoxId)
                        : boxes.OrderByDescending(t => (long)t.Rect.Width * t.Rect.Height).First();

                    if (pick.Rect.Width < 20) { W("   （找不到目标格子，跳过取样）"); return; }

                    var s = SampleBox(pick.Rect);

                    if (step.Rename)
                    {
                        bool ok = s.White > renameBase + 0.005;
                        W($"   格子 {pick.Id} 改名中：近白像素占比 = {s.White:P1}（改名前基线 {renameBase:P1}）");
                        W(ok
                            ? "   ✔ 编辑框已绘出 —— 分层窗口里子控件（TextBox）正常"
                          : "   ✘ 近白像素没有增加 —— 子控件被分层位图遮住了");
                    }
                    else
                    {
                        whiteByBox[pick.Id] = s.White;
                        rows.Add((step.Embed, step.Glass, s.Panel, s.PanelLum, s.InkLum));
                        W($"   格子 {pick.Id} @ {pick.Rect.Location}：底板={C3(s.Panel)}（亮度 {s.PanelLum,5:0.0}）　" +
                          $"图标/文字最亮={C3(s.Ink)}（亮度 {s.InkLum,5:0.0}）");
                    }
                }
                catch (Exception ex) { W("   取样失败：" + ex.Message); }

                i++;
                W("");
                return;
            }

            // ── 逐档设置 / 改名探针 ──
            if (i < plan.Count)
            {
                var (e, g, rn) = plan[i];
                ctx.SetEmbedForTest(e);

                if (rn)
                {
                    var (started, id) = ctx.ProbeRename();
                    renameBoxId = id;
                    renameBase = whiteByBox.TryGetValue(id, out var w) ? w : 0;
                    W("── 就地改名探针：" + (started ? $"格子 {id} 已进入编辑态" : "没有可改名项，跳过"));
                }
                else
                {
                    ctx.SetGlassForTest(g);
                    W($"── 嵌入桌面层={(e ? "是" : "否")}　玻璃不透明度 = {g}%");
                    W(ctx.DescribeForms().TrimEnd());
                }

                apply = false;
                return;
            }

            // ── 收尾 ──
            timer.Stop();

            W("══════ 取样汇总 ══════");
            foreach (var grp in rows.GroupBy(r => r.Embed))
            {
                W("");
                W($"  【{(grp.Key ? "已嵌入桌面层" : "普通顶层窗口")}】");
                W($"    {"玻璃",-6}{"底板色",-18}{"底板亮度",-10}{"图标亮度",-10}");
                foreach (var r in grp)
                    W($"    {r.Glass + "%",-6}{C3(r.Panel),-18}{r.PanelLum,-10:0.0}{r.InkLum,-10:0.0}");
            }

            W("");
            foreach (var grp in rows.GroupBy(r => r.Embed))
            {
                double inkMin = grp.Min(r => r.InkLum), inkMax = grp.Max(r => r.InkLum);
                double panMin = grp.Min(r => r.PanelLum), panMax = grp.Max(r => r.PanelLum);
                string tag = grp.Key ? "已嵌入" : "顶层  ";

                W($"  {tag} 图标亮度波动 {inkMin:0.0}~{inkMax:0.0}" +
                  (inkMax > 0 ? $"（偏差 {(inkMax - inkMin) / inkMax:P1}）" : "") +
                  $"　底板亮度变化 {panMin:0.0}~{panMax:0.0}" +
                  (panMax > 0 ? $"（幅度 {(panMax - panMin) / Math.Max(1, panMax):P0}）" : ""));

                W(inkMax > 0 && (inkMax - inkMin) / inkMax < 0.05
                    ? "         ✔ 调透明度时图标不动 —— 符合「图标透明度不变」"
                    : "         ✘ 图标亮度随透明度变化 —— 仍在走整窗 alpha");
            }

            W("");
            W("退出前状态：");
            W(ctx.DescribeForms());
            W("正在还原桌面并退出…");
            ctx.Shutdown();
        };

        timer.Start();
        Application.Run(ctx);
        W("已退出，桌面应已还原。");
    }

    /// <summary>在格子矩形内取样：底板色（众数）+ 图标/文字亮度（P99.5）+ 近白像素占比</summary>
    private static (Color Panel, double PanelLum, Color Ink, double InkLum, double White) SampleBox(Rectangle box)
    {
        var b = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        if (box.Width < 20 || box.Height < 20) return (Color.Black, 0, Color.Black, 0, 0);

        using var shot = new Bitmap(b.Width, b.Height);
        using (var gg = Graphics.FromImage(shot))
            gg.CopyFromScreen(b.X, b.Y, 0, 0, shot.Size);

        // 内缩，避开圆角与 1px 描边
        int m = 6;
        int x0 = Math.Max(0, box.Left - b.X + m);
        int y0 = Math.Max(0, box.Top - b.Y + m);
        int w = Math.Min(shot.Width - x0, box.Width - m * 2);
        int h = Math.Min(shot.Height - y0, box.Height - m * 2);
        if (w <= 4 || h <= 4) return (Color.Black, 0, Color.Black, 0, 0);

        var px = new (double Lum, Color C)[w * h];
        int white = 0;

        var d = shot.LockBits(new Rectangle(0, 0, shot.Width, shot.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[d.Stride * shot.Height];
            Marshal.Copy(d.Scan0, buf, 0, buf.Length);

            int k = 0;
            for (int y = y0; y < y0 + h; y++)
            {
                int row = y * d.Stride;
                for (int x = x0; x < x0 + w; x++)
                {
                    int o = row + x * 4;
                    byte bl = buf[o], gr = buf[o + 1], rd = buf[o + 2];
                    var c = Color.FromArgb(rd, gr, bl);
                    px[k++] = ((rd + gr + bl) / 3.0, c);
                    if (rd > 235 && gr > 235 && bl > 235) white++;
                }
            }
        }
        finally { shot.UnlockBits(d); }

        Array.Sort(px, (a, b2) => a.Lum.CompareTo(b2.Lum));

        var panel = px[px.Length / 2];                        // 中位 ≈ 底板主导色
        var ink = px[(int)((px.Length - 1) * 0.995)];         // 最亮 0.5% ≈ 图标高光 / 白字
        return (panel.C, panel.Lum, ink.C, ink.Lum, white / (double)px.Length);
    }

    private static string C3(Color c) => $"({c.R},{c.G},{c.B})";

    private static void RunGlassFull(string? outDir)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        Program.Silent = true;

        void W(string s) => Console.WriteLine(s);

        outDir ??= ".";
        Directory.CreateDirectory(outDir);

        ApplicationConfiguration.Initialize();

        var ctx = new UI.MainContext(autoTakeover: true, readOnlyData: true);
        W("已启动完整实例（接管桌面 + 嵌入 Progman）。开始逐档改玻璃不透明度并全屏截图…");
        W("");
        W(ctx.DescribeForms());

        var steps = new[] { 100, 67, 40 };
        int step = 0;

        var timer = new System.Windows.Forms.Timer { Interval = 520 };
        timer.Tick += (_, _) =>
        {
            int s = step++;
            int gi = s / 2;

            if (gi >= steps.Length)
            {
                timer.Stop();
                W("");
                W("退出前状态：");
                W(ctx.DescribeForms());
                W("正在还原桌面并退出…");
                ctx.Shutdown();
                return;
            }

            if (s % 2 == 0)
            {
                ctx.SetGlassForTest(steps[gi]);
                W($"── 设置玻璃不透明度 = {steps[gi]}%");
                W(ctx.DescribeForms().TrimEnd());
                return;
            }

            try
            {
                var b = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                using var bmp = new Bitmap(b.Width, b.Height);
                using (var gg = Graphics.FromImage(bmp))
                    gg.CopyFromScreen(b.X, b.Y, 0, 0, bmp.Size);
                string p = Path.Combine(outDir, $"gf-{steps[gi]}.png");
                bmp.Save(p, ImageFormat.Png);
                W($"   已截图 → {p}");
                W("");
            }
            catch (Exception ex) { W("   截图失败: " + ex.Message); }
        };
        timer.Start();

        Application.Run(ctx);
        W("已退出，桌面应已还原。");
    }

    /// <summary>取窗口在屏幕上的真实矩形。GetWindowRect 返回屏幕坐标，SetParent 之后依然正确。</summary>
    private static Rectangle RectOf(Form f)
    {
        if (Native.GetWindowRect(f.Handle, out var r))
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return new Rectangle(f.Left, f.Top, Math.Max(1, f.Width), Math.Max(1, f.Height));
    }

    /// <summary>区域亮度统计：平均值反映整体（背景为主），P99 反映最亮的那 1%（图标 / 文字）</summary>
    private static (double Avg, double P99) BrightnessStats(Bitmap bmp)
    {
        var vals = new List<int>(bmp.Width * bmp.Height);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int o = row + x * 4;
                    vals.Add((buf[o] + buf[o + 1] + buf[o + 2]) / 3);
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        if (vals.Count == 0) return (0, 0);
        vals.Sort();
        return (vals.Average(), vals[(int)((vals.Count - 1) * 0.99)]);
    }

    private static void RunVirTest(string? outPath, int size = 96)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        Program.Silent = true;

        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        W("══════ 虚拟项（此电脑 / 回收站 / 网络）自检 ══════");
        W("");

        var items = DesktopScanner.Scan();
        int realCount = items.Count;
        W($"① 目录枚举          : {realCount} 项");

        var host = new DesktopHost();
        if (!host.Locate())
        {
            W("② 桌面窗口定位      : 失败 —— 无法继续（explorer 未运行？）");
            return;
        }

        // 这条命令专门做「ListView 索引 vs SHGetFileInfo 索引」对照 —— 必须显式要图标索引
        var all = host.EnumerateIcons(needImageIndex: true);
        W($"② 原生 ListView 项数: {all.Count} 项");
        W($"   差值              : {all.Count - realCount} —— 应当等于桌面上的虚拟项个数");

        // ★ 关键交叉验证：原生 ListView 的图标索引到底是不是「系统 Imagelist」的索引？
        //   对真实文件来说两者都取得到，正好可以拿来当基准。
        //   若一致 → 虚拟项用 ListView 索引是对的；若不一致 → 这条取图标路径必须换。
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
            if (!it.IsVirtual) byName[it.Name] = it.Path;

        W("");
        W("交叉验证（真实项）：ListView 图标索引 vs SHGetFileInfo 索引");
        W($"{"显示名",-30} {"ListView",-10} {"SHGetFileInfo",-14} 一致?");
        W(new string('─', 66));

        int cmp = 0, same = 0;
        foreach (var ic in all)
        {
            if(!byName.TryGetValue(ic.Text, out var path)) continue;
            if (cmp >= 8) break;

            if (!UI.IconCache.ShellIconIndex(path, true, Path.GetExtension(path), out int sysIdx)) continue;

            bool ok = sysIdx == ic.ImageIndex;
            cmp++;
            if (ok) same++;
            W($"{ic.Text,-30} {ic.ImageIndex,-10} {sysIdx,-14} {(ok ? "是" : "否  ← 不一致")}");
        }
        W($"—— 抽查 {cmp} 项，一致 {same} 项 {(cmp > 0 && cmp == same ? "（同一张表，索引可直用）" : "（不是同一张表！）")}");

        int added = DesktopScanner.AppendVirtualItems(items, host);
        W($"③ 补入虚拟项        : {added} 个  → 合计 {items.Count} 项");
        W("");

        var virt = items.Where(i => i.IsVirtual).ToList();
        if (virt.Count == 0)
        {
            W("⚠ 没有识别到任何虚拟项。");
            W("  若桌面上确实显示了「此电脑 / 回收站」，请检查 ListView 里的显示名是否在 VirtualItems 表内。");
            return;
        }

        using var icons = new UI.IconCache(size);
        UI.IconCache.TraceEnabled = true;
        UI.IconCache.ClearTrace();

        W($"{"名称",-10} {"归属格子",-10} {"伪路径",-44} 渲染结果");
        W(new string('─', 92));

        var shots = new List<(string Name, Bitmap Bmp)>();
        bool allOk = true;
        foreach (var it in virt)
        {
            var img = icons.GetIcon(it.Path);
            var bmp = new Bitmap(img);

            string boxName = it.BoxId switch
            {
                BoxIds.System => "系统",
                BoxIds.MyFolders => "我的文件夹",
                BoxIds.MyFiles => "我的文件",
                BoxIds.Apps => "APP",
                _ => it.BoxId.ToString(),
            };

            bool blank = UI.IconCache.IsBlank(bmp);
            if (blank) allOk = false;
            W($"{it.Name,-10} {boxName,-10} {it.Path,-44} " +
              $"{bmp.Width}×{bmp.Height}{(blank ? "  ⚠ 空白位图" : "  正常")}");

            shots.Add((it.Name, bmp));
        }

        W("");
        W("取图标溯源（走到哪一档 / 原生边长 / 内容占比）：");
        foreach (var t in UI.IconCache.Traces)
            W($"   {t.Path,-44} {t.Source,-26} 原生 {t.Slot,3}px  内容占比 {t.Fill:P0}");
        W("");

        W($"归类校验：全部应落在「系统」格子 → {(virt.All(v => v.BoxId == BoxIds.System) ? "通过" : "失败")}");
        W($"类型校验：全部应为特殊项(3)    → {(virt.All(v => v.Type == ItemType.Special) ? "通过" : "失败")}");
        W($"图标校验：全部应渲染出非空图标  → {(allOk ? "通过" : "失败")}");

        // ── 输出对照图：每个图标 4× 放大，横向排列 ──
        if (shots.Count > 0 && outPath is { Length: > 0 })
        {
            const int z = 4, pad = 16, lbl = 22;
            int cellW = size * z, cellH = size * z + lbl;
            int w = pad + shots.Count * (cellW + pad);
            int h = pad * 2 + cellH;

            using var canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(canvas);
            g.Clear(Color.FromArgb(255, 22, 22, 26));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            using var fg = new SolidBrush(Color.FromArgb(255, 226, 226, 232));
            using var font = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);

            for (int i = 0; i < shots.Count; i++)
            {
                int x = pad + i * (cellW + pad);
                g.DrawImage(shots[i].Bmp, new Rectangle(x, pad, cellW, cellH - lbl));
                g.DrawString(shots[i].Name, font, fg, x, pad + cellH - lbl + 2);
            }

            canvas.Save(outPath, ImageFormat.Png);
            W($"");
            W($"对照图已保存：{outPath}（每个图标 4×，{w}×{h}）");
        }

        foreach (var (_, b) in shots) b.Dispose();

        if (outPath is { Length: > 0 })
            File.WriteAllText(Path.ChangeExtension(outPath, ".txt"), sb.ToString(), new UTF8Encoding(true));
    }

    private static void RunIconProbe(string? outPath)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;

        const int target = 40;
        var items = DesktopScanner.Scan();

        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        W("══════ 图标像素级诊断 ══════");
        W($"目标尺寸 {target}px · 桌面 {items.Count} 项");
        W("");
        W("Imagelist 各档：J=Jumbo X=ExtraLarge L=Large（括号内为 HICON 内部位图真实边长）");
        W("");
        W($"{"项目",-24} {"idx",-5} {"Jumbo",-22} {"ExtraLarge",-22} {"Large",-22} {"命中",-28} 内容占比");
        W(new string('─', 150));

        UI.IconCache.TraceEnabled = true;
        // 诊断路径会自行 Dispose 候选位图，因此关掉中间位图复用池（池里的图不归调用方所有）
        UI.IconCache.UsePool = false;
        UI.IconCache.ClearTrace();

        const int cellH = 92;
        int rows = Math.Max(1, items.Count);

        using var sheet = new Bitmap(820, rows * cellH + 26);
        using (var sg = Graphics.FromImage(sheet))
        {
            sg.Clear(Color.FromArgb(46, 46, 50));
            using var f = new Font("Microsoft YaHei UI", 8F);
            using var fs = new Font("Consolas", 7F);

            sg.DrawString("项目 / 类型 / 路径", f, Brushes.White, 8, 6);
            sg.DrawString("A 旧", f, Brushes.White, 290, 6);
            sg.DrawString("B 新(就近)", f, Brushes.White, 345, 6);
            sg.DrawString("C 新(最大)", f, Brushes.White, 405, 6);
            sg.DrawString("Jumbo 原样", f, Brushes.White, 480, 6);
            sg.DrawString("ExtraLarge 原样", f, Brushes.White, 560, 6);
            sg.DrawString("Large 原样", f, Brushes.White, 645, 6);

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                int y = 26 + i * cellH;
                bool ex = Directory.Exists(it.Path) || File.Exists(it.Path);
                string ext = Path.GetExtension(it.Path);
                if (ext.Length == 0) ext = ".dat";

                sg.DrawString(Trunc(it.Name, 16), f, Brushes.White, 8, y + 2);
                sg.DrawString($"{it.Type} {ext}", fs, Brushes.LightGray, 8, y + 18);
                sg.DrawString(Trunc(it.Path, 34), fs, Brushes.Gray, 8, y + 30);

                // A：旧算法（DrawIconEx 直接缩放到 40）
                Try(sg, 290, y, 40, () => UI.IconCache.ExtractLegacy(it.Path, target, ex));
                // B：新算法（就近档 + GDI+ 高质量缩放）
                Try(sg, 345, y, 40, () => UI.IconCache.ExtractV2(it.Path, target, ex));
                // C：新算法变体（取最大可用档 + GDI+ 高质量缩放）
                Try(sg, 405, y, 40, () => ExtractLargest(it.Path, target, ex, ext));

                if (!UI.IconCache.ShellIconIndex(it.Path, ex, ext, out int idx))
                {
                    W($"{Trunc(it.Name, 22),-24} {"—",-5} 无系统图标索引");
                    continue;
                }

                sg.DrawString($"idx={idx}", fs, Brushes.Khaki, 8, y + 44);

                var jumbo = UI.IconCache.FromImageList(idx, Native.SHIL_JUMBO);
                var xl = UI.IconCache.FromImageList(idx, Native.SHIL_EXTRALARGE);
                var lg = UI.IconCache.FromImageList(idx, Native.SHIL_LARGE);

                W($"{Trunc(it.Name, 22),-24} {idx,-5} {Desc(jumbo),-22} {Desc(xl),-22} {Desc(lg),-22}");

                Raw(sg, fs, jumbo, 480, y);
                Raw(sg, fs, xl, 560, y);
                Raw(sg, fs, lg, 645, y);
            }
        }

        sheet.Save("iconprobe.png", ImageFormat.Png);
        W("");
        W("对照图: iconprobe.png");
        W("A=旧算法(DrawIconEx 直接缩放)  B=新算法(原样渲染+高质量缩放)  右侧=各档 Imagelist 原样渲染");

        var t = outPath ?? "iconprobe.txt";
        try { File.WriteAllText(t, sb.ToString(), new UTF8Encoding(false)); } catch { }
        Console.WriteLine($"报告已写入: {t}");
    }

    /// <summary>
    /// 变体：在非畸形（内容占比 ≥ 30%）的候选档里取**尺寸最大**的一档，
    /// 再交给 GDI+ 高质量缩放 —— 用于和「就近取档」比较，判断哪个更清晰。
    /// </summary>
    private static Bitmap? ExtractLargest(string path, int target, bool exists, string ext)
    {
        if (!UI.IconCache.ShellIconIndex(path, exists, ext, out int idx)) return null;

        UI.IconCache.Cand? pick = null;
        foreach (int sh in new[] { Native.SHIL_JUMBO, Native.SHIL_EXTRALARGE, Native.SHIL_LARGE })
        {
            if (UI.IconCache.FromImageList(idx, sh) is not { } c) continue;
            if (UI.IconCache.IsBlank(c.Bmp) || c.FillPct < 0.30) { c.Bmp.Dispose(); continue; }

            if (pick is null) { pick = c; continue; }
            if (c.NativePx > pick.Value.NativePx) { pick.Value.Bmp.Dispose(); pick = c; }
            else c.Bmp.Dispose();
        }

        if (pick is not { } best) return null;
        var outp = UI.IconCache.Scale(best.Bmp, target);
        best.Bmp.Dispose();
        return outp;
    }

    /// <summary>把某一档的原样渲染图按 48px 画到对照表上，并标注真实像素尺寸与内容占比</summary>
    private static void Raw(Graphics g, Font f, UI.IconCache.Cand? c, int x, int y)
    {
        if (c is not { } v) return;
        try
        {
            g.DrawImage(v.Bmp, x, y, 48, 48);
            g.DrawString($"{v.NativePx}px {v.FillPct:P0}", f,
                v.FillPct < 0.3 ? Brushes.Orange : Brushes.LightGreen, x, y + 50);
        }
        catch { }
        finally { v.Bmp.Dispose(); }
    }

    private static string Desc(UI.IconCache.Cand? c)
        => c is { } v ? $"{v.ListPx}/{v.NativePx}px {v.FillPct:P0}" : "无";

    private static void Try(Graphics g, int x, int y, int size, Func<Bitmap?> make)
    {
        try
        {
            using var b = make();
            if (b != null) g.DrawImage(b, new Rectangle(x, y, size, size));
        }
        catch { }
    }

    private static void DrawCell(Graphics g, int x, int y, Func<Image> make)
    {
        try
        {
            using var b = make();
            g.DrawImage(b, x, y);
        }
        catch (Exception ex)
        {
            using var f = new Font("Consolas", 5F);
            g.DrawString("!" + ex.GetType().Name, f, Brushes.Red, x, y);
        }
    }

    /// <summary>旧实现：Icon.ToBitmap（仅用于对照）</summary>
    private static Bitmap OldExtract(string path, int size)
    {
        var shfi = new Native.SHFILEINFO();
        uint flags = Native.SHGFI_ICON | Native.SHGFI_LARGEICON;
        uint attrs = 0;
        string probe = path;
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            var ext = Path.GetExtension(path);
            if (ext.Length == 0) ext = ".dat";
            probe = "__probe__" + ext;
            flags |= Native.SHGFI_USEFILEATTRIBUTES;
            attrs = 0x80;
        }
        var r = Native.SHGetFileInfo(probe, attrs, ref shfi,
            (uint)Marshal.SizeOf<Native.SHFILEINFO>(), flags);
        if (r == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            return new Bitmap(size, size);

        try
        {
            using var ico = Icon.FromHandle(shfi.hIcon);
            using var src = ico.ToBitmap();
            var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.DrawImage(src, 0, 0, size, size);
            return bmp;
        }
        finally { Native.DestroyIcon(shfi.hIcon); }
    }

    // ───────────────────────── 隐藏方法实测 ─────────────────────────

    /// <summary>
    /// 逐种隐藏方式实测，判断在当前 Windows 版本上哪种能真正让原生桌面图标消失。
    /// 每种方式施加后等待 2 秒再检查可见性 —— 因为 explorer 常会异步恢复。
    /// 结束时恢复现场。
    /// </summary>
    private static void RunHideTest(string? outPath)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);

        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        var host = new DesktopHost();
        if (!host.Locate())
        {
            W("❌ 未定位到桌面窗口链，无法测试");
            return;
        }

        W($"桌面链: Progman=0x{host.Progman:X} DefView=0x{host.DefView:X} ListView=0x{host.ListView:X}");
        W($"初始 ListView 可见            : {Native.IsWindowVisible(host.ListView)}");
        W("");

        bool Check(string label)
        {
            Thread.Sleep(2000);
            bool vis = Native.IsWindowVisible(host.ListView);
            W($"  {label,-42} → 可见={vis} {(vis ? "（未生效/被恢复）" : "（已隐藏 ✅）")}");
            return !vis;
        }

        // ① ShowWindow(SW_HIDE) 直接隐藏 ListView
        W("① ShowWindow(ListView, SW_HIDE)");
        Native.ShowWindow(host.ListView, Native.SW_HIDE);
        bool m1 = Check("2 秒后");
        W("");

        // 恢复
        Native.ShowWindow(host.ListView, Native.SW_SHOW);
        Thread.Sleep(600);

        // ② 向 DefView 发 WM_COMMAND(0x7402)
        W("② SendMessage(DefView, WM_COMMAND, 0x7402)");
        Native.SendMessage(host.DefView, Native.WM_COMMAND, (IntPtr)0x7402, IntPtr.Zero);
        bool m2 = Check("2 秒后");
        W("");

        // 若 ② 生效则再发一次恢复
        if (m2)
        {
            Native.SendMessage(host.DefView, Native.WM_COMMAND, (IntPtr)0x7402, IntPtr.Zero);
            Thread.Sleep(600);
        }

        // ③ 去掉 ListView 的 WS_VISIBLE 样式
        W("③ SetWindowLongPtr(ListView, GWL_STYLE & ~WS_VISIBLE)");
        long st = Native.GetWindowLongPtr(host.ListView, Native.GWL_STYLE).ToInt64();
        Native.SetWindowLongPtr(host.ListView, Native.GWL_STYLE, (IntPtr)(st & ~(long)Native.WS_VISIBLE));
        bool m3 = Check("2 秒后");
        W("");

        // 恢复样式
        Native.SetWindowLongPtr(host.ListView, Native.GWL_STYLE, (IntPtr)st);
        Thread.Sleep(600);

        // ④ 把 ListView 移到屏幕外
        W("④ SetWindowPos(ListView, -20000, -20000)");
        Native.GetWindowRect(host.ListView, out var origRect);
        Native.SetWindowPos(host.ListView, IntPtr.Zero, -20000, -20000, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        bool m4 = Check("2 秒后");
        W("");

        // 恢复位置
        if (m4)
        {
            Native.SetWindowPos(host.ListView, IntPtr.Zero, origRect.Left, origRect.Top, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Thread.Sleep(600);
        }

        W("══════════════ 结论 ══════════════");
        W($"  ① ShowWindow                 : {(m1 ? "有效 ✅" : "无效 ❌")}");
        W($"  ② WM_COMMAND 0x7402(DefView) : {(m2 ? "有效 ✅" : "无效 ❌")}");
        W($"  ③ 去掉 WS_VISIBLE            : {(m3 ? "有效 ✅" : "无效 ❌")}");
        W($"  ④ 移出屏幕                   : {(m4 ? "有效 ✅" : "无效 ❌")}");
        W("");
        W($"  最终 ListView 可见           : {Native.IsWindowVisible(host.ListView)}");

        var target = outPath ?? Path.Combine(AppContext.BaseDirectory, "hidetest-result.txt");
        try { File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false)); } catch { }
    }

    // ───────────────────────── 环境探测 ─────────────────────────

    private static void RunProbe(string? outPath)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);

        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        W("═══════════════════════════════════════════════════");
        W(" DeskLite 环境探测");
        W(" 时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        W("═══════════════════════════════════════════════════");
        W("");
        W("【1】会话信息");
        W($"  用户        : {Environment.UserName}");
        W($"  交互式会话  : {Environment.UserInteractive}");
        W($"  64 位进程   : {Environment.Is64BitProcess}");
        W($"  系统        : {Environment.OSVersion}");
        W("");

        W("【2】桌面窗口链（接管前提）");
        var host = new DesktopHost();
        bool ok = host.Locate();
        W($"  Progman                  : 0x{host.Progman:X}");
        W($"  SHELLDLL_DefView         : 0x{host.DefView:X}");
        W($"  SysListView32            : 0x{host.ListView:X}");
        W($"  定位结果                 : {(ok ? "成功 ✅" : "失败 ❌")}");
        if (ok)
        {
            W($"  所属进程                 : {host.ExplorerProcessName()} (PID {host.ExplorerPid})");
            W($"  原生图标当前可见         : {Native.IsWindowVisible(host.ListView)}");
            W("");
            W("【3】原始桌面图标枚举（跨进程读取 SysListView32）");
            var icons = host.EnumerateIcons(60);
            W($"  LVM_GETITEMCOUNT 总数    : {host.ItemCount()}");
            W($"  本次读取                 : {icons.Count} 项");
            foreach (var ic in icons.Take(15))
                W($"    [{ic.Index,2}] {ic.Text}");
            W("");
        }

        W("【4】规则引擎 —— 真实桌面项分类结果");
        var items = DesktopScanner.Scan();
        var grouped = DesktopScanner.GroupByBox(items);
        var (total, folders, shortcuts, files, special) = DesktopScanner.Summary(items);
        W($"  扫描到真实桌面项         : {total} 项");
        W($"  类型码分布               : 文件夹(1)={folders} · 快捷方式(2)={shortcuts} · 特殊(3)={special} · 文件(4)={files}");
        W("");
        foreach (var b in _data_boxes())
        {
            var list = grouped.TryGetValue(b.Id, out var l) ? l : new List<DiscoveredItem>();
            W($"  ── [{b.Id}] {b.Name}（{list.Count} 项）──");
            foreach (var it in list.Take(8))
                W($"     {(int)it.Type}  {it.Name}   ← {Path.GetFileName(Path.GetDirectoryName(it.Path))}");
            if (list.Count > 8) W($"     … 其余 {list.Count - 8} 项");
            W("");
        }

        W("【5】数据层（只读探测 —— 本命令不写盘）");
        var store = new DataStore(readOnly: true);
        var data = store.Load();
        W($"  存储目录                 : {store.Root}");
        W($"  配置版本                 : {data.ConfigVersion}");
        W($"  格子数                   : {data.Boxes.Count}");
        foreach (var b in data.Boxes)
            W($"    [{b.Id}] {b.Name,-8} {b.Width}×{b.Height} @({b.X},{b.Y})  schema=v{b.SchemaVersion}" +
              (b.SchemaVersion < DeskBox.CurrentSchemaVersion ? "  ← 待迁移" : ""));
        W($"  项数                     : {data.Items.Count}");
        var main = new FileInfo(store.MainPath);
        W($"  主文件                   : {(main.Exists ? $"存在 {main.Length} 字节，最后写入 {main.LastWriteTime:yyyy-MM-dd HH:mm:ss}" : "不存在")}");
        W($"  序列化往返               : {(DeskData.Parse(data.Serialize()).Boxes.Count == data.Boxes.Count ? "✓ 一致" : "✗ 不一致")}");
        W($"  备份文件数               : {store.ListBackups().Count}");
        W("");
        W("  ⚠ 本命令以只读方式打开数据层：以上读取不会改动 FencesDataFile.dat 及其滚动备份。");
        W("");
        W("═══════════════════════════════════════════════════");

        var target = outPath ?? Path.Combine(AppContext.BaseDirectory, "probe-result.txt");
        try
        {
            File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"结果已写入: {target}");
        }
        catch { }

        static List<DeskBox> _data_boxes() => DeskData.CreateDefault().Boxes;
    }

    // ═══════════════ 命令行分发 ═══════════════

    /// <summary>
    /// 命令行开关总表。命中则执行对应工具并返回 <c>true</c>（调用方随即结束进程）；
    /// 没命中返回 <c>false</c>，由 <see cref="Program.Main"/> 走正常启动流程。
    ///
    /// 这里也装了崩溃防护：不少自检会真的接管桌面（隐藏原生图标），
    /// 万一工具自身抛异常，同样需要保证桌面被还原 —— 否则「跑个诊断把桌面搞没了」。
    /// </summary>
    /// <summary><see cref="Native.AttachConsole"/> 的「附到父进程控制台」常量</summary>
    private const int ATTACH_PARENT_PROCESS = -1;

    public static bool TryRun(string[] args)
    {
        if (args.Length == 0) return false;

        CrashGuard.Init();

        switch (args[0])
        {
            // ── 环境 / 桌面探查 ──
            case "--probe" or "-p": RunProbe(A(args, 1)); return true;
            case "--conflict": RunConflictCheck(); return true;
            case "--scan": RunScan(); return true;
            case "--layout": RunLayout(); return true;

            // ── 桌面接管 ──
            case "--restore": RunRestore(); return true;
            case "--hideshot": RunHideShot(A(args, 1) ?? "hideshot.png"); return true;
            case "--hidetest": RunHideTest(A(args, 1)); return true;
            case "--fixdesktop": RunFixDesktop(); return true;
            case "--listwnd": RunListWnd(A(args, 1)); return true;
            case "--pointtest": RunPointTest(A(args, 1)); return true;
            case "--overlaytest": RunOverlayTest(A(args, 1) ?? "ovl"); return true;

            // ── 图标 ──
            case "--icontest": RunIconTest(A(args, 1) ?? "icontest.png"); return true;
            case "--iconprobe": RunIconProbe(A(args, 1)); return true;
            case "--virtest": RunVirTest(A(args, 1), Clamp(Int(args, 2, 96), 16, 256)); return true;

            // ── 样式 / 透明度 ──
            case "--uishot":
                RunUiShot(A(args, 1) ?? "uishot.png", Int(args, 2, 40), Int(args, 3, 92),
                          args.Length > 4 && args[4] == "light");
                return true;
            case "--glasstest": RunGlassTest(A(args, 1)); return true;
            case "--glassfull": RunGlassFull(A(args, 1)); return true;
            case "--alphaprobe": RunAlphaProbe(A(args, 1)); return true;
            case "--resizetest": RunResizeTest(); return true;

            // ── 内存 ──
            case "--mem": RunMemProbe(Int(args, 1, 20), A(args, 2)); return true;
            case "--memstep": RunMemStep(A(args, 1)); return true;
            case "--memdiff": RunMemDiff(A(args, 1)); return true;

            // ── UI 自检 ──
            case "--uiprobe": RunUiProbe(Int(args, 1, 8)); return true;
            case "--demo": RunDemo(A(args, 1) ?? "demo.png", Int(args, 2, 8)); return true;
            case "--shot": RunShot(A(args, 1) ?? "shot.png"); return true;
            case "--settingsshot": RunSettingsShot(A(args, 1) ?? "settings.png", Int(args, 2, 0)); return true;
            case "--settingstest": RunSettingsTest(); return true;
            case "--gdiresize": RunGdiResize(Int(args, 1, 20000), A(args, 2) ?? "region"); return true;

            // ── 右键集成 ──
            case "--shelltest" or "--shellinstall" or "--shelluninstall" or "--shellverify":
                RunShellTest(args[0]); return true;

            // ── 集成入口 ──
            case "--cmd": RunShellCommand(args); return true;
            case "--startup": RunStartup(!string.Equals(A(args, 1), "off", StringComparison.OrdinalIgnoreCase)); return true;
            case "--quit": RunQuit(); return true;

            default: return false;
        }
    }

    /// <summary>取第 i 个参数（越界返回 null）</summary>
    private static string? A(string[] args, int i) => i < args.Length ? args[i] : null;

    /// <summary>取第 i 个整数参数，解析失败时用 <paramref name="def"/></summary>
    private static int Int(string[] args, int i, int def)
        => i < args.Length && int.TryParse(args[i], out var v) ? v : def;

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    // ═══════════════ 原先内联在 Main 里的几个小工具 ═══════════════

    /// <summary>
    /// 扫描自检（<c>--scan</c>）：只跑 <see cref="DesktopScanner"/>，不建窗口、不接管桌面。
    /// 用来在「格子全空」这类问题时先分清是扫描层还是 UI 层的锅。
    /// </summary>
    private static void RunScan()
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;

        var list = DesktopScanner.Scan();
        Console.WriteLine($"扫描到 {list.Count} 项");
        foreach (var it in list.Take(8))
            Console.WriteLine($"  [{(int)it.Type}] {it.Name}  →  box {it.BoxId}  {it.Path}");

        var host = new DesktopHost();
        host.Locate();
        var withVir = new List<DiscoveredItem>(list);
        int vir = DesktopScanner.AppendVirtualItems(withVir, host);
        Console.WriteLine($"补入虚拟项 {vir} 个（宿主定位 {(host.Located ? "成功" : "失败")}）");
        foreach (var it in withVir.Where(i => i.IsVirtual))
            Console.WriteLine($"  [虚拟] {it.Name}  {it.Path}");

        var grouped = DesktopScanner.GroupByBox(withVir);
        foreach (var g in grouped.OrderBy(g => g.Key))
            Console.WriteLine($"  格子 {g.Key,-4} → {g.Value.Count} 项");
    }

    /// <summary>
    /// 桌面救援（<c>--restore</c>）：把被隐藏的原生桌面图标重新显示出来。
    ///
    /// 进程被强杀 / 崩溃后，正常退出流程来不及还原，用这条命令兜底。
    /// 它不依赖任何进程内状态，因此在新进程里也能正确工作 ——
    /// 这正是「崩溃后桌面图标消失」最直接的人工解法。
    /// </summary>
    private static void RunRestore()
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;

        // 主程序若正在运行，它的保活巡检会在几秒内把图标重新藏回去 ——
        // 那种情况下「图标又不见了」不是还原失败，而是接管模式仍在生效。
        // 如实说清楚，免得用户以为命令没起作用。
        bool running = IsMainInstanceRunning();

        var h = new DesktopHost();
        if (h.Locate())
        {
            h.ShowNativeIcons();                 // 内部会一并清掉崩溃标记
            Console.WriteLine("已显示原生桌面图标（ListView 已 ShowWindow）。");
        }
        else
        {
            // 退到「不依赖 DesktopHost」的最小路径再试一次
            Console.WriteLine(CrashGuard.EmergencyRestoreDesktop()
                ? "已显示原生桌面图标（走兜底路径）。"
                : "未定位到桌面窗口链，无法还原。");
            CrashGuard.MarkDesktopShown();
        }

        if (running)
            Console.WriteLine("注意：桌面整理 · 精简版 正在运行，它会在几秒后重新接管桌面（图标再次隐藏）。\n" +
                              "       若要长期保持原生图标，请先从托盘菜单退出本程序。");
    }

    /// <summary>
    /// 主实例（常驻的那个）是否正在运行。
    /// 借全局互斥体判断 —— 主流程是 <c>new Mutex(true, ...)</c> 初始持有，
    /// 所以这里 <c>WaitOne(0)</c> 拿不到就说明它活着；拿得到说明只有我们（用完立即归还）。
    /// </summary>
    private static bool IsMainInstanceRunning()
    {
        try
        {
            using var m = new Mutex(false, Program.MutexName);
            bool got = false;
            try { got = m.WaitOne(0); } catch { }
            if (got) { try { m.ReleaseMutex(); } catch { } return false; }
            return true;
        }
        catch { return false; }
    }


    /// <summary>
    /// 极简实验（<c>--hideshot</c>）：隐藏 ListView → 截图 → 恢复。
    /// 用于判断隐藏是否真的改变了屏幕画面。
    /// </summary>
    private static void RunHideShot(string p)
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);

        var host = new DesktopHost();
        host.Locate();
        Console.WriteLine($"隐藏前 ListView visible = {Native.IsWindowVisible(host.ListView)}");

        host.HideNativeIcons(force: true);
        Thread.Sleep(1500);
        Console.WriteLine($"隐藏后 ListView visible = {Native.IsWindowVisible(host.ListView)}");

        RunShot(p);

        host.ShowNativeIcons();
        Thread.Sleep(500);
        Console.WriteLine($"恢复后 ListView visible = {Native.IsWindowVisible(host.ListView)}");
    }

    /// <summary>
    /// 布局诊断（<c>--layout</c>）：打印格子模型坐标 vs 窗口实际屏幕坐标（排查 DPI 缩放）。
    /// 注意用 <c>autoTakeover: false</c> —— 只量布局，不动用户桌面。
    /// </summary>
    private static void RunLayout()
    {
        Native.AttachConsole(ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        Program.Silent = true;
        ApplicationConfiguration.Initialize();

        var lm = new UI.MainContext(autoTakeover: false, readOnlyData: true);
        lm.DumpLayout();
        lm.Shutdown();
    }
}

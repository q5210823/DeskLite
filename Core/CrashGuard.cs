using System.Text;
using DeskLite.Win32;

namespace DeskLite.Core;

/// <summary>
/// 崩溃防护 + 桌面状态守护。
///
/// ── 要解决的问题 ──
/// 接管桌面时本程序会隐藏 explorer 的原生图标层（<c>ShowWindow(ListView, SW_HIDE)</c>），
/// 而「把它显示回来」这件事只写在正常退出流程（<c>MainContext.ExitApp</c>）里。
/// 一旦进程崩溃、被强杀、或异常退出，那段代码根本没机会跑 ——
/// explorer 不会自己把 ListView 恢复，于是用户看到的就是「桌面图标全消失」。
///
/// ── 三层防护（互为兜底，任一层生效即可避免图标丢失）──
///   ① <b>全局异常兜底</b>
///      · UI 线程异常 → 记日志后**继续运行**（托盘程序不该被一次 UI 异常带走）；
///      · 后台线程 / 终结器线程异常 → 进程一定会结束，但结束前先把桌面还原。
///   ② <b>落盘标记自愈</b>
///      隐藏桌面时写 <c>desktop_hidden.lock</c>，正常恢复时删掉它。
///      下次启动若发现标记还在，说明上次没走完清理流程 → 启动第一件事就是无条件还原。
///   ③ <b>兜底命令</b>
///      <c>DeskLite.exe --restore</c> 不依赖任何进程内状态，随时可手动救回桌面。
///
/// 说明：所有方法都必须能在「进程正在死」的糟糕时机被调用，因此一律不抛异常、
/// 不依赖 MainContext / 窗体等可能已经释放的对象，只做最必要的文件与 Win32 调用。
/// </summary>
internal static class CrashGuard
{
    private const string MarkerName = "desktop_hidden.lock";
    private const long MaxLogBytes = 512 * 1024;      // 单个日志文件封顶，避免无限增长

    /// <summary>数据目录（与 DataStore 的 <c>%APPDATA%\DeskLite</c> 一致）</summary>
    public static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskLite");

    private static string MarkerPath => Path.Combine(StateDir, MarkerName);
    private static string LogDir => Path.Combine(StateDir, "logs");

    /// <summary>
    /// 本次启动是否检测到「上次异常退出」，并因此自动还原了桌面。
    /// 供托盘气泡提示使用。
    /// </summary>
    public static bool RecoveredFromCrash { get; private set; }

    /// <summary>当前是否处于「已隐藏原生桌面图标」的状态（仅本进程视角）</summary>
    public static bool DesktopMarkedHidden => File.Exists(MarkerPath);

    // ───────── ① 全局异常兜底 ─────────

    /// <summary>安装全局异常处理器。必须在创建任何窗口之前调用。</summary>
    public static void Init()
    {
        // UI 线程：改成「交给我们处理」，而不是默认的「弹框后结束进程」。
        // WinForms 默认行为会把一次普通异常升级成一次闪退，对托盘常驻程序代价太大。
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => OnUiThreadException(e.Exception);
        }
        catch { /* 极端情况下设置失败也不能影响启动 */ }

        // 非 UI 线程（线程池 / FileSystemWatcher / 终结器 / 定时器回调）：
        // 这里的异常**无法阻止**进程结束，但可以在结束前把桌面救回来。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            OnFatal(e.ExceptionObject as Exception, e.IsTerminating);

        // 未观察的 Task 异常：记一笔就好，不要让它升级成进程级错误。
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };
    }

    /// <summary>
    /// UI 线程未捕获异常：记日志后**继续运行**。
    /// 本程序是托盘常驻工具，一次 UI 异常就把进程带走（默认行为）得不偿失；
    /// 若是渲染路径出错，下一次 WM_PAINT 会重画，通常能自愈。
    /// </summary>
    private static void OnUiThreadException(Exception? ex)
    {
        Log(ex, "Application.ThreadException（UI 线程）");
        // 不退出：本程序只是托盘工具，UI 异常后各功能仍可能正常。
        // 若异常发生在渲染路径，下一个 WM_PAINT 会重新走一遍，通常能自愈。
    }

    /// <summary>后台线程未捕获异常：进程即将结束，先把桌面原生图标还回去。</summary>
    private static void OnFatal(Exception? ex, bool terminating)
    {
        Log(ex, terminating ? "AppDomain.UnhandledException（进程即将结束）" : "AppDomain.UnhandledException");

        if (!DesktopMarkedHidden) return;

        // 此时不能再相信任何托管状态，直接走「按窗口类名找 ListView」的最小路径
        if (EmergencyRestoreDesktop())
            LogText("崩溃兜底：已在进程结束前还原桌面原生图标。");
    }

    // ───────── ② 落盘标记 ─────────

    /// <summary>记录「原生桌面图标已被本程序隐藏」——隐藏成功之后立刻调用</summary>
    public static void MarkDesktopHidden()
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(MarkerPath,
                $"{Environment.ProcessId}{Environment.NewLine}{DateTime.Now:O}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch { /* 标记写不进去只是失去一层防护，不影响功能 */ }
    }

    /// <summary>清除标记 —— 原生图标已恢复（或从未隐藏）时调用</summary>
    public static void MarkDesktopShown()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { }
    }

    /// <summary>
    /// 启动自愈：若发现上次留下的「已隐藏」标记，说明上次没有正常退出
    /// （崩溃 / 强杀 / 断电），桌面很可能还停在「没有原生图标」的状态。
    /// 这里无条件还原一次，然后清掉标记。
    /// </summary>
    public static void RecoverIfNeeded()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return;

            RecoveredFromCrash = true;
            bool ok = EmergencyRestoreDesktop();
            MarkDesktopShown();

            LogText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 检测到上次未正常退出" +
                    $"（desktop_hidden.lock 残留），已自动还原桌面原生图标：{(ok ? "成功" : "未定位到桌面窗口")}");
        }
        catch { }
    }

    // ───────── ③ 兜底还原（不依赖任何进程内对象）─────────

    /// <summary>
    /// 只做一件事：把桌面 <c>SysListView32</c> 显示回来。
    /// 全程按窗口类名定位，不引用 DesktopHost / MainContext，因此可以被
    /// 异常处理器、独立进程（<c>--restore</c>）、启动自愈三处共用。
    /// </summary>
    public static bool EmergencyRestoreDesktop()
    {
        try
        {
            var lv = FindDesktopListView();
            if (lv == IntPtr.Zero) return false;

            Native.ShowWindow(lv, Native.SW_SHOW);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 定位桌面图标承载窗口：Progman → SHELLDLL_DefView → SysListView32。
    /// DefView 挂在 WorkerW 下时走回退枚举（部分系统配置如此）。
    /// </summary>
    public static IntPtr FindDesktopListView()
    {
        IntPtr progman = Native.FindWindow("Progman", null);
        IntPtr defView = progman != IntPtr.Zero
            ? Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null)
            : IntPtr.Zero;

        if (defView == IntPtr.Zero)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((h, _) =>
            {
                var sb = new StringBuilder(64);
                Native.GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() != "WorkerW") return true;

                var dv = Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv == IntPtr.Zero) return true;
                found = dv;
                return false;
            }, IntPtr.Zero);
            defView = found;
        }

        if (defView == IntPtr.Zero) return IntPtr.Zero;

        var lv = Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
        if (lv == IntPtr.Zero) lv = Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        return lv;
    }

    // ───────── 日志 ─────────

    /// <summary>记一条异常（含堆栈）到 <c>%APPDATA%\DeskLite\logs</c>；永不抛出</summary>
    public static void Log(Exception? ex, string where)
    {
        if (ex == null) return;
        LogText($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}{Environment.NewLine}{ex}{Environment.NewLine}");
    }

    /// <summary>记一行文本；永不抛出</summary>
    public static void LogText(string text)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"desklite-{DateTime.Now:yyyyMMdd}.log");

            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxLogBytes) return;

            File.AppendAllText(path, text + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>日志目录（供「打开数据目录」等入口展示）</summary>
    public static string LogPath => LogDir;
}

using Microsoft.Win32;

namespace DeskLite.Core;

/// <summary>
/// 开机自启（对应 DeskGo 的 <c>CFG_DESKTOPMGR_AUTORUN</c> 与计划任务 <c>DeskGo</c>）。
///
/// 同时维护**两个**位置，互为保险：
///   · <c>HKCU\...\Run</c> 键 —— 首选，用户级权限即可，卸载删键完全回滚；
///   · 「启动」文件夹里的快捷方式 —— 部分安全软件会静默清理 Run 键
///     （本机实测出现过写入后条目消失），这个位置通常不被拦。
///
/// 检测时任一位置存在即算「已登记」，取消时两处一起清 —— 这样即使用户或
/// 安全软件动过其中一个，设置界面的勾选状态也不会来回跳。
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskLite";

    /// <summary>「启动」文件夹里的快捷方式路径</summary>
    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DeskLite.lnk");

    /// <summary>Run 键里是否已登记</summary>
    public static bool InRegistry
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(ValueName) is string s && s.Length > 0;
            }
            catch { return false; }
        }
    }

    /// <summary>启动文件夹里是否已有快捷方式</summary>
    public static bool InStartupFolder
    {
        get { try { return File.Exists(ShortcutPath); } catch { return false; } }
    }

    /// <summary>两个位置任一存在即视为已启用了开机自启</summary>
    public static bool IsEnabled => InRegistry || InStartupFolder;

    /// <summary>登记 / 取消开机自启（Run 键为主，启动文件夹快捷方式为辅）</summary>
    public static bool Apply(bool enable, string? exePath = null)
    {
        exePath ??= ResolveExePath();
        if (string.IsNullOrEmpty(exePath)) return false;

        bool a = ApplyRegistry(enable, exePath);
        bool b = ApplyShortcut(enable, exePath);
        return a || b;
    }

    /// <summary>
    /// 解析「应该被登记为自启目标」的可执行文件。
    ///
    /// 用 <c>dotnet DeskLite.dll</c> 启动时 <see cref="Environment.ProcessPath"/> 是
    /// <c>dotnet.exe</c>，直接登记会变成「开机启动一个空的 dotnet」—— 必须换成同目录的
    /// apphost（DeskLite.exe）。
    /// </summary>
    private static string? ResolveExePath()
    {
        var p = Environment.ProcessPath;
        if (string.IsNullOrEmpty(p)) return null;

        var name = Path.GetFileNameWithoutExtension(p);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "DeskLite.exe");
            if (File.Exists(exe)) return exe;
        }
        return p;
    }

    /// <summary>诊断用：把实际会登记的目标 exe 亮出来</summary>
    internal static string? TargetExeForDiagnostics => ResolveExePath();

    private static bool ApplyRegistry(bool enable, string exePath)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (k == null) return false;

            if (enable) k.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 启动文件夹快捷方式。删除很简单，创建走 <c>WScript.Shell</c>。
    ///
    /// 为什么不用 COM interop 手写 <c>IShellLink</c>：那需要几十行必须逐字正确的
    /// 接口定义，一旦有偏差就是运行时崩溃；而这里只在用户勾选设置时执行一次，
    /// 起一个 PowerShell 子进程的代价完全可以接受，且失败也不影响 Run 键那条路。
    /// </summary>
    private static bool ApplyShortcut(bool enable, string exePath)
    {
        try
        {
            if (!enable)
            {
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
                return true;
            }

            if (File.Exists(ShortcutPath)) return true;   // 已有就不重复创建

            string dir = Path.GetDirectoryName(exePath) ?? "";
            // 单引号在 PowerShell 里原样传递内容，转义内部单引号即可
            static string Q(string s) => "'" + s.Replace("'", "''") + "'";
            string script =
                $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut({Q(ShortcutPath)});" +
                $"$s.TargetPath = {Q(exePath)};" +
                $"$s.WorkingDirectory = {Q(dir)};" +
                "$s.Description = 'DeskLite';" +
                "$s.Save()";

            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(10000);
            return File.Exists(ShortcutPath);
        }
        catch { return false; }
    }
}

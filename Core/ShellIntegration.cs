using Microsoft.Win32;

namespace DeskLite.Core;

/// <summary>右键菜单的作用范围</summary>
public enum MenuScope
{
    /// <summary>桌面空白处</summary>
    Desktop,
    /// <summary>文件（任意文件类型）</summary>
    File,
    /// <summary>文件夹</summary>
    Folder,
    /// <summary>文件夹空白处（资源管理器内）</summary>
    FolderBackground,
}

/// <summary>
/// Shell 右键集成（对应 DeskGo 的 <c>DeskMgrShellExtx64.dll</c> + <c>Win11ContextMenu.exe</c>）。
///
/// DeskGo 走的是 **注入式 Shell 扩展 DLL**：需要编译 COM 组件 + 管理员注册 + 加载进资源管理器进程，
/// 体积与风险都大。精简版改用 **HKCU 注册表静态谓词（Verb）**：
///   · 无需管理员权限（只写当前用户）
///   · 无需编译 DLL、不注入 explorer，零崩溃风险
///   · 卸载即删键，可完全回滚
/// 代价：Win11 上出现在「显示更多选项」（Shift+F10）里；如需回到经典菜单见 <see cref="ApplyMenuStyle"/>。
///
/// 注册结构（以桌面背景为例）：
/// <code>
/// HKCU\Software\Classes\DesktopBackground\Shell\DeskLite
///     MUIVerb      = 桌面整理 · 精简版
///     Icon         = &lt;exe&gt;,0
///     SubCommands  = (空串 → 使用 shell 子键下的级联项)
///     \shell\sort\command   (默认) = "&lt;exe&gt;" --cmd sort
///     \shell\search\command (默认) = "&lt;exe&gt;" --cmd search
/// </code>
/// </summary>
public static class ShellIntegration
{
    private const string ClassesRoot = @"Software\Classes";
    private const string Verb = "DeskLite";
    public const string Title = "桌面整理 · 精简版";

    /// <summary>Win11 新式菜单开关的 CLSID：删掉它即恢复 Win10 经典右键菜单</summary>
    private const string Win11Clsid = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    // ───────── 各范围的注册表路径 ─────────

    private static string Root(MenuScope s) => s switch
    {
        MenuScope.Desktop          => $@"{ClassesRoot}\DesktopBackground\Shell\{Verb}",
        MenuScope.File             => $@"{ClassesRoot}\*\shell\{Verb}",
        MenuScope.Folder           => $@"{ClassesRoot}\Directory\shell\{Verb}",
        MenuScope.FolderBackground => $@"{ClassesRoot}\Directory\Background\shell\{Verb}",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    // ───────── 状态查询 ─────────

    public static bool IsInstalled(MenuScope s) => KeyExists(Root(s));

    public static bool DesktopInstalled => IsInstalled(MenuScope.Desktop);
    public static bool FileInstalled => IsInstalled(MenuScope.File) || IsInstalled(MenuScope.Folder);
    public static bool AnyInstalled => DesktopInstalled || FileInstalled;

    /// <summary>当前是否为 Win10 经典右键菜单（存在该 CLSID 键即经典）</summary>
    public static bool IsClassicMenu => KeyExists(Win11Clsid);

    /// <summary>当前 exe 的完整路径（供注册表命令使用）</summary>
    public static string ExePath => Environment.ProcessPath ?? "";

    // ───────── 安装 / 卸载 ─────────

    /// <summary>
    /// 按设置写入注册表。幂等：先整体清空本程序写入的键，再按需重建。
    /// </summary>
    public static void Install(AppSettings s, string? exePath = null)
    {
        exePath ??= ExePath;
        if (string.IsNullOrEmpty(exePath)) return;

        Uninstall();

        if (s.MenuDesktop) BuildVerb(MenuScope.Desktop, exePath, includeSendTo: false);
        if (s.MenuFile)
        {
            BuildVerb(MenuScope.File, exePath, includeSendTo: true);
            BuildVerb(MenuScope.Folder, exePath, includeSendTo: true);
        }

        ApplyMenuStyle(s);
    }

    /// <summary>移除本程序写入的全部注册表项（完全回滚）</summary>
    public static void Uninstall()
    {
        foreach (MenuScope s in Enum.GetValues<MenuScope>())
            DeleteKey(Root(s));
    }

    /// <summary>
    /// 菜单风格开关（对应 DeskGo 的「win11菜单配置」与「净化右键菜单配置」）。
    ///   classic = true（净化）→ 写 CLSID 键，回到 Win10 经典菜单（需重启 explorer 生效）
    ///   classic = false      → 删 CLSID 键，使用 Win11 新式菜单
    /// </summary>
    public static void ApplyMenuStyle(AppSettings s) => ApplyMenuStyle(classic: s.CleanMenu && !s.Win11Menu);

    public static void ApplyMenuStyle(bool classic)
    {
        try
        {
            if (classic)
            {
                using var k = Registry.CurrentUser.CreateSubKey($@"{Win11Clsid}\InprocServer32");
                k?.SetValue("", "", RegistryValueKind.String);
            }
            else
            {
                DeleteKey(Win11Clsid);
            }
        }
        catch { /* 受策略限制时忽略，不影响主体功能 */ }
    }

    /// <summary>重启资源管理器使菜单风格生效（会短暂黑屏/闪一下任务栏）</summary>
    public static bool RestartExplorer()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); } catch { }
            }
            Thread.Sleep(900);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "..", "explorer.exe"))
                { UseShellExecute = true });
            }
            catch
            {
                try { System.Diagnostics.Process.Start("explorer.exe"); } catch { return false; }
            }
            return true;
        }
        catch { return false; }
    }

    // ───────── 内部构建 ─────────

    private static void BuildVerb(MenuScope scope, string exe, bool includeSendTo)
    {
        try
        {
            string root = Root(scope);
            using var k = Registry.CurrentUser.CreateSubKey(root, writable: true);
            if (k == null) return;

            k.SetValue("", Title, RegistryValueKind.String);          // 默认值
            k.SetValue("MUIVerb", Title, RegistryValueKind.String);
            k.SetValue("Icon", $"\"{exe}\",0", RegistryValueKind.String);
            k.SetValue("Position", "Bottom", RegistryValueKind.String);
            // 空串 → 使用 shell 子键下的级联项（Win10/11 经典菜单均支持）
            k.SetValue("SubCommands", "", RegistryValueKind.String);

            WriteChild(root, "sort", "一键整理桌面", $"\"{exe}\" --cmd sort");
            WriteChild(root, "search", "桌面搜索", $"\"{exe}\" --cmd search");

            if (includeSendTo)
                WriteChild(root, "sendto", "送入桌面格子", $"\"{exe}\" --cmd sendto \"%1\"");
        }
        catch { /* 策略受限时静默失败 */ }
    }

    private static void WriteChild(string parentRoot, string id, string label, string command)
    {
        using var ck = Registry.CurrentUser.CreateSubKey($@"{parentRoot}\shell\{id}", writable: true);
        if (ck == null) return;
        ck.SetValue("", label, RegistryValueKind.String);
        ck.SetValue("MUIVerb", label, RegistryValueKind.String);
        ck.SetValue("Icon", "", RegistryValueKind.String);

        using var cmd = Registry.CurrentUser.CreateSubKey($@"{parentRoot}\shell\{id}\command", writable: true);
        cmd?.SetValue("", command, RegistryValueKind.String);
    }

    // ───────── 注册表小工具 ─────────

    private static bool KeyExists(string path)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(path);
            return k != null;
        }
        catch { return false; }
    }

    private static void DeleteKey(string path)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
        catch { }
    }

    // ───────── 自检文本（供 --shelltest 诊断）─────────

    public static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"exe            = {ExePath}");
        sb.AppendLine($"桌面背景菜单   = {(DesktopInstalled ? "已注册" : "未注册")}");
        sb.AppendLine($"文件/文件夹菜单= {(FileInstalled ? "已注册" : "未注册")}");
        sb.AppendLine($"菜单风格       = {(IsClassicMenu ? "Win10 经典（净化模式）" : "Win11 新式")}");

        foreach (MenuScope s in new[] { MenuScope.Desktop, MenuScope.File, MenuScope.Folder })
        {
            sb.AppendLine();
            sb.AppendLine($"[{s}] {(IsInstalled(s) ? "已注册" : "未注册")}  {Root(s)}");
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Root(s));
                if (k == null) continue;
                foreach (var name in k.GetValueNames())
                    sb.AppendLine($"    {name} = {k.GetValue(name)}");
                using var sh = Registry.CurrentUser.OpenSubKey($@"{Root(s)}\shell");
                if (sh != null)
                    foreach (var sub in sh.GetSubKeyNames())
                    {
                        using var c = Registry.CurrentUser.OpenSubKey($@"{Root(s)}\shell\{sub}\command");
                        sb.AppendLine($"    · {sub} → {c?.GetValue("")}");
                    }
            }
            catch { }
        }
        return sb.ToString();
    }
}

using System.Text;

namespace DeskLite.Core;

/// <summary>
/// 应用设置（对应 DeskGo 的 <c>DesktopSettings.exe</c> 三个设置页）。
///
/// 键名与 DeskGo 的 <c>%APPDATA%\Tencent\DeskGo\ConFile.dat</c> 中的 <c>CFG_*</c> 键对齐，
/// 但只保留精简版需要的项：基础页 + 桌面页 + 右键集成。
/// 壁纸页 / 云与 AI 相关键（CFG_WALLPAPER_*、CFG_DOCCLOUD_*、CFG_CLOUD_NOTE_* 等）已全部去掉。
///
/// 存储：<c>%APPDATA%\DeskLite\ConFile.dat</c>，纯文本 <c>key=value</c>。
/// 说明：DeskGo 原文件是加密的 OLE 复合文档（键名明文、值加密），无密钥无法读写，
///       因此本精简版用**同结构明文**，键名完全对齐，便于对照与手工调试。
/// </summary>
public sealed class AppSettings
{
    // ───────── 基础页（对应 DeskGo: ReportSettingBasicPage）─────────

    /// <summary>CFG_SYS_THEME —— 主题：Dark / Light</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>CFG_SYS_GLASS —— 毛玻璃效果配置（格子不透明度，0–100，100 = 不透明）</summary>
    public int GlassOpacity { get; set; } = 92;

    /// <summary>CFG_ICON_SIZE —— 格子内图标尺寸（像素）。
    /// 默认 40 是按腾讯桌面管家的实际观感实测校准的（图标约 40px / 标签约 12px / 行距约 80px）。</summary>
    public int IconSize { get; set; } = 40;

    /// <summary>CFG_DESKTOPMGR_AUTORUN —— 随 Windows 登录自启</summary>
    public bool AutoRun { get; set; } = false;

    // ───────── 桌面页（对应 DeskGo: ReportSettingDeskPage）─────────

    /// <summary>CFG_ENABLED_HIDE_ALL_ICONS —— 启动后接管桌面（隐藏原生图标）</summary>
    public bool TakeoverOnStart { get; set; } = true;

    /// <summary>CFG_HIDE_ALL_FILESANDBOXES —— 把格子嵌入桌面层（SetParent 到 Progman）</summary>
    public bool EmbedOnStart { get; set; } = false;

    /// <summary>格子自动收展配置 —— 启动时恢复上次折叠状态</summary>
    public bool RememberCollapse { get; set; } = true;

    /// <summary>桌面搜索快捷键配置 —— 形如 "Ctrl+Alt+S"，留空表示不注册</summary>
    public string SearchHotkey { get; set; } = "Ctrl+Alt+S";

    /// <summary>双击打开项（关闭则单击打开）</summary>
    public bool DoubleClickOpen { get; set; } = true;

    // ───────── 右键集成（对应 DeskGo 第五节 + "win11菜单配置"/"净化右键菜单配置"）─────────

    /// <summary>桌面空白处右键菜单（级联子菜单）</summary>
    public bool MenuDesktop { get; set; } = false;

    /// <summary>文件 / 文件夹右键菜单（级联子菜单）</summary>
    public bool MenuFile { get; set; } = false;

    /// <summary>win11菜单配置 —— 优先走 Win11 新式菜单（需系统支持，失败自动回退传统菜单）</summary>
    public bool Win11Menu { get; set; } = false;

    /// <summary>净化右键菜单配置 —— 注册时同时屏蔽同名第三方项，避免重复</summary>
    public bool CleanMenu { get; set; } = false;

    // ───────── 派生属性 ─────────

    /// <summary>格子窗口不透明度（WinForms Opacity 需要 0.0–1.0）</summary>
    public double Opacity => Math.Clamp(GlassOpacity, 30, 100) / 100.0;

    public bool IsLight => string.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase);

    public void ToggleTheme() => Theme = IsLight ? "Dark" : "Light";

    // ───────── 持久化 ─────────

    public const string FileName = "ConFile.dat";

    /// <summary>
    /// 设置文件路径。环境变量 <c>DESKLITE_CONFILE</c> 可覆盖（用于演示 / 回归测试，
    /// 这样试验配置不会污染用户真实设置）。
    /// </summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("DESKLITE_CONFILE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeskLite", FileName);

    /// <summary>读取设置；不存在或损坏时返回默认值</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        var s = new AppSettings();
        try
        {
            if (!File.Exists(path)) return s;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';' or '[') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();
                s.Apply(key, val);
            }
        }
        catch { /* 读取失败时使用默认值，不影响启动 */ }
        return s;
    }

    private void Apply(string key, string val)
    {
        bool Bool() => val is "1" or "true" or "True";
        int Int(int fallback) => int.TryParse(val, out var n) ? n : fallback;

        switch (key)
        {
            case "CFG_SYS_THEME":              Theme = val.Length > 0 ? val : Theme; break;
            case "CFG_SYS_GLASS":              GlassOpacity = Int(GlassOpacity); break;
            case "CFG_ICON_SIZE":              IconSize = Int(IconSize); break;
            case "CFG_DESKTOPMGR_AUTORUN":     AutoRun = Bool(); break;

            case "CFG_ENABLED_HIDE_ALL_ICONS": TakeoverOnStart = Bool(); break;
            case "CFG_HIDE_ALL_FILESANDBOXES": EmbedOnStart = Bool(); break;
            case "CFG_FENCES_AUTO_COLLAPSE":   RememberCollapse = Bool(); break;
            case "CFG_SEARCH_HOTKEY":          SearchHotkey = val; break;
            case "CFG_FENCES_DBLCLICK_OPEN":   DoubleClickOpen = Bool(); break;

            case "CFG_RMENU_DESKTOP":          MenuDesktop = Bool(); break;
            case "CFG_RMENU_FILE":             MenuFile = Bool(); break;
            case "CFG_RMENU_WIN11":            Win11Menu = Bool(); break;
            case "CFG_RMENU_CLEAN":            CleanMenu = Bool(); break;
        }
    }

    /// <summary>保存设置（原子写入：先写 .tmp 再改名）</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch { /* 保存失败不影响运行 */ }
    }

    /// <summary>序列化为 key=value 文本（键名顺序与 DeskGo 上报顺序一致）</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DeskLite 设置 — 键名对齐 DeskGo ConFile.dat 的 CFG_* 键");
        sb.AppendLine("# 精简版已移除壁纸 / 云与 AI / 内嵌内容相关配置");
        sb.AppendLine();

        sb.AppendLine("[基础]");
        sb.AppendLine($"CFG_SYS_THEME={Theme}");
        sb.AppendLine($"CFG_SYS_GLASS={GlassOpacity}");
        sb.AppendLine($"CFG_ICON_SIZE={IconSize}");
        sb.AppendLine($"CFG_DESKTOPMGR_AUTORUN={(AutoRun ? 1 : 0)}");

        sb.AppendLine();
        sb.AppendLine("[桌面]");
        sb.AppendLine($"CFG_ENABLED_HIDE_ALL_ICONS={(TakeoverOnStart ? 1 : 0)}");
        sb.AppendLine($"CFG_HIDE_ALL_FILESANDBOXES={(EmbedOnStart ? 1 : 0)}");
        sb.AppendLine($"CFG_FENCES_AUTO_COLLAPSE={(RememberCollapse ? 1 : 0)}");
        sb.AppendLine($"CFG_SEARCH_HOTKEY={SearchHotkey}");
        sb.AppendLine($"CFG_FENCES_DBLCLICK_OPEN={(DoubleClickOpen ? 1 : 0)}");

        sb.AppendLine();
        sb.AppendLine("[右键集成]");
        sb.AppendLine($"CFG_RMENU_DESKTOP={(MenuDesktop ? 1 : 0)}");
        sb.AppendLine($"CFG_RMENU_FILE={(MenuFile ? 1 : 0)}");
        sb.AppendLine($"CFG_RMENU_WIN11={(Win11Menu ? 1 : 0)}");
        sb.AppendLine($"CFG_RMENU_CLEAN={(CleanMenu ? 1 : 0)}");

        return sb.ToString();
    }

    public AppSettings Clone()
    {
        var c = new AppSettings();
        c.ApplyAll(this);
        return c;
    }

    private void ApplyAll(AppSettings o)
    {
        Theme = o.Theme;
        GlassOpacity = o.GlassOpacity;
        IconSize = o.IconSize;
        AutoRun = o.AutoRun;
        TakeoverOnStart = o.TakeoverOnStart;
        EmbedOnStart = o.EmbedOnStart;
        RememberCollapse = o.RememberCollapse;
        SearchHotkey = o.SearchHotkey;
        DoubleClickOpen = o.DoubleClickOpen;
        MenuDesktop = o.MenuDesktop;
        MenuFile = o.MenuFile;
        Win11Menu = o.Win11Menu;
        CleanMenu = o.CleanMenu;
    }
}

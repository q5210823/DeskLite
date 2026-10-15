namespace DeskLite.Core;

/// <summary>
/// 规则引擎 —— 复刻 DeskGo 的类型判定与自动归类。
///
/// 归类映射（由 DeskGo 日志中 BoxID × 类型码 交叉表还原）：
///   类型码 1（文件夹）        → 我的文件夹 (101)
///   类型码 2（快捷方式）      → APP        (104)
///   类型码 3（特殊项）        → 系统格子   (1)
///   类型码 4 + 可执行(.exe等) → APP        (104)
///   类型码 4 + 文档/图片等    → 我的文件   (102)
/// </summary>
public static class Rules
{
    /// <summary>可执行文件扩展名 —— 类型码 4 中归入 APP 格的部分</summary>
    private static readonly HashSet<string> ExecutableExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".com", ".ps1", ".vbs", ".jar",
    };

    /// <summary>快捷方式扩展名 —— 类型码 2</summary>
    private static readonly HashSet<string> ShortcutExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url", ".pif",
    };

    /// <summary>特殊项扩展名 —— 类型码 3</summary>
    private static readonly HashSet<string> SpecialExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sys", ".drv", ".cpl", ".msc", ".scr", ".ocx", ".inf", ".dll",
    };

    /// <summary>
    /// 判定一个路径的类型码。判定顺序与 DeskGo 一致：虚拟项 → 目录 → 快捷方式 → 特殊 → 普通文件。
    /// </summary>
    public static ItemType Classify(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ItemType.Special;

        // 3 = 虚拟项（此电脑 / 回收站 / 网络）—— 伪路径 ::{CLSID}，必须先判，
        //     因为它既不是目录也不存在，落到后面会被当成「无扩展名的特殊项」而丢掉身份。
        if (VirtualItems.IsVirtual(path)) return ItemType.Special;

        // 1 = 文件夹
        try { if (Directory.Exists(path)) return ItemType.Folder; }
        catch { /* 路径异常 → 落到特殊项 */ }

        // 磁盘上真有人建了个叫「此电脑」的文件夹时，上面那条已经把它判成文件夹了；
        // 这里只覆盖「按名字看是虚拟项、但路径又不存在」的残留情况。
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (VirtualItems.ByName(name) != null) return ItemType.Special;

        var ext = Path.GetExtension(path);

        // 2 = 快捷方式
        if (ShortcutExts.Contains(ext)) return ItemType.Shortcut;

        // 3 = 特殊项
        if (SpecialExts.Contains(ext)) return ItemType.Special;
        if (ext.Length == 0) return ItemType.Special;   // 无扩展名又不是目录 → 特殊项

        // 4 = 普通文件
        return ItemType.File;
    }

    /// <summary>
    /// 已知条目是否为目录时的判定。
    /// 比 Classify(path) 更可靠：直接使用枚举时的文件属性，不再二次访问磁盘。
    /// </summary>
    public static ItemType Classify(string path, bool isDirectory)
        => isDirectory ? ItemType.Folder : Classify(path);

    /// <summary>类型码 4 中，是否属于可执行程序（用于区分落入 APP 还是我的文件）</summary>
    public static bool IsExecutable(string path)
        => ExecutableExts.Contains(Path.GetExtension(path));

    /// <summary>
    /// 按规则解析一个项应该归入哪个格子。
    /// </summary>
    public static int ResolveBoxId(string path, ItemType type)
    {
        return type switch
        {
            ItemType.Folder => BoxIds.MyFolders,
            ItemType.Shortcut => BoxIds.Apps,
            ItemType.Special => BoxIds.System,
            ItemType.File => IsExecutable(path) ? BoxIds.Apps : BoxIds.MyFiles,
            _ => BoxIds.MyFiles,
        };
    }

    /// <summary>便捷重载：先判定类型再解析格子</summary>
    public static (ItemType Type, int BoxId) Resolve(string path)
    {
        var t = Classify(path);
        return (t, ResolveBoxId(path, t));
    }

    /// <summary>
    /// 显示名 —— 与 DeskGo 一致：文件去掉扩展名，文件夹保留原名。
    /// </summary>
    public static string DisplayName(string path)
    {
        try
        {
            // 虚拟项伪路径没有文件名可言，直接取表里的规范名
            if (VirtualItems.IsVirtual(path)) return VirtualItems.FriendlyName(path);

            if (Directory.Exists(path))
                return Path.GetFileName(path.TrimEnd('\\', '/'));

            var file = Path.GetFileName(path);
            var ext = Path.GetExtension(file);
            return ext.Length > 0 ? file[..^ext.Length] : file;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>类型码的中文名（用于界面展示与图标分类）</summary>
    public static string TypeName(ItemType t) => t switch
    {
        ItemType.Folder => "文件夹",
        ItemType.Shortcut => "快捷方式",
        ItemType.Special => "特殊项",
        ItemType.File => "文件",
        _ => "未知",
    };
}

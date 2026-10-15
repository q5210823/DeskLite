namespace DeskLite.Core;

/// <summary>扫描到的桌面项</summary>
public sealed class DiscoveredItem
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public ItemType Type { get; set; }
    public int BoxId { get; set; }
    public bool IsShortcut => Type == ItemType.Shortcut && !IsVirtual;

    /// <summary>是否 Shell 命名空间虚拟项（此电脑 / 回收站 / 网络）</summary>
    public bool IsVirtual { get; set; }
}

/// <summary>
/// 桌面扫描器。
///
/// 设计说明：
///   接管模式下会隐藏 explorer 的原生图标层，格子的数据来源采用**两个真实桌面目录**：
///     · 用户桌面    %USERPROFILE%\Desktop
///     · 公共桌面    %PUBLIC%\Desktop
///   理由：原生 ListView 只能读到显示名、拿不到完整路径（且虚拟项无路径），
///        从目录枚举才能拿到可操作的真实路径，与 DeskGo 能"整理文件"的能力对齐。
/// </summary>
public static class DesktopScanner
{
    /// <summary>两个桌面目录（用户桌面 + 公共桌面）</summary>
    public static IEnumerable<string> DesktopDirectories()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrEmpty(user)) yield return user;
        if (!string.IsNullOrEmpty(common)) yield return common;
    }

    /// <summary>扫描并分类桌面上的全部项</summary>
    public static List<DiscoveredItem> Scan()
    {
        var list = new List<DiscoveredItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in DesktopDirectories().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;

            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
            catch { continue; }

            foreach (var e in entries)
            {
                try
                {
                    // 跳过隐藏项与系统文件
                    if ((e.Attributes & FileAttributes.Hidden) != 0) continue;
                    if (e.Attributes.HasFlag(FileAttributes.System)) continue;
                    if (e.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    if (!seen.Add(e.FullName)) continue;

                    // 直接用枚举到的属性判断目录，避免二次访问磁盘
                    var isDir = (e.Attributes & FileAttributes.Directory) != 0;
                    var type = Rules.Classify(e.FullName, isDir);
                    list.Add(new DiscoveredItem
                    {
                        Path = e.FullName,
                        Name = Rules.DisplayName(e.FullName),
                        Type = type,
                        BoxId = Rules.ResolveBoxId(e.FullName, type),
                    });
                }
                catch { /* 单个项异常不影响整体扫描 */ }
            }
        }

        return list;
    }

    /// <summary>
    /// 把原生桌面 ListView 里的**虚拟项**（此电脑 / 回收站 / 网络）补进扫描结果。
    ///
    /// 为什么必须单独走这一步：目录枚举天生看不到它们 —— 它们不是文件，
    /// 由 explorer 的命名空间扩展提供。实测原生 ListView 60 项、目录枚举 58 项，
    /// 差的正好是这两个（加上网络共 3 项）。
    ///
    /// 只补「当前确实显示在桌面上」的项：ListView 里没有就说明用户在
    /// 「桌面图标设置」里把它关掉了，此时不补，尊重用户选择。
    /// </summary>
    /// <returns>实际补入的项数</returns>
    public static int AppendVirtualItems(List<DiscoveredItem> list, DeskLite.Win32.DesktopHost host)
    {
        if (!host.Located) return 0;

        List<DeskLite.Win32.DesktopIcon> icons;
        // 归类只要显示名：不读坐标、不读图标索引 —— 每项少两个跨进程来回
        try { icons = host.EnumerateIcons(needLayout: false); }
        catch { return 0; }
        if (icons.Count == 0) return 0;

        // 磁盘上真实项的显示名：真有人建了个叫「此电脑」的文件夹时，以真实项为准，不重复补
        var realNames = new HashSet<string>(
            list.Where(i => !i.IsVirtual).Select(i => i.Name), StringComparer.OrdinalIgnoreCase);

        var seenPaths = new HashSet<string>(
            list.Where(i => i.IsVirtual).Select(i => i.Path), StringComparer.OrdinalIgnoreCase);

        int added = 0;
        foreach (var ic in icons)
        {
            var vi = VirtualItems.ByName(ic.Text);
            if (vi == null) continue;
            if (realNames.Contains(ic.Text)) continue;
            if (!seenPaths.Add(vi.Path)) continue;

            list.Add(new DiscoveredItem
            {
                Path = vi.Path,                  // ::{CLSID}
                Name = ic.Text,                  // 用系统实际显示名（可能是「计算机」而不是「此电脑」）
                Type = ItemType.Special,
                BoxId = BoxIds.System,
                IsVirtual = true,
                // 注意：**不要**把 ListView 的图标索引带过来。
                // 那是 ListView 私有 Imagelist 的序号，不是系统 Imagelist 的索引，
                // 拿去查 SHGetImageList 会得到完全无关的图标。取图标由 IconCache
                // 自己走「解析名 → PIDL」完成。
            });
            added++;
        }
        return added;
    }

    /// <summary>按格子分组</summary>
    public static Dictionary<int, List<DiscoveredItem>> GroupByBox(IEnumerable<DiscoveredItem> items)
        => items.GroupBy(i => i.BoxId).ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>统计信息（供设置页/状态显示）</summary>
    public static (int Total, int Folders, int Shortcuts, int Files, int Special) Summary(IEnumerable<DiscoveredItem> items)
    {
        int total = 0, f = 0, s = 0, file = 0, sp = 0;
        foreach (var i in items)
        {
            total++;
            switch (i.Type)
            {
                case ItemType.Folder: f++; break;
                case ItemType.Shortcut: s++; break;
                case ItemType.File: file++; break;
                case ItemType.Special: sp++; break;
            }
        }
        return (total, f, s, file, sp);
    }
}

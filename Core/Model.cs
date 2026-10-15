namespace DeskLite.Core;

/// <summary>
/// 项目类型码 —— 与 DeskGo 的定义完全一致。
/// 证据来源：DeskGo 运行日志 31,788 条 SaveFencesItem 记录统计。
///   1 = 文件夹（无扩展名的目录）
///   2 = 快捷方式（.lnk / .url）
///   3 = 特殊项（系统项、异常路径）
///   4 = 普通文件（.png .pdf .txt .exe …）
/// </summary>
public enum ItemType
{
    Folder = 1,
    Shortcut = 2,
    Special = 3,
    File = 4,
}

/// <summary>内置格子 ID —— 与 DeskGo 保持一致。</summary>
public static class BoxIds
{
    public const int System = 1;        // 系统格子
    public const int MyFolders = 101;   // 我的文件夹
    public const int MyFiles = 102;     // 我的文件
    public const int Apps = 104;        // APP
}

/// <summary>字段分隔符 —— DeskGo 的序列化分隔符，原样保留。</summary>
public static class Fmt
{
    public const string Sep = "<|>";
}

/// <summary>
/// 格子内的一个项。序列化格式与 DeskGo 的 CDataFile::SaveFencesItem 一致：
///   item[名称 &lt;|&gt; BoxID &lt;|&gt; 位置 &lt;|&gt; 类型码 &lt;|&gt; 完整路径 &lt;|&gt; 标志]
/// </summary>
public sealed class DeskItem
{
    /// <summary>显示名（不含扩展名）</summary>
    public string Name { get; set; } = "";

    /// <summary>所属格子 ID</summary>
    public int BoxId { get; set; }

    /// <summary>格子内排序位</summary>
    public int Position { get; set; }

    /// <summary>类型码 —— 归类判据</summary>
    public ItemType Type { get; set; }

    /// <summary>完整路径</summary>
    public string FullPath { get; set; } = "";

    /// <summary>附加状态位</summary>
    public int Flags { get; set; }

    public string Serialize() => string.Join(Fmt.Sep,
        Name,
        BoxId.ToString(),
        Position.ToString(),
        ((int)Type).ToString(),
        FullPath,
        Flags.ToString());

    public static DeskItem? Parse(string body)
    {
        var p = body.Split(Fmt.Sep);
        if (p.Length < 6) return null;
        if (!int.TryParse(p[1], out var box)) return null;
        if (!int.TryParse(p[2], out var pos)) return null;
        if (!int.TryParse(p[3], out var type)) return null;
        int.TryParse(p[5], out var flags);
        return new DeskItem
        {
            Name = p[0],
            BoxId = box,
            Position = pos,
            Type = (ItemType)type,
            FullPath = p[4],
            Flags = flags,
        };
    }
}

/// <summary>
/// 一个格子。序列化格式与 DeskGo 的 CDataFile::SaveFencesBoxScreenInfo 一致：
///   boxScreen[BoxID &lt;|&gt; 名称 &lt;|&gt; x &lt;|&gt; y &lt;|&gt; 宽 &lt;|&gt; 高 &lt;|&gt; 折叠 &lt;|&gt; 0 &lt;|&gt; 2 &lt;|&gt; 对齐 &lt;|&gt; ManualSize &lt;|&gt; SchemaVersion]
///
/// 末两位是 DeskGo 里「恒为常量」的空位，本精简版借用来存自己的扩展状态：
///   · 下标 10 → <see cref="ManualSize"/>（用户手工定过尺寸，不再自动贴合内容）
///   · 下标 11 → <see cref="SchemaVersion"/>（本行字段语义版本，见下方说明）
/// </summary>
public sealed class DeskBox
{
    /// <summary>
    /// 当前 boxScreen 行的字段语义版本。
    ///   0 = DeskGo 原生布局（没有任何 DeskLite 扩展字段，读到即为「待迁移」）
    ///   1 = 启用下标 10/11（ManualSize / SchemaVersion 自身）
    ///
    /// 为什么要这一位：以后若给格子加**需要落盘的新状态**（最典型的是「此格子映射到
    /// 磁盘某个目录」，也就是 DesGo 的任意门），老数据文件里那一格是空的 —— 没有版本号
    /// 就只能靠「字段数够不够」猜，一旦猜错就是把用户布局读成垃圾。
    /// 有了它，加载时按版本号分段补齐即可，见 <see cref="DeskData.Migrate"/>。
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>该行记录的字段语义版本；由 <see cref="DeskData.Migrate"/> 负责升到当前值</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 340;
    public int Height { get; set; } = 430;

    /// <summary>格子自动收展（折叠）状态</summary>
    public bool Collapsed { get; set; }

    /// <summary>
    /// 用户手工拖拽过四角 / 四边调整过尺寸。
    /// 置位后不再按内容自动贴合尺寸（否则用户刚拉好的大小会被下一次刷新抹掉），
    /// 内容装不下时靠格子内部滚动显示。
    /// </summary>
    public bool ManualSize { get; set; }

    /// <summary>该格子接受哪些类型码的项（决定自动归类去向）</summary>
    public HashSet<ItemType> Accepts { get; set; } = new();

    /// <summary>是否为内置格子（内置格子不可删除）</summary>
    public bool IsBuiltIn => Id is BoxIds.System or BoxIds.MyFolders or BoxIds.MyFiles or BoxIds.Apps;

    public string Serialize() => $"boxScreen[{string.Join(Fmt.Sep,
        Id.ToString(),
        Name,
        X.ToString(),
        Y.ToString(),
        Width.ToString(),
        Height.ToString(),
        Collapsed ? "1" : "0",
        "0",
        "2",
        Accepts.Contains(ItemType.Folder) ? "4" : "1",
        ManualSize ? "1" : "0",
        SchemaVersion.ToString())}]";

    public static DeskBox? Parse(string body)
    {
        var p = body.Split(Fmt.Sep);
        if (p.Length < 6) return null;
        if (!int.TryParse(p[0], out var id)) return null;
        var box = new DeskBox
        {
            Id = id,
            Name = p[1],
            X = int.TryParse(p[2], out var x) ? x : 0,
            Y = int.TryParse(p[3], out var y) ? y : 0,
            Width = int.TryParse(p[4], out var w) ? w : 340,
            Height = int.TryParse(p[5], out var h) ? h : 430,
            Collapsed = p.Length > 6 && p[6] == "1",
            // 第 11 个字段（下标 10）在 DeskGo 里恒为 0，是可自由使用的空位
            ManualSize = p.Length > 10 && p[10] == "1",
            // 第 12 个字段（下标 11）同样恒为 0；老文件读出来是 0 → 视为 v0，交给 Migrate 升级
            SchemaVersion = p.Length > 11 && int.TryParse(p[11], out var sv) ? sv : 0,
        };
        return box;
    }
}

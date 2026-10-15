using System.Text;

namespace DeskLite.Core;

/// <summary>
/// 全局数据容器：格子集合 + 项集合。
/// 文本序列化格式与 DeskGo 的 ConFile.dat 键结构对齐（键名保持一致，便于对照）：
///   CFG_FENCES_CONFIGVERSION &lt;|&gt; 2
///   CFG_FENCESBOX_COUNT      &lt;|&gt; N
///   boxScreen[BoxID &lt;|&gt; 名称 &lt;|&gt; x &lt;|&gt; y &lt;|&gt; 宽 &lt;|&gt; 高 &lt;|&gt; …]
///   CFG_FENCESITEM_COUNT     &lt;|&gt; M
///   item[名称 &lt;|&gt; BoxID &lt;|&gt; 位置 &lt;|&gt; 类型码 &lt;|&gt; 完整路径 &lt;|&gt; 标志]
/// </summary>
public sealed class DeskData
{
    public int ConfigVersion { get; set; } = 2;

    public List<DeskBox> Boxes { get; } = new();
    public List<DeskItem> Items { get; } = new();

    /// <summary>创建内置的四个格子（与 DeskGo 出厂状态一致：系统 + 我的文件夹 + 我的文件 + APP）</summary>
    public static DeskData CreateDefault()
    {
        var d = new DeskData();
        d.Boxes.Add(new DeskBox
        {
            Id = BoxIds.System,
            Name = "系统",
            X = 1140, Y = 60, Width = 340, Height = 430,
            Accepts = { ItemType.Special },
        });
        d.Boxes.Add(new DeskBox
        {
            Id = BoxIds.MyFolders,
            Name = "我的文件夹",
            X = 60, Y = 60, Width = 340, Height = 430,
            Accepts = { ItemType.Folder },
        });
        d.Boxes.Add(new DeskBox
        {
            Id = BoxIds.MyFiles,
            Name = "我的文件",
            X = 420, Y = 60, Width = 340, Height = 430,
            Accepts = { ItemType.File },
        });
        d.Boxes.Add(new DeskBox
        {
            Id = BoxIds.Apps,
            Name = "APP",
            X = 780, Y = 60, Width = 340, Height = 430,
            Accepts = { ItemType.Shortcut, ItemType.File },
        });
        return d;
    }

    public DeskBox? FindBox(int id) => Boxes.FirstOrDefault(b => b.Id == id);

    /// <summary>
    /// 确保「系统格子」存在（承载 此电脑 / 回收站 / 网络 这类 Shell 虚拟项）。
    ///
    /// 早期版本没有实现虚拟项，因此只建了 101/102/104 三个格子；老数据文件里同样没有系统格子。
    /// 这里补一个，关键是**必须挑一个与现有格子不相交的位置** ——
    /// 否则启动时的 <c>ArrangeIfOverlapping()</c> 会判定重叠、把用户拖好的布局整体重排，
    /// 表现出来就是「位置又没记住」。
    /// </summary>
    /// <param name="workArea">主屏工作区（由调用方传入，避免 Core 依赖 WinForms 的 Screen）</param>
    /// <returns>本次是否新建了格子</returns>
    public bool EnsureSystemBox(Rectangle workArea)
    {
        if (Boxes.Any(b => b.Id == BoxIds.System)) return false;

        var box = new DeskBox
        {
            Id = BoxIds.System,
            Name = "系统",
            Width = DefaultBoxWidth,
            Height = DefaultBoxHeight,
            Accepts = { ItemType.Special },
        };

        var spot = FindFreeSpot(workArea, new Size(DefaultBoxWidth, DefaultBoxHeight), box.Id);
        box.X = spot.X;
        box.Y = spot.Y;

        Boxes.Add(box);
        return true;
    }

    // ─────────────── 格子的新建 / 删除 / 重命名 ───────────────

    /// <summary>新建格子的默认尺寸</summary>
    public const int DefaultBoxWidth = 300;
    public const int DefaultBoxHeight = 200;

    /// <summary>
    /// 为新格子分配一个不冲突的 ID。
    /// 内置格子只占 1 / 101 / 102 / 104，用户格子统一从 200 起步，
    /// 这样即使以后内置集合扩充也不会撞号。
    /// </summary>
    public int NextBoxId()
    {
        int max = Boxes.Count == 0 ? 0 : Boxes.Max(b => b.Id);
        return Math.Max(max + 1, 200);
    }

    /// <summary>
    /// 新建格子：自动挑一个**不与现有格子相交**的落点，省得用户自己挪。
    /// 新格子默认什么都收（靠拖拽 / 「以此格子为准」手工指派来装东西）。
    /// </summary>
    public DeskBox CreateBox(string name, Rectangle workArea)
    {
        var box = new DeskBox
        {
            Id = NextBoxId(),
            Name = string.IsNullOrWhiteSpace(name) ? "新建格子" : name.Trim(),
            Width = DefaultBoxWidth,
            Height = DefaultBoxHeight,
            Accepts = { ItemType.File, ItemType.Folder, ItemType.Shortcut },
        };

        var spot = FindFreeSpot(workArea, new Size(box.Width, box.Height), box.Id);
        box.X = spot.X;
        box.Y = spot.Y;

        Boxes.Add(box);
        return box;
    }

    /// <summary>
    /// 删除格子。内置格子（系统 / 我的文件夹 / 我的文件 / APP）不可删除。
    /// 格子里手工指派过的项一并释放，回到规则自动归类
    /// （不这么做的话，项会指向一个已不存在的格子而彻底消失）。
    /// </summary>
    public bool DeleteBox(int id)
    {
        var box = FindBox(id);
        if (box == null) return false;
        if (box.IsBuiltIn) return false;

        Boxes.Remove(box);
        Items.RemoveAll(i => i.BoxId == id);
        return true;
    }

    /// <summary>重命名格子（内置格子也允许改名）</summary>
    public bool RenameBox(int id, string name)
    {
        var box = FindBox(id);
        if (box == null) return false;

        var n = (name ?? "").Trim();
        if (n.Length == 0) return false;

        box.Name = n;
        return true;
    }

    /// <summary>
    /// 在工作区里找一个不与现有格子相交的落点：列优先，每列从上往下。
    /// 现有格子按「预期尺寸」占位（宽高取 max(实际, 给定尺寸)），宁可留多不留少。
    /// 实在没空位就贴右下角，交给启动时的重叠检测收尾。
    /// </summary>
    private Point FindFreeSpot(Rectangle workArea, Size size, int excludeId = -1)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
            workArea = new Rectangle(0, 0, 1920, 1080);

        const int Margin = 40, Gap = 24;
        int w = size.Width, h = size.Height;

        var taken = Boxes
            .Where(b => b.Id != excludeId)
            .Select(b => new Rectangle(b.X, b.Y, Math.Max(w, b.Width), Math.Max(h, b.Height)))
            .ToList();

        var mine = new Rectangle(0, 0, w, h);

        for (int x = workArea.Left + Margin; x + w <= workArea.Right - Margin; x += w + Gap)
        {
            for (int y = workArea.Top + Margin; y + h <= workArea.Bottom - Margin; y += h + Gap)
            {
                mine.X = x;
                mine.Y = y;
                if (taken.All(r => !r.IntersectsWith(mine))) return new Point(x, y);
            }
        }

        return new Point(
            Math.Max(workArea.Left + Margin, workArea.Right - Margin - w),
            Math.Max(workArea.Top + Margin, workArea.Bottom - Margin - h));
    }

    /// <summary>按规则把一个文件/文件夹纳入数据模型（已存在则不重复添加）</summary>
    public DeskItem? AddPath(string path)
    {
        var (type, boxId) = Rules.Resolve(path);
        var existing = Items.FirstOrDefault(i =>
            string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Type = type;
            existing.BoxId = boxId;
            return existing;
        }

        var box = FindBox(boxId) ?? FindBox(BoxIds.MyFiles);
        if (box == null) return null;

        var item = new DeskItem
        {
            Name = Rules.DisplayName(path),
            BoxId = box.Id,
            Position = Items.Count(i => i.BoxId == box.Id),
            Type = type,
            FullPath = path,
            Flags = 0,
        };
        Items.Add(item);
        return item;
    }

    // ─────────────── 手工指派（拖拽换格） ───────────────

    /// <summary>
    /// 把一个项手工指派到指定格子 —— 即 DeskGo 里「把图标拖进另一个格子」的行为。
    /// 以 <see cref="DeskItem.Flags"/>=1 标记为「手工覆盖」，优先于规则自动归类。
    /// </summary>
    public void AssignBox(string path, string name, ItemType type, int boxId)
    {
        var ex = Items.FirstOrDefault(i =>
            string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));

        // 落到目标格子的**末尾**：取现有记录里最大的位次 +1。
        // 不能用「记录条数」当位次 —— 用户拖过顺序的格子位次是 0..n-1 连续编号，
        // 但一个格子里可能只有部分项有记录，用条数会插到中间去。
        int next = Items
            .Where(i => i.BoxId == boxId && !ReferenceEquals(i, ex))
            .Select(i => i.Position)
            .DefaultIfEmpty(-1)
            .Max() + 1;

        if (ex != null)
        {
            ex.BoxId = boxId;
            ex.Name = name;
            ex.Type = type;
            ex.Flags = 1;
            ex.Position = next;
            return;
        }

        Items.Add(new DeskItem
        {
            Name = name,
            BoxId = boxId,
            Position = next,
            Type = type,
            FullPath = path,
            Flags = 1,
        });
    }

    /// <summary>查询手工指派的目标格子；无覆盖时返回 <c>null</c>（表示按规则自动归类）</summary>
    public int? BoxOverride(string path)
    {
        var ex = Items.FirstOrDefault(i =>
            string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        return ex is { Flags: 1 } ? ex.BoxId : null;
    }

    /// <summary>清除某个项的手工指派（回到规则自动归类）</summary>
    public void ClearAssign(string path)
        => Items.RemoveAll(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>移除已不在桌面上的项（保持与文件系统一致）</summary>
    public int PruneMissing()
    {
        return Items.RemoveAll(i =>
        {
            try
            {
                return !File.Exists(i.FullPath) &&
                       !Directory.Exists(i.FullPath) &&
                       !IsVirtual(i.FullPath);
            }
            catch { return true; }
        });
    }

    private static bool IsVirtual(string path)
        => path.StartsWith("::", StringComparison.Ordinal) || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

    /// <summary>重排某个格子内的位置索引</summary>
    public void Reindex(int boxId)
    {
        var list = Items.Where(i => i.BoxId == boxId).OrderBy(i => i.Position).ToList();
        for (int i = 0; i < list.Count; i++) list[i].Position = i;
    }

    // ─────────────── 格子内顺序（拖动排序） ───────────────

    /// <summary>
    /// 记录一个格子内的排列顺序。
    ///
    /// 复用 <see cref="DeskItem.Position"/> 与 <see cref="DeskItem.Flags"/>=1，
    /// **不新增任何序列化格式**：语义上「拖到第几个位置」和「钉在这个格子」
    /// 本来就是同一件事（DeskGo 的 <c>item[]</c> 行也是每项一条、带位置）。
    ///
    /// 调用方传进来的必须是该格子**当前完整**的顺序 —— 这里会把每一项都写成
    /// 显式记录，于是整个格子变成确定性排布，不会因为扫描顺序变化而跳动。
    /// 代价是这些项从此「手工指定优先」，不再随文件类型变化被自动改派，
    /// 这正是 DeskGo 里手工整理之后的预期行为（想退回请用「按规则自动归类」）。
    /// </summary>
    public void ReorderBox(int boxId, IReadOnlyList<string> orderedPaths)
    {
        for (int i = 0; i < orderedPaths.Count; i++)
        {
            var path = orderedPaths[i];
            if (string.IsNullOrEmpty(path)) continue;

            var ex = Items.FirstOrDefault(x =>
                string.Equals(x.FullPath, path, StringComparison.OrdinalIgnoreCase));

            if (ex != null)
            {
                ex.BoxId = boxId;
                ex.Position = i;
                ex.Flags = 1;
                continue;
            }

            // 之前走规则自动归类的项：第一次为它落一条显式记录
            var (type, _) = Rules.Resolve(path);
            Items.Add(new DeskItem
            {
                Name = Rules.DisplayName(path),
                BoxId = boxId,
                Position = i,
                Type = type,
                FullPath = path,
                Flags = 1,
            });
        }
    }

    /// <summary>
    /// 某个格子里**已经显式记录过**的顺序（路径 → 位次）。
    /// 只认 <c>Flags=1</c> 的行；库里有但这里查不到的项表示「从没排过」，
    /// 由调用方排在有序项之后、保持扫描顺序。
    /// </summary>
    public Dictionary<string, int> RecordedOrder(int boxId)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in Items)
            if (i.BoxId == boxId && i.Flags == 1)
                map[i.FullPath] = i.Position;
        return map;
    }

    // ─────────────── 格式版本迁移 ───────────────

    /// <summary>
    /// 本次加载是否把旧格式数据升到了当前 <see cref="DeskBox.CurrentSchemaVersion"/>。
    /// 为 true 时调用方应尽快回写一次（正常启动路径里由 MainContext 负责），
    /// 让磁盘上的文件一次性对齐到新格式，之后就不会再判为「需要迁移」。
    /// </summary>
    public bool Migrated { get; private set; }

    /// <summary>
    /// 按 <see cref="DeskBox.SchemaVersion"/> 把老数据逐级补齐到当前格式。
    ///
    /// 现在只有两级（0 → 1），而 v1 的全部内容就是「字段语义版本号本身」，
    /// 所以这里的循环只做版本号推进。**将来加字段时，在对应的 if 分支里补默认值即可**，
    /// 例如：
    ///   <code>
    ///   if (b.SchemaVersion &lt; 2) { b.MappedFolder = ""; b.SchemaVersion = 2; }
    ///   </code>
    /// 关键是：判据永远是**行内记录的版本号**，而不是「字段个数够不够」——
    /// 后者在字段可选、或者对方程序也往里塞东西时会直接读串。
    /// </summary>
    private void Migrate()
    {
        foreach (var b in Boxes)
        {
            if (b.SchemaVersion >= DeskBox.CurrentSchemaVersion) continue;

            // v0（DeskGo 原生 / 早期 DeskLite 落盘）→ v1：下标 11 由常量 0 改为真实版本号。
            // 老文件里这一位本来就是 0，语义上等价于「没有扩展字段」，无需搬运任何数据。
            b.SchemaVersion = DeskBox.CurrentSchemaVersion;
            Migrated = true;
        }
    }

    // ─────────────── 序列化 ───────────────

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("CFG_FENCES_CONFIGVERSION").Append(Fmt.Sep).Append(ConfigVersion).AppendLine();
        sb.Append("CFG_FENCESBOX_COUNT").Append(Fmt.Sep).Append(Boxes.Count).AppendLine();
        foreach (var b in Boxes) sb.AppendLine(b.Serialize());
        sb.Append("CFG_FENCESITEM_COUNT").Append(Fmt.Sep).Append(Items.Count).AppendLine();
        foreach (var i in Items) sb.Append("item[").Append(i.Serialize()).AppendLine("]");
        return sb.ToString();
    }

    public static DeskData Parse(string text)
    {
        var d = new DeskData();
        if (string.IsNullOrWhiteSpace(text)) return CreateDefault();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;

            if (line.StartsWith("boxScreen[", StringComparison.Ordinal) && line.EndsWith("]"))
            {
                var b = DeskBox.Parse(line[10..^1]);
                if (b != null) d.Boxes.Add(b);
            }
            else if (line.StartsWith("item[", StringComparison.Ordinal) && line.EndsWith("]"))
            {
                var it = DeskItem.Parse(line[5..^1]);
                if (it != null) d.Items.Add(it);
            }
            else if (line.StartsWith("CFG_FENCES_CONFIGVERSION", StringComparison.Ordinal))
            {
                var p = line.Split(Fmt.Sep);
                if (p.Length > 1 && int.TryParse(p[1], out var v)) d.ConfigVersion = v;
            }
        }

        // 恢复 Accepts（序列化里只存了对齐标记，重建内置格子的接受类型）
        foreach (var b in d.Boxes)
        {
            switch (b.Id)
            {
                case BoxIds.System:    b.Accepts = new HashSet<ItemType> { ItemType.Special }; break;
                case BoxIds.MyFolders: b.Accepts = new HashSet<ItemType> { ItemType.Folder }; break;
                case BoxIds.MyFiles: b.Accepts = new HashSet<ItemType> { ItemType.File }; break;
                case BoxIds.Apps: b.Accepts = new HashSet<ItemType> { ItemType.Shortcut, ItemType.File }; break;
                default: b.Accepts = new HashSet<ItemType> { ItemType.File, ItemType.Folder, ItemType.Shortcut }; break;
            }
        }

        if (d.Boxes.Count == 0) return CreateDefault();

        // 读到老格式就在这里一次性补齐（并把 Migrated 置位，提示调用方回写）
        d.Migrate();
        return d;
    }
}

using System.Text;

namespace DeskLite.Core;

/// <summary>
/// 本地持久化 + 滚动备份。
/// 存储位置：%APPDATA%\DeskLite\（与 DeskGo 的 %APPDATA%\Tencent\DeskGo\ 对应）
/// 备份策略沿用 DeskGo：
///   FencesDataFile.dat         当前数据
///   FencesDataFile_temp_1..8   8 份滚动日备
///   FencesDataFile_week        周快照
///   FencesDataFile_month       月快照
/// 说明：DeskGo 的原文件是加密的（熵 7.985 bit/byte），无密钥无法读写，
///       因此本精简版采用**同结构明文**存储，键名与字段格式完全对齐，便于对照与调试。
/// </summary>
public sealed class DataStore
{
    public const string MainFile = "FencesDataFile.dat";
    public const string WeekFile = "FencesDataFile_week";
    public const string MonthFile = "FencesDataFile_month";
    public const int MaxDailyBackups = 8;

    public string Root { get; }

    /// <summary>
    /// 只读（沙箱）模式：<see cref="Save"/> 变成空操作，磁盘上一个字节都不会被改写。
    ///
    /// 为什么需要它：命令行自检（<c>--probe</c> / <c>--mem</c> / <c>--layout</c> …）为了拿到
    /// 真实的格子布局与内存占用，必须真的把 <c>MainContext</c> 建起来；但它们的职责只是
    /// **观测**，绝不该反过来改写用户的 FencesDataFile.dat。
    /// 早期版本正是踩了这个坑 —— 跑一次诊断就把主文件连同当日滚动备份一起重写，
    /// 还会顺手把超出 8 份的旧备份 Trim 掉，属于「看一眼就把人东西动了」。
    /// </summary>
    public bool ReadOnly { get; }

    public DataStore(string? root = null, bool readOnly = false)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskLite");
        ReadOnly = readOnly;

        // 只读模式下连目录都不建：数据目录该不该存在，不该由一次诊断来决定
        if (!ReadOnly) Directory.CreateDirectory(Root);
    }

    public string MainPath => Path.Combine(Root, MainFile);

    /// <summary>读取数据；不存在或损坏时返回内置默认结构</summary>
    public DeskData Load()
    {
        try
        {
            if (!File.Exists(MainPath)) return DeskData.CreateDefault();
            var text = File.ReadAllText(MainPath, Encoding.UTF8);
            var d = DeskData.Parse(text);
            return d.Boxes.Count > 0 ? d : DeskData.CreateDefault();
        }
        catch
        {
            return DeskData.CreateDefault();
        }
    }

    /// <summary>保存数据，并按日滚动备份。只读模式下直接返回，不产生任何磁盘写入。</summary>
    public void Save(DeskData data)
    {
        if (ReadOnly) return;

        Directory.CreateDirectory(Root);
        var text = data.Serialize();

        // 写临时文件再改名，避免写入中断损坏主文件
        var tmp = MainPath + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (File.Exists(MainPath)) File.Delete(MainPath);
        File.Move(tmp, MainPath);

        RotateDaily(text);
    }

    private void RotateDaily(string text)
    {
        var today = DateTime.Now.ToString("yyyyMMdd");

        // 日备：temp_1..8，滚动
        var slot = Path.Combine(Root, $"{MainFile}_temp_{today}");
        if (!File.Exists(slot))
        {
            File.WriteAllText(slot, text, new UTF8Encoding(false));
            TrimDailySlots();
        }
        else
        {
            File.WriteAllText(slot, text, new UTF8Encoding(false));
        }

        // 周快照（每周一）
        if (DateTime.Now.DayOfWeek == DayOfWeek.Monday)
            File.WriteAllText(Path.Combine(Root, WeekFile), text, new UTF8Encoding(false));

        // 月快照（每月 1 日）
        if (DateTime.Now.Day == 1)
            File.WriteAllText(Path.Combine(Root, MonthFile), text, new UTF8Encoding(false));
    }

    private void TrimDailySlots()
    {
        try
        {
            var slots = Directory.GetFiles(Root, $"{MainFile}_temp_*")
                                 .OrderByDescending(f => f)
                                 .ToList();
            foreach (var old in slots.Skip(MaxDailyBackups))
                File.Delete(old);
        }
        catch { /* 备份清理失败不影响主流程 */ }
    }

    /// <summary>列出可用的备份（供"恢复备份"使用）</summary>
    public IReadOnlyList<(string Path, DateTime Time, long Size)> ListBackups()
    {
        var list = new List<(string, DateTime, long)>();
        try
        {
            var main = new FileInfo(MainPath);
            if (main.Exists) list.Add((main.FullName, main.LastWriteTime, main.Length));

            foreach (var f in Directory.GetFiles(Root, $"{MainFile}_*"))
            {
                var fi = new FileInfo(f);
                list.Add((fi.FullName, fi.LastWriteTime, fi.Length));
            }
            foreach (var f in new[] { WeekFile, MonthFile })
            {
                var p = Path.Combine(Root, f);
                if (File.Exists(p))
                {
                    var fi = new FileInfo(p);
                    list.Add((fi.FullName, fi.LastWriteTime, fi.Length));
                }
            }
        }
        catch { }
        return list.OrderByDescending(x => x.Item2).ToList();
    }
}

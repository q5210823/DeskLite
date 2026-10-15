namespace DeskLite.Core;

/// <summary>
/// 桌面上的一个「虚拟项」—— 位于 Shell 命名空间里、**没有文件系统实体**的系统图标。
///
/// 为什么需要单独一层：
///   「此电脑 / 回收站 / 网络」这类项由 explorer 的命名空间扩展（namespace extension）提供，
///   它们既不是 %USERPROFILE%\Desktop 下的文件，也不是 %PUBLIC%\Desktop 下的文件。
///   因此 <see cref="DesktopScanner"/> 那种「枚举目录」的做法**永远看不到它们** ——
///   实测原生 ListView 有 60 项、目录枚举只有 58 项，差的就是这两个。
///
/// 表示方式：
///   内部用伪路径 <c>::{CLSID}</c>（Shell 解析名），既能被 <see cref="Rules"/> 识别为
///   <see cref="ItemType.Special"/>，也能直接交给 <c>explorer.exe shell:::{CLSID}</c> 打开。
///   <see cref="DeskData.IsVirtual"/> 与 <see cref="DeskData.PruneMissing"/> 早就预留了这个前缀判据。
/// </summary>
public sealed class VirtualItem
{
    /// <summary>Shell 命名空间里的对象 GUID（不带花括号）</summary>
    public string Clsid { get; }

    /// <summary>规范中文名（作为兜底显示名）</summary>
    public string Zh { get; }

    /// <summary>shell 各语言/版本下可能出现的显示名，用于与 ListView 文本比对</summary>
    public IReadOnlyList<string> Aliases { get; }

    internal VirtualItem(string clsid, string zh, params string[] aliases)
    {
        Clsid = clsid;
        Zh = zh;
        Aliases = new[] { zh }.Concat(aliases).ToArray();
    }

    /// <summary>本程序内部使用的伪路径，形如 <c>::{20D04FE0-…}</c></summary>
    public string Path => "::" + Clsid;

    /// <summary>交给 ShellExecute 打开的解析名，形如 <c>shell:::{20D04FE0-…}</c></summary>
    public string ShellPath => "shell:::" + Clsid;

    /// <summary>ListView 里显示的文本是否就是本项</summary>
    public bool Matches(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        foreach (var a in Aliases)
            if (string.Equals(n, a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>桌面虚拟项总表 + 伪路径工具</summary>
public static class VirtualItems
{
    // CLSID 取自各自命名空间扩展的官方 GUID：
    //   此电脑     {20D04FE0-3AEA-1069-A2D8-08002B30309D}
    //   回收站     {645FF040-5081-101B-9F08-00AA002F954E}
    //   网络       {F02C1A0D-BE21-4350-88B0-7367FC96EF3C}
    private static readonly VirtualItem[] Table =
    {
        new("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "此电脑",
            "计算机", "这台电脑", "我的电脑", "This PC", "Computer", "My Computer"),

        new("{645FF040-5081-101B-9F08-00AA002F954E}", "回收站",
            "Recycle Bin"),

        new("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "网络",
            "网上邻居", "Network"),
    };

    /// <summary>按原生桌面上的默认顺序排列（此电脑 → 回收站 → 网络）</summary>
    public static IReadOnlyList<VirtualItem> All => Table;

    /// <summary>是否是虚拟项伪路径（<c>::</c> 或 <c>shell:</c> 前缀）</summary>
    public static bool IsVirtual(string? path)
        => !string.IsNullOrEmpty(path)
           && (path.StartsWith("::", StringComparison.Ordinal)
               || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase));

    /// <summary>伪路径 → 虚拟项；不是虚拟项或认不出来则返回 <c>null</c></summary>
    public static VirtualItem? Lookup(string? path)
    {
        if (!IsVirtual(path)) return null;

        var body = path!.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? path[6..] : path[2..];
        body = body.TrimStart(':');

        foreach (var v in Table)
            if (body.Contains(v.Clsid, StringComparison.OrdinalIgnoreCase)) return v;

        return null;
    }

    /// <summary>ListView 显示名 → 虚拟项</summary>
    public static VirtualItem? ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var v in Table)
            if (v.Matches(name!)) return v;
        return null;
    }

    /// <summary>伪路径的友好显示名；认不出来时原样返回</summary>
    public static string FriendlyName(string path) => Lookup(path)?.Zh ?? path;
}

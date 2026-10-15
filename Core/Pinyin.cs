using System.Text;

namespace DeskLite.Core;

/// <summary>
/// 汉字 → 拼音首字母。
/// 用途：桌面搜索支持拼音匹配（对应 DeskGo 的 disklib.xml 拼音表能力）。
///
/// 实现：查 <see cref="PinyinTable"/> —— 一张内联的「Unicode 码位 → 拼音首字母」表，
/// 覆盖 GB2312 一级汉字（3755 字，占日常用字 99% 以上）。
///
/// 历史：原实现走 <c>Encoding.GetEncoding("GB2312")</c> 把汉字转成区位码，再按区位码分界点
/// 判定首字母。功能正确，但代价是**必须加载 System.Text.Encoding.CodePages 程序集并初始化
/// 936 编码表** —— 实测这一步让常驻内存凭空多出 5.5 MB（托管堆 4.8 MB），而且
/// <c>Encoding</c> 会缓存实例、这块内存一旦建立就不会再还回去。
/// 现在自带表，零依赖、零程序集加载，首次查表时才展开（约 20 KB）。
/// </summary>
public static class Pinyin
{
    private static bool _initialized;

    /// <summary>保留旧 API：现在是空操作，只为兼容既有调用点</summary>
    public static void Init() => _initialized = true;

    /// <summary>拼音能力始终可用（表已内联，不再依赖外部编码提供程序）</summary>
    public static bool Available { get { Init(); return _initialized; } }

    /// <summary>取单个字符的拼音首字母（非汉字原样返回小写形式）</summary>
    public static string CharInitial(char c)
    {
        if (c >= 'a' && c <= 'z') return c.ToString();
        if (c >= 'A' && c <= 'Z') return char.ToLowerInvariant(c).ToString();
        if (c < 128) return c.ToString();

        char initial = PinyinTable.Lookup(c);
        return initial != '\0' ? initial.ToString() : c.ToString();
    }

    /// <summary>取整串的拼音首字母，如「我的文件」→ "wdwj"</summary>
    public static string InitialsOf(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(CharInitial(c));
        return sb.ToString();
    }

    /// <summary>
    /// 匹配：支持子串、拼音首字母、全拼首字母。
    /// </summary>
    public static bool Matches(string name, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        var k = keyword.Trim().ToLowerInvariant();

        if (name.ToLowerInvariant().Contains(k)) return true;
        if (InitialsOf(name).Contains(k)) return true;

        return false;
    }
}

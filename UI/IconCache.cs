using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using DeskLite.Win32;

namespace DeskLite.UI;

/// <summary>
/// 图标缓存。取系统关联图标（与 DeskGo 的 ExtIconCache 思路一致），按路径缓存，避免重复取图标与句柄泄漏。
///
/// 实现踩过的三个坑，依次列出：
///
///   ① **alpha 不能走 Icon.ToBitmap()** —— 它内部用 Bitmap.FromHbitmap，会丢掉 alpha 通道，
///      把透明区域画成纯黑方块。必须用 DrawIconEx 画到 32bpp ARGB 位图上。
///
///   ② **SHGetFileInfo(SHGFI_ICON) 对 .lnk 快捷方式经常返回纯黑图标**（实测：115浏览器、
///      Bandizip、Cheat Engine、ima、Microsoft Edge、ReNamer 等一批快捷方式全黑；
///      换成 SHGFI_SYSICONINDEX + SHGetImageList 走系统 Imagelist 就能拿到真实图标）。
///
///   ③ **路径要用真实的** —— 文件/目录存在时传真实路径才能拿到该程序/文档的专属图标；
///      只有路径不存在时才退化为「按扩展名取通用图标」（SHGFI_USEFILEATTRIBUTES）。
///
/// 因此最终采用「多级回退 + 黑图检测」策略，保证任何情况下都不会出现黑方块。
/// </summary>
internal sealed class IconCache : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly Dictionary<string, Image> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> _byExt = new(StringComparer.OrdinalIgnoreCase);

    public int Size { get; }

    public IconCache(int size = 48) => Size = size;

    // ───────── 诊断溯源（仅 --iconprobe 时开启）─────────

    /// <summary>一次图标提取的溯源记录</summary>
    internal sealed record Trace(string Path, string Source, int Slot, double Fill);

    private static readonly List<Trace> _traceList = new();

    /// <summary>是否记录溯源信息</summary>
    public static bool TraceEnabled { get; set; }

    public static IReadOnlyList<Trace> Traces => _traceList;

    public static void ClearTrace() => _traceList.Clear();

    private static void Note(string path, string source, int slot, double fill)
    {
        if (TraceEnabled) _traceList.Add(new Trace(path, source, slot, fill));
    }

    /// <summary>
    /// 取某个路径的图标（带缓存）。
    /// 虚拟项（此电脑 / 回收站 / 网络）按伪路径 <c>::{CLSID}</c> 传入，
    /// 内部会自动走「解析名 → PIDL」这条专用路径。
    /// </summary>
    public Image GetIcon(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;

        Image img;
        if (DiagStub || (_realExtractCount >= 0 && _extracted++ >= _realExtractCount))
        {
            // 诊断：完全不碰 shell，也不做任何位图转换 —— 只给一张手绘图
            img = HandDrawn(Size);
        }
        else if (DiagNoShellGdi)
        {
            // 诊断：走完 shell 取图标流程，但跳过 GDI+ 的渲染/扫描/缩放
            var probe = ShellIconIndex(path, Directory.Exists(path) || File.Exists(path),
                                       Ext(path), out int ix);
            if (probe)
            {
                // 走完 shell → HICON，但把渲染出来的位图直接丢掉（池内的不释放）
                var c = FromImageList(ix, Native.SHIL_EXTRALARGE);
                if (c is { } cc) { Retire(cc.Bmp); CntScale++; }
            }
            img = HandDrawn(Size);
        }
        else
        {
            img = Extract(path, Size, Directory.Exists(path) || File.Exists(path))
                  ?? Placeholder(".dat", Size);
        }

        _cache[path] = img;
        return img;
    }

    // ── 细粒度诊断：把「HICON → 位图」这条链逐段拆开（配合 --memstep 做对照）──
    //
    // 用途与教训（2026-10 那一轮压内存时留下的）：
    //   当时 `DESKLITE_ICONSTUB=1` 是 18.5 MB、`DESKLITE_ICONSTUB2=1` 是 21.6 MB，
    //   而完整流程 33.0 MB，于是判断「约 11 MB 在这条链的某个环节上」，
    //   就加了下面这一串开关想逐个绕过、找出是谁。
    //
    //   结论是：**七个开关全部无效**（都停在 32~34 MB）。真正的原因不在任何单个环节，
    //   而在「同一个图标被多渲染了 2.6 倍」——见 IsMalformed 的修复记录。
    //   这组开关保留下来，是因为它们确实是「先归因、再优化」的正确手法，
    //   下次再有类似问题可以直接复用；但别指望它们能直接指出答案。
    //
    //   全部开关一律用环境变量开启（值须为 "1"），默认关闭、零运行时开销。

    private static bool Env(string name) => Environment.GetEnvironmentVariable(name) == "1";

    /// <summary>诊断：完全不碰 shell，直接给一张手绘图</summary>
    private static readonly bool DiagStub = Env("DESKLITE_ICONSTUB");

    /// <summary>诊断：走完 shell 取图标流程，但跳过 GDI+ 的渲染/扫描/缩放</summary>
    private static readonly bool DiagNoShellGdi = Env("DESKLITE_ICONSTUB2");

    /// <summary>诊断：只对前 N 项做真实提取，其余用手绘图（判断开销是「每项累积」还是「一次性初始化」）</summary>
    private static readonly int _realExtractCount =
        int.TryParse(Environment.GetEnvironmentVariable("DESKLITE_ICONREALN"), out var rn) ? rn : -1;
    private int _extracted;

    /// <summary>诊断：只 Rent 位图，完全不创建 Graphics（去掉 Graphics.FromImage / Clear）</summary>
    private static readonly bool DiagNoGfx = Env("DESKLITE_NOGFX");

    /// <summary>诊断：创建 Graphics 但跳过 DrawIconEx（去掉 GDI 绘制本身）</summary>
    private static readonly bool DiagNoDrawIcon = Env("DESKLITE_NODRAWICON");

    /// <summary>诊断：跳过 LockBits 扫描（ContentBox / IsBlank / Grab）</summary>
    private static readonly bool DiagNoLock = Env("DESKLITE_NOLOCK");

    /// <summary>诊断：跳过 Normalize（它内部仍是 Graphics.DrawImage + HighQualityBicubic）</summary>
    private static readonly bool DiagNoNormalize = Env("DESKLITE_NONORMALIZE");

    /// <summary>诊断：跳过 Scale（直接返回原图）</summary>
    private static readonly bool DiagNoScale = Env("DESKLITE_NOSCALE");

    /// <summary>诊断：关掉 GDI+ 中间位图复用池</summary>
    private static readonly bool DiagNoPool = Env("DESKLITE_NOPOOL");

    /// <summary>
    /// 是否算「畸形容器」——必须「贴住左上角 **且** 图形确实只占一小块」同时成立。
    ///
    /// 修复记录：旧判据只看「贴边」（<see cref="IsCornerClipped"/>），而图标本来就会铺到
    /// 画布边缘附近，于是 48 档对绝大多数图标都不达标，每个图标一路掉到 256 档再回来 ——
    /// 实测每图标平均渲染 2.6 次、其中 256×256 档 49 次，常驻私有内存因此多出 11 MB。
    /// 改成与 <see cref="Normalize"/> 同一个触发条件后，每图标 1 次渲染，内存降回。
    /// </summary>
    private static bool IsMalformed(Rectangle box, Bitmap bmp, double fill)
        => IsCornerClipped(box, bmp) && fill <= 0.5;

    // ── 计数器：回答「每个图标到底渲染了几次位图、走了哪些分支」──
    internal static int CntRender, CntAccept, CntReject, CntNormalize, CntScale, CntCopy;
    internal static readonly int[] CntByShil = new int[8];

    /// <summary>见过多少个不同的图标索引（&lt;&lt; 项数就说明可以按索引记忆化）</summary>
    private static readonly HashSet<int> _seenIndex = new();
    internal static int DistinctIndexCount => _seenIndex.Count;

    /// <summary>各位图分配点「按边长统计的张数」——用来回答「到底 new 了多大的位图、多少张」</summary>
    internal static readonly SortedDictionary<int, int> SzRender = new();
    internal static readonly SortedDictionary<int, int> SzCopy = new();
    internal static readonly SortedDictionary<int, int> SzNorm = new();
    internal static readonly SortedDictionary<int, int> SzScale = new();

    private static void Bump(SortedDictionary<int, int> d, int px)
    {
        d.TryGetValue(px, out int n);
        d[px] = n + 1;
    }

    /// <summary>把「边长 → 张数」直方图算成总字节数（32bpp = 4 B/px）</summary>
    private static long BytesOf(SortedDictionary<int, int> d)
    {
        long s = 0;
        foreach (var kv in d) s += (long)kv.Key * kv.Key * 4 * kv.Value;
        return s;
    }

    private static string Hist(SortedDictionary<int, int> d)
        => d.Count == 0 ? "—" : string.Join(" ", d.Select(kv => $"{kv.Key}px×{kv.Value}"));

    internal static string CounterSummary()
    {
        int icons = CntScale == 0 ? 1 : CntScale;
        var sb = new StringBuilder();
        sb.AppendLine($"渲染 {CntRender} 次（EXTRALARGE {CntByShil[Native.SHIL_EXTRALARGE]} / JUMBO {CntByShil[Native.SHIL_JUMBO]} / LARGE {CntByShil[Native.SHIL_LARGE]}），"
                    + $"达标 {CntAccept}、未达标 {CntReject}、Normalize {CntNormalize}、Scale {CntScale}、CopyExact {CntCopy}"
                    + $" → 每图标平均渲染 {CntRender / (double)icons:0.00} 次");
        sb.AppendLine($"  不同图标索引 {_seenIndex.Count} 个（Scale {CntScale} 次 → 索引重复率 {(CntScale == 0 ? 0 : 100.0 * (CntScale - _seenIndex.Count) / CntScale):N0}%）");
        sb.AppendLine($"  RenderNative 请求: {BytesOf(SzRender) / 1048576.0:N1} MB  尺寸 {Hist(SzRender)}");
        sb.AppendLine($"    其中真正分配: {BytesOf(SzRentReal) / 1048576.0:N1} MB  尺寸 {Hist(SzRentReal)}（其余复用池命中）");
        sb.AppendLine($"  CopyExact   分配: {BytesOf(SzCopy) / 1048576.0:N1} MB  尺寸 {Hist(SzCopy)}");
        sb.AppendLine($"  Normalize   分配: {BytesOf(SzNorm) / 1048576.0:N1} MB  尺寸 {Hist(SzNorm)}");
        sb.AppendLine($"  Scale       分配: {BytesOf(SzScale) / 1048576.0:N1} MB  尺寸 {Hist(SzScale)}");
        sb.Append($"  合计真正分配 { (BytesOf(SzRentReal) + BytesOf(SzCopy) + BytesOf(SzNorm) + BytesOf(SzScale)) / 1048576.0:N1} MB");
        return sb.ToString();
    }

    internal static void ResetCounters()
    {
        CntRender = CntAccept = CntReject = CntNormalize = CntScale = CntCopy = 0;
        Array.Clear(CntByShil);
        SzRender.Clear(); SzRentReal.Clear(); SzCopy.Clear(); SzNorm.Clear(); SzScale.Clear();
    }

    private static string Ext(string path)
    {
        var e = Path.GetExtension(path);
        return e.Length == 0 ? ".dat" : e;
    }

    /// <summary>纯手绘占位图（不接触 shell / 不做任何位图转换）</summary>
    internal static Bitmap HandDrawn(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        using var pen = new Pen(Color.FromArgb(220, 220, 220), Math.Max(1f, size / 24f));
        using var brush = new SolidBrush(Color.FromArgb(90, 90, 90));
        var r = new Rectangle(size / 8, size / 12, size * 3 / 4, size * 5 / 6);
        g.FillRectangle(brush, r);
        g.DrawRectangle(pen, r);
        return bmp;
    }

    /// <summary>取某扩展名的图标（类型图标，用于搜索/列表）</summary>
    public Image GetIconByExtension(string ext)
    {
        if (string.IsNullOrEmpty(ext)) ext = ".dat";
        if (_byExt.TryGetValue(ext, out var cached)) return cached;

        Image img = Placeholder(ext, Size);
        _byExt[ext] = img;
        return img;
    }

    // ───────── 多级回退 ─────────

    /// <summary>
    /// 当前使用的提取入口 —— 直接走高清管线 <see cref="ExtractV2"/>。
    /// <see cref="ExtractLegacy"/> 保留下来只作为 <c>--iconprobe</c> 的对照基准。
    /// </summary>
    public static Bitmap? Extract(string path, int size, bool exists) => ExtractV2(path, size, exists);

    /// <summary>
    /// 【旧实现，仅对照用】依次尝试：系统 Imagelist → SHGetFileInfo → 按扩展名的通用图标。
    /// 问题：DrawIconEx 用目标尺寸直接缩放 HICON，GDI 的老缩放路径不做低通滤波 →
    /// 大图标缩小后明显像素化；且 HICON 内部位图偏小时会原样贴在左上角 → 图标看起来极小。
    /// </summary>
    public static Bitmap? ExtractLegacy(string path, int size, bool exists)
    {
        foreach (int shil in new[] { Native.SHIL_JUMBO, Native.SHIL_EXTRALARGE })
        {
            var b = ViaImageList(path, size, exists, shil);
            if (b != null && !IsBlank(b)) return b;
            b?.Dispose();
        }

        var viaShell = ViaShellIcon(path, size, exists);
        if (viaShell != null && !IsBlank(viaShell)) return viaShell;
        viaShell?.Dispose();

        // 最后一级：按扩展名取通用图标（用假路径 + FILE_ATTRIBUTE_NORMAL）
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) ext = ".dat";
        var generic = ViaShellIcon("__desklite_probe__" + ext, size, exists: false);
        if (generic != null && !IsBlank(generic)) return generic;
        generic?.Dispose();

        return null;
    }

    private Image Placeholder(string ext, int size)
    {
        // 兜底：先试「按扩展名的通用图标」，再不行就手绘一个中性占位图，保证不出现黑块
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);

        try
        {
            var v = ViaShellIcon("__desklite_probe__" + (ext.StartsWith('.') ? ext : "." + ext), size, exists: false);
            if (v != null && !IsBlank(v))
            {
                g.DrawImage(v, 0, 0);
                v.Dispose();
                return bmp;
            }
            v?.Dispose();
        }
        catch { }

        // 手绘一个中性占位图
        using var pen = new Pen(Color.FromArgb(220, 220, 220), Math.Max(1f, size / 24f));
        using var brush = new SolidBrush(Color.FromArgb(90, 90, 90));
        var r = new Rectangle(size / 8, size / 12, size * 3 / 4, size * 5 / 6);
        g.FillRectangle(brush, r);
        g.DrawRectangle(pen, r);
        for (int i = 1; i <= 3; i++)
        {
            int y = r.Top + r.Height * i / 4;
            g.DrawLine(pen, r.Left + r.Width / 6, y, r.Right - r.Width / 6, y);
        }
        return bmp;
    }

    // ═════════════════════════════════════════════════════════════════
    //  高清提取管线（当前使用）—— 原样渲染 + GDI+ 高质量缩放
    // ═════════════════════════════════════════════════════════════════
    //
    // 为什么不是「DrawIconEx 直接缩放到目标尺寸」：
    //   DrawIconEx 的缩放走的是 GDI 老路径（StretchBlt 语义），对 256 → 40 这种
    //   大幅缩小既不做低通滤波、也不做亚像素修正，结果是边缘锯齿、细线断裂 ——
    //   也就是肉眼看到的「像素化」；并且当 HICON 内部位图比目标小时，它会把
    //   原始小图**原样贴在左上角**（实测 Cheat Engine / MyLauncher 就是这样变成
    //   角落里一个看不清的小点）。
    //
    // 现在把「取像素」和「缩放」彻底拆开：
    //   ① **原样渲染**：DrawIconEx 的 cx/cy 传 HICON 内部位图的真实尺寸（1:1），
    //      完全不缩放，拿到未经污染的原始像素（含 alpha）；
    //   ② **高质量缩放**：先「逐次减半」再双三次收尾，且恒等铺满整个目标方块。

    /// <summary>ExtraLarge 档的声明尺寸：40 以下的目标尺寸用它最合适，不必碰 256 档</summary>
    private const int ExtraLargePx = 48;

    /// <summary>
    /// 取档顺序 —— 决定「一个图标要渲染几次位图」。
    ///
    /// 目标尺寸 ≤ 48 时以 ExtraLarge(48) 打头：它对 16~48 的常用尺寸既是「不小于目标」
    /// 又是「最小的那一档」，一次渲染即可命中，**完全不碰 256 档**。
    /// 目标 > 48 时（用户把图标调大）才按大→小找。
    ///
    /// 这是「常驻内存」的关键优化点：旧实现对每一项都把三档全部渲染出来再评分，
    /// 相当于每一项都要分配一张 256×256×4 = 262 KB 的位图（外加 Scale 的中间位图），
    /// 58 项就会产生几十 MB 的瞬时非托管分配，GDI+ 的堆会因此长期虚高。
    /// </summary>
    private static readonly int[] OrderSmall = { Native.SHIL_EXTRALARGE, Native.SHIL_JUMBO, Native.SHIL_LARGE };
    private static readonly int[] OrderLarge = { Native.SHIL_JUMBO, Native.SHIL_EXTRALARGE, Native.SHIL_LARGE };

    private static int[] OrderFor(int size) => size <= ExtraLargePx ? OrderSmall : OrderLarge;

    /// <summary>某一档 Imagelist 的候选提取结果</summary>
    internal readonly struct Cand
    {
        public readonly int Shil;       // 档位（SHIL_*）
        public readonly int ListPx;     // Imagelist 声明尺寸
        public readonly int NativePx;   // HICON 内部位图真实尺寸
        public readonly double FillPct; // 有效内容包围盒占画布面积比
        public readonly Rectangle Box;  // 有效内容包围盒
        public readonly Bitmap Bmp;     // 原样渲染结果

        public Cand(int shil, int listPx, int nativePx, double fill, Rectangle box, Bitmap bmp)
        {
            Shil = shil; ListPx = listPx; NativePx = nativePx; FillPct = fill; Box = box; Bmp = bmp;
        }

        /// <summary>择优评分（越小越好）：优先「不小于目标且最小」的一档；资源畸形的降权。</summary>
        public double Score(int target)
        {
            double s = NativePx >= target ? NativePx : 200000 - NativePx;
            if (FillPct < 0.30) s += 100000;                        // 小图塞大画布
            // 只有「贴边 **且** 图形很小」才算畸形容器；正常铺满的图标不加罚分，
            // 否则评分会把最好的那一档也压到最低优先级（与 acceptable 判据保持一致）
            if (Box.Width > 0 && Box.Height > 0 && IsMalformed(Box, Bmp, FillPct)) s += 50000;
            return s;
        }
    }

    /// <summary>
    /// 系统图标提取（高清管线）。依次尝试 Imagelist → SHGetFileInfo → 按扩展名的通用图标。
    /// 全部失败返回 null，由调用方兜底成占位图。
    ///
    /// 与旧实现的关键差别：**按需取档，命中即停**。
    /// 旧实现把三档全部渲染完再评分挑一个；现在按「先用最合适的那一档试，
    /// 不达标才升级到更大档」的顺序，绝大多数图标一次渲染就够（48 档只有 9 KB，
    /// 而不是 256 档的 262 KB）。
    /// </summary>
    /// <summary>
    /// 从**已知的系统图标索引**渲染出目标尺寸位图。
    ///
    /// 为什么要抽出来：虚拟项（此电脑 / 回收站 / 网络）没有文件路径，走不了
    /// <see cref="ShellIconIndex"/> 那一步；但它们的图标索引可以从原生桌面 ListView 的
    /// <c>LVIF_IMAGE</c> 直接读到，而且**就是同一个系统图标列表的索引** ——
    /// 所以从这一步往后，整条管线（档位择优 → Normalize → Scale）可以原样复用，
    /// 不必为虚拟项另写一套。
    /// </summary>
    private static Bitmap? RenderFromIndex(int index, int size, string path)
    {
        Bitmap? best = null;
        int bestShil = 0, bestNative = 0;
        double bestFill = 0, bestScore = double.MaxValue;
        var bestBox = Rectangle.Empty;

        foreach (var shil in OrderFor(size))
        {
            if (FromImageList(index, shil) is not { } c) continue;

            if (IsBlank(c.Bmp)) { Retire(c.Bmp); continue; }

            double score = c.Score(size);

            // 「畸形资源」的判据必须是「贴着左上角 **且** 图形只占一小块」两者同时成立
            // —— 这正是 Normalize 的触发条件。只看「贴边」会把绝大多数正常图标
            // （图标本来就会铺到边缘附近）误判成畸形，于是 48 档永不达标、
            // 每个图标一路掉到 256 档再回来：实测每图标平均渲染 2.6 次、
            // 其中 256×256 档 49 次，白白多出两倍以上的 shell 取图标开销。
            bool malformed = IsMalformed(c.Box, c.Bmp, c.FillPct);

            bool acceptable = c.NativePx >= size
                              && c.FillPct >= 0.30
                              && !malformed;

            if (acceptable)
            {
                // ★ 命中即停：后面的档不用再渲染
                CntAccept++;
                Retire(best);                  // 丢弃之前留的备选（独立副本）
                best = c.Bmp;                  // 复用池中的图，随后立即使用，不会被覆盖
                bestShil = c.Shil;
                bestNative = c.NativePx;
                bestFill = c.FillPct;
                bestBox = c.Box;
                break;
            }

            CntReject++;

            // 不达标：留作备选。因为下一轮 Rent 会复用同一块中间缓冲，
            // 这里必须留一份独立副本，否则备选会被后一轮的渲染覆盖。
            if (best is null || score < bestScore)
            {
                Retire(best);
                CntCopy++;
                best = CopyExact(c.Bmp);
                bestShil = c.Shil;
                bestNative = c.NativePx;
                bestFill = c.FillPct;
                bestBox = c.Box;
                bestScore = score;
            }
            Retire(c.Bmp);
        }

        if (best is not { } bmp) return null;

        var outp = Finish(bmp, bestBox, bestFill, size);   // Normalize + Scale
        Note(path, $"Imagelist#{bestShil}", bestNative, bestFill);
        Retire(bmp);
        return outp;
    }

    /// <summary>
    /// 虚拟项取图标索引：解析名 → PIDL → <c>SHGetFileInfo(SHGFI_PIDL | SHGFI_SYSICONINDEX)</c>。
    ///
    /// ⚠ 为什么不用原生桌面 ListView 里读到的图标索引（<c>LVIF_IMAGE</c>）：
    ///   **那不是同一张表。** 实测交叉验证 8 个真实桌面项，ListView 给的索引是
    ///   11/12/13/14…（它自己私有 Imagelist 的连续序号），而 SHGetFileInfo 给的是
    ///   51/93/139/226…，**8 项全部不一致**。直接拿 ListView 索引去查
    ///   <c>SHGetImageList(SHIL_*)</c> 会取到完全无关的图标 ——
    ///   实测「此电脑」变成了硬盘、「回收站」变成了带叉的驱动器。
    ///   正确做法是走 PIDL，让 shell 自己给出系统 Imagelist 里的索引。
    /// </summary>
    internal static bool ShellIconIndexByParsingName(string parsingName, out int index)
    {
        index = -1;
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (Native.SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _) != 0)
                return false;
            if (pidl == IntPtr.Zero) return false;

            var shfi = new Native.SHFILEINFO();
            var r = Native.SHGetFileInfoPidl(pidl, 0, ref shfi,
                (uint)Marshal.SizeOf<Native.SHFILEINFO>(),
                Native.SHGFI_PIDL | Native.SHGFI_SYSICONINDEX);
            if (r == IntPtr.Zero) return false;

            index = shfi.iIcon;
            return true;
        }
        catch { return false; }
        finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }

    /// <summary>虚拟项的回退路径：直接让 shell 给 HICON（走 PIDL）</summary>
    internal static Bitmap? ViaParsingNameIcon(string parsingName, int size)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (Native.SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _) != 0
                || pidl == IntPtr.Zero)
                return null;

            var shfi = new Native.SHFILEINFO();
            var r = Native.SHGetFileInfoPidl(pidl, 0, ref shfi,
                (uint)Marshal.SizeOf<Native.SHFILEINFO>(),
                Native.SHGFI_PIDL | Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
            if (r == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;

            try { return HIconToBitmap(shfi.hIcon, size); }
            finally { DestroyIcon(shfi.hIcon); }
        }
        catch { return null; }
        finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }

    public static Bitmap? ExtractV2(string path, int size, bool exists)
    {
        // ── ⓪ 虚拟项（此电脑 / 回收站 / 网络）：没有磁盘路径，走「解析名 → PIDL」──
        //
        // 不能落到下面几步：SHGetFileInfo 的字符串版认不出 ::{CLSID}，
        // 一路走到第 ③ 步会「按扩展名」拿到一个毫不相干的 .dat 图标，比取不到更误导。
        if (Core.VirtualItems.IsVirtual(path))
        {
            if (ShellIconIndexByParsingName(path, out int vIdx))
            {
                var byV = RenderFromIndex(vIdx, size, path);
                if (byV != null) return byV;
            }

            var byH = ViaParsingNameIcon(path, size);
            if (byH != null && !IsBlank(byH))
            {
                var (hBox, hFill) = Metrics(byH);
                var outp = Finish(byH, hBox, hFill, size);
                Retire(byH);
                Note(path, "SHGetFileInfo(PIDL·HICON)", 0, hFill);
                return outp;
            }
            Retire(byH);

            Note(path, "虚拟项取图标失败", 0, 0);
            return null;
        }

        string ext = Path.GetExtension(path);
        if (ext.Length == 0) ext = ".dat";

        // ── ① 系统 Imagelist：既能拿到真实图标，又是唯一能拿到大尺寸 + alpha 的途径 ──
        if (ShellIconIndex(path, exists, ext, out int index))
        {
            var byIndex = RenderFromIndex(index, size, path);
            if (byIndex != null) return byIndex;
        }

        // ── ② SHGetFileInfo 直接给 HICON（原样渲染后同样走高质量缩放）──
        var viaShell = ViaShellIconRaw(path, size, exists);
        if (viaShell is { } sh && !IsBlank(sh))
        {
            var (box, fill) = Metrics(sh);
            var outp = Finish(sh, box, fill, size);
            Retire(sh);
            Note(path, "SHGetFileInfo(HICON)", 0, fill);
            return outp;
        }
        Retire(viaShell);

        // ── ③ 按扩展名的通用图标 ──
        var generic = ViaShellIconRaw("__desklite_probe__" + ext, size, false);
        if (generic is { } ge && !IsBlank(ge))
        {
            var (box, fill) = Metrics(ge);
            var outp = Finish(ge, box, fill, size);
            Retire(ge);
            Note(path, "通用图标(按扩展名)", 0, fill);
            return outp;
        }
        Retire(generic);

        Note(path, "失败", 0, 0);
        return null;
    }

    /// <summary>内容包围盒 + 面积占比（与 <see cref="Cand"/> 里算的一致）</summary>
    private static (Rectangle Box, double Fill) Metrics(Bitmap bmp)
    {
        var box = ContentBox(bmp);
        double fill = box.IsEmpty ? 0
            : box.Width * (double)box.Height / (bmp.Width * (double)bmp.Height);
        return (box, fill);
    }

    /// <summary>
    /// 收尾两步：<see cref="Normalize"/>（修正「图形被画在大画布角落」的畸形资源）
    /// → <see cref="Scale"/>（缩到目标尺寸）。两者都能用诊断开关旁路，方便定位内存开销。
    ///
    /// <c>DESKLITE_NOSCALE=1</c> 时返回 <see cref="CopyExact"/> 的独立副本，
    /// 这样「每个图标一张缓存位图」的结构与正常流程一致，不会因为共用同一张池内位图
    /// 而把内存读数带偏。
    /// </summary>
    private static Bitmap Finish(Bitmap bmp, Rectangle box, double fill, int size)
    {
        CntScale++;

        var src = bmp;
        if (!DiagNoNormalize)
        {
            CntNormalize++;
            src = Normalize(bmp, box, fill);
        }

        Bitmap outp = DiagNoScale ? CopyExact(src) : Scale(src, size);
        if (!ReferenceEquals(src, bmp)) Retire(src);
        return outp;
    }

    /// <summary>取系统图标索引（SHGFI_SYSICONINDEX）。失败返回 false。</summary>
    internal static bool ShellIconIndex(string path, bool exists, string ext, out int index)
    {
        index = 0;
        var shfi = new Native.SHFILEINFO();
        uint attrs = 0;
        string probe = path;
        if (!exists)
        {
            probe = "__desklite_probe__" + ext;
            attrs = 0x80;
        }

        var r = Native.SHGetFileInfo(probe, attrs, ref shfi,
            (uint)Marshal.SizeOf<Native.SHFILEINFO>(), Native.SHGFI_SYSICONINDEX);
        if (r == IntPtr.Zero) return false;

        index = shfi.iIcon;
        _seenIndex.Add(index);
        return true;
    }

    /// <summary>从某一档 Imagelist 取图标，并「原样渲染」成位图（不做任何缩放）</summary>
    internal static Cand? FromImageList(int index, int shil)
    {
        var iid = Native.IID_IImageList;
        if (Native.SHGetImageList(shil, ref iid, out var il) != 0 || il == null) return null;

        if (shil >= 0 && shil < CntByShil.Length) CntByShil[shil]++;

        try
        {
            int lx = 0, ly = 0;
            il.GetIconSize(ref lx, ref ly);

            IntPtr h = IntPtr.Zero;
            if (il.GetIcon(index, Native.ILD_TRANSPARENT, ref h) != 0 || h == IntPtr.Zero) return null;
            try
            {
                var (w, hh) = HIconSize(h);
                if (w <= 0 || hh <= 0) return null;

                var bmp = RenderNative(h, w, hh);
                var box = ContentBox(bmp);
                double fill = box.IsEmpty
                    ? 0
                    : box.Width * (double)box.Height / (bmp.Width * (double)bmp.Height);
                return new Cand(shil, lx, w, fill, box, bmp);
            }
            finally { DestroyIcon(h); }
        }
        catch { return null; }
        finally { if (il != null) Marshal.ReleaseComObject(il); }
    }

    /// <summary>SHGetFileInfo(SHGFI_ICON|SHGFI_LARGEICON) → 原样渲染的位图</summary>
    internal static Bitmap? ViaShellIconRaw(string path, int size, bool exists)
    {
        var shfi = new Native.SHFILEINFO();
        uint flags = Native.SHGFI_ICON | Native.SHGFI_LARGEICON;
        uint attrs = 0;
        string probe = path;
        if (!exists)
        {
            var ext = Path.GetExtension(path);
            if (ext.Length == 0) ext = ".dat";
            probe = "__desklite_probe__" + ext;
            flags |= Native.SHGFI_USEFILEATTRIBUTES;
            attrs = 0x80;
        }

        var r = Native.SHGetFileInfo(probe, attrs, ref shfi,
            (uint)Marshal.SizeOf<Native.SHFILEINFO>(), flags);
        if (r == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;

        try
        {
            var (w, h) = HIconSize(shfi.hIcon);
            if (w <= 0 || h <= 0) w = h = size;
            return RenderNative(shfi.hIcon, w, h);
        }
        catch { return null; }
        finally { DestroyIcon(shfi.hIcon); }
    }

    /// <summary>查询 HICON 内部颜色位图的真实像素尺寸（0,0 表示查询失败）</summary>
    internal static (int W, int H) HIconSize(IntPtr hIcon)
    {
        var ii = new Native.ICONINFO();
        if (!Native.GetIconInfo(hIcon, ref ii)) return (0, 0);

        int w = 0, h = 0;
        try
        {
            if (ii.hbmColor != IntPtr.Zero)
            {
                var bm = new Native.BITMAP();
                if (Native.GetObject(ii.hbmColor, Marshal.SizeOf<Native.BITMAP>(), ref bm) > 0)
                {
                    w = bm.bmWidth;
                    h = bm.bmHeight;
                }
            }
            if (w <= 0 && ii.hbmMask != IntPtr.Zero)
            {
                var bm = new Native.BITMAP();
                if (Native.GetObject(ii.hbmMask, Marshal.SizeOf<Native.BITMAP>(), ref bm) > 0)
                {
                    w = bm.bmWidth;
                    h = bm.bmHeight / 2;      // 单色掩码是「图像 + AND 掩码」两倍高
                }
            }
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
        }
        return (w, h);
    }

    /// <summary>
    /// HICON **原样**（cx/cy = 位图真实尺寸，1:1 不缩放）渲染到 32bpp ARGB 位图。
    /// 这是整条管线能拿到干净原始像素的关键 —— 缩放一律不交给 GDI。
    ///
    /// 位图走 <see cref="Rent"/> 复用：图标提取是串行的，每一项都重新分配一张
    /// 「原样渲染图」会让 GDI+ 的私有堆一路涨到高点且不归还系统
    /// （实测 58 项要多占 10 MB 以上）。复用后同一尺寸只分配一次。
    /// </summary>
    internal static Bitmap RenderNative(IntPtr hIcon, int w, int h)
    {
        CntRender++;
        Bump(SzRender, Math.Max(w, h));
        var bmp = Rent(Math.Max(1, w), Math.Max(1, h));

        // 诊断：只留位图，连 Graphics 都不建 —— 隔离「Graphics.FromImage」自身的成本
        if (DiagNoGfx) return bmp;

        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);

        // 诊断：Graphics 建了但不画 —— 隔离「DrawIconEx 走 HDC」的成本
        if (DiagNoDrawIcon) return bmp;

        IntPtr hdc = g.GetHdc();
        try { Native.DrawIconEx(hdc, 0, 0, hIcon, w, h, 0, IntPtr.Zero, Native.DI_NORMAL); }
        finally { g.ReleaseHdc(hdc); }

        return bmp;
    }

    // ───────── GDI+ 中间位图复用池 ─────────
    //
    // 背景：GDI+（gdiplus.dll）用的是自己的私有堆，**只涨不缩**，释放的图不会把内存还给系统。
    // 而图标提取对每一项都要做「原样渲染 → 内容扫描 → 高质量缩放」，
    // 每次都 new 一张位图，58 项下来 GDI+ 的堆会稳定在高水位并长期常驻。
    //
    // 实测（关掉图标绘制做对照）：完整 32.0 MB → 不画图标 18.4 MB，
    // 其中 shell 取图标只占 3.1 MB，剩下 10.4 MB 全是 GDI+ 的渲染/扫描/缩放。
    //
    // 之所以能安全复用：图标提取是「串行」的（全部在 UI 线程、同一时刻只处理一项），
    // 且中间图的尺寸来自系统 Imagelist（32/48/256）与逐次减半，取值有限、不会无限增长。
    // 最终缓存下来的那张目标尺寸图仍然是**独立分配**的，不受池影响。

    [ThreadStatic] private static Dictionary<(int W, int H), Bitmap>? _pool;

    /// <summary>诊断用：关掉复用池（观察差异）。可用 <c>DESKLITE_NOPOOL=1</c> 覆盖</summary>
    internal static bool UsePool = !DiagNoPool;

    // 统计：真正落地的 Bitmap 分配次数（池命中不算）
    internal static readonly SortedDictionary<int, int> SzRentReal = new();

    private static Bitmap Rent(int w, int h)
    {
        if (!UsePool) { Bump(SzRentReal, Math.Max(w, h)); return new Bitmap(w, h, PixelFormat.Format32bppArgb); }

        var pool = _pool ??= new Dictionary<(int, int), Bitmap>();
        if (pool.TryGetValue((w, h), out var b) && b.Width == w && b.Height == h) return b;

        Bump(SzRentReal, Math.Max(w, h));
        b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        pool[(w, h)] = b;
        return b;
    }

    private static bool IsPooled(Bitmap b)
    {
        var pool = _pool;
        if (pool == null) return false;
        foreach (var v in pool.Values) if (ReferenceEquals(v, b)) return true;
        return false;
    }

    /// <summary>归还中间图：池里的不释放，池外的（独立分配）正常释放</summary>
    private static void Retire(Bitmap? b)
    {
        if (b == null) return;
        if (IsPooled(b)) return;
        b.Dispose();
    }

    /// <summary>
    /// 释放 GDI+ 中间位图复用池。
    ///
    /// 池只服务于「一轮连续的图标提取」（启动首屏那一轮，见上面对池的说明），
    /// 而它是 <c>static</c> 的 —— 提完之后这些中间图会一直挂在字典里占着 GDI+ 的
    /// 私有堆，且该堆**只涨不缩**，于是这块内存整个进程生命周期都收不回来。
    /// 首屏渲染完成后调用一次把它整个还掉即可；之后若再提取（用户改了图标尺寸等）
    /// 会重新建池，行为完全不变。
    ///
    /// <b>为什么安全</b>：<see cref="GetIcon"/> 对外交付的、以及缓存进字典的位图，
    /// 全部来自 <see cref="Scale"/> / <see cref="CopyExact"/> / <see cref="HIconToBitmap"/>
    /// 的**独立分配**（<see cref="Finish"/> 的返回值恒为新位图），
    /// 不存在「外部还持有一张池内位图」的情况，所以这里 Dispose 不会误伤。
    /// </summary>
    public static void ReleasePool()
    {
        var pool = _pool;
        if (pool == null) return;

        foreach (var b in pool.Values) { try { b.Dispose(); } catch { } }
        pool.Clear();
        _pool = null;
    }

    /// <summary>把位图逐像素精确复制一份（用于需要长期持有、而中间缓冲会被复用的场合）</summary>
    private static Bitmap CopyExact(Bitmap src)
    {
        Bump(SzCopy, src.Width);
        var nb = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sd = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var dd = nb.LockBits(new Rectangle(0, 0, nb.Width, nb.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int len = Math.Min(Math.Abs(sd.Stride), Math.Abs(dd.Stride)) * src.Height;
                var buf = ScanBuffer(len);
                Marshal.Copy(sd.Scan0, buf, 0, len);
                Marshal.Copy(buf, 0, dd.Scan0, len);
            }
            finally { nb.UnlockBits(dd); }
        }
        finally { src.UnlockBits(sd); }
        return nb;
    }

    /// <summary>
    /// 高质量缩放到正方形目标尺寸 —— **纯字节级面积平均（area / box filter）**。
    ///
    /// 为什么不用 GDI+ 的 <c>Graphics.DrawImage</c> + <c>HighQualityBicubic</c>：
    /// 实测 58 个图标走 GDI+ 缩放会在私有内存上多吃 10 MB 以上（每项约 180 KB，
    /// 而输出只有 6.4 KB），且把中间图改成复用池也降不下来 —— 说明开销来自
    /// GDI+ 每次绘制时创建的内部结构，而不在位图本身。
    ///
    /// 面积平均同时也是**缩小**场景下质量更好的做法：它是真正的低通滤波，
    /// 不会像双三次那样在细线边缘产生振铃与过冲。1.2×（48→40）时两者视觉几乎一致，
    /// 6.4×（256→40）时面积平均更干净。
    ///
    /// alpha 处理：在「预乘 alpha」空间做加权平均再还原，避免透明像素把边缘拖黑。
    /// </summary>
    internal static Bitmap Scale(Bitmap src, int size)
    {
        byte[] sbuf; int sstride, sh; bool topDown;
        try { (sbuf, sstride, sh, topDown) = Grab(src); }
        catch { return new Bitmap(size, size, PixelFormat.Format32bppArgb); }

        int sw = src.Width;
        int dw = Math.Max(1, size), dh = Math.Max(1, size);

        Bump(SzScale, size);
        var dst = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
        var dd = dst.LockBits(new Rectangle(0, 0, dw, dh), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int dstride = Math.Abs(dd.Stride);
            var outBuf = ScanBuffer2(dstride * dh);
            Array.Clear(outBuf, 0, dstride * dh);

            double sx = (double)sw / dw;
            double sy = (double)sh / dh;

            for (int dy = 0; dy < dh; dy++)
            {
                double y0 = dy * sy, y1 = y0 + sy;
                int iy0 = (int)Math.Floor(y0), iy1 = (int)Math.Ceiling(y1);
                int doff = dy * dstride;

                for (int dx = 0; dx < dw; dx++)
                {
                    double x0 = dx * sx, x1 = x0 + sx;
                    int ix0 = (int)Math.Floor(x0), ix1 = (int)Math.Ceiling(x1);

                    double aSum = 0, rSum = 0, gSum = 0, bSum = 0, wSum = 0;

                    for (int yy = iy0; yy < iy1 && yy < sh; yy++)
                    {
                        if (yy < 0) continue;
                        double wy = Math.Min(yy + 1, y1) - Math.Max(yy, y0);
                        if (wy <= 0) continue;
                        int srow = (topDown ? yy : sh - 1 - yy) * sstride;

                        for (int xx = ix0; xx < ix1 && xx < sw; xx++)
                        {
                            if (xx < 0) continue;
                            double wx = Math.Min(xx + 1, x1) - Math.Max(xx, x0);
                            if (wx <= 0) continue;

                            double w = wx * wy;
                            int i = srow + xx * 4;
                            double a = sbuf[i + 3];
                            if (a <= 0) { wSum += w; continue; }

                            // 预乘加权
                            bSum += sbuf[i] * a * w;
                            gSum += sbuf[i + 1] * a * w;
                            rSum += sbuf[i + 2] * a * w;
                            aSum += a * w;
                            wSum += w;
                        }
                    }

                    int o = doff + dx * 4;
                    if (aSum > 0 && wSum > 0)
                    {
                        // 非预乘还原：颜色 = Σ(c·a·w) / Σ(a·w)；alpha = Σ(a·w) / Σw
                        double inv = 1.0 / aSum;
                        outBuf[o] = Clamp(bSum * inv);          // B
                        outBuf[o + 1] = Clamp(gSum * inv);      // G
                        outBuf[o + 2] = Clamp(rSum * inv);      // R
                        outBuf[o + 3] = Clamp(aSum / wSum);     // A
                    }
                }
            }

            Marshal.Copy(outBuf, 0, dd.Scan0, dstride * dh);
        }
        finally { dst.UnlockBits(dd); }

        return dst;
    }

    private static byte Clamp(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)Math.Round(v);

    /// <summary>缩放输出用的复用缓冲（与扫描缓冲分开，避免互相覆盖）</summary>
    [ThreadStatic] private static byte[]? _scanOut;

    private static byte[] ScanBuffer2(int need)
    {
        var b = _scanOut;
        if (b == null || b.Length < need) b = _scanOut = new byte[Math.Max(need, 64 * 1024)];
        return b;
    }

    /// <summary>
    /// 像素扫描用的复用缓冲（每线程一份，只增不减）。
    /// 用 <c>LockBits + Marshal.Copy</c> 一次性拷出来再扫，比逐点 <c>GetPixel</c> 快约两个数量级，
    /// 而且整个过程只分配这一块缓冲、不产生逐点 interop 开销。
    /// </summary>
    [ThreadStatic] private static byte[]? _scan;

    private static byte[] ScanBuffer(int need)
    {
        var b = _scan;
        if (b == null || b.Length < need) b = _scan = new byte[Math.Max(need, 64 * 1024)];
        return b;
    }

    /// <summary>把位图拷到复用缓冲，返回 (缓冲, 行跨距, 高度, 逐行方向)。</summary>
    private static (byte[] buf, int stride, int h, bool topDown) Grab(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            bool topDown = stride > 0;
            int abs = Math.Abs(stride);
            var buf = ScanBuffer(abs * h);
            Marshal.Copy(data.Scan0, buf, 0, abs * h);
            return (buf, abs, h, topDown);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>有效内容（alpha &gt; 16）的包围盒；全透明返回 <see cref="Rectangle.Empty"/></summary>
    internal static Rectangle ContentBox(Bitmap bmp)
    {
        // 诊断：不扫像素，直接给一个「贴边铺满」的典型包围盒（与多数 app 图标一致），
        // 用来隔离 LockBits + Marshal.Copy 的成本。正确性由计数器自检
        // （NOLOCK 下的达标/未达标次数应与默认一致，否则本次对照作废）。
        if (DiagNoLock)
        {
            int ins = Math.Max(1, bmp.Width / 16);
            int side = Math.Max(1, bmp.Width - ins * 2);
            return new Rectangle(ins, ins, side, side);
        }

        byte[] buf; int stride, h; bool topDown;
        try { (buf, stride, h, topDown) = Grab(bmp); }
        catch { return Rectangle.Empty; }

        int w = bmp.Width;
        int minX = w, minY = h, maxX = -1, maxY = -1;

        for (int row = 0; row < h; row++)
        {
            int y = topDown ? row : h - 1 - row;
            int off = row * stride;
            for (int x = 0; x < w; x++)
            {
                if (buf[off + x * 4 + 3] <= 16) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>有效内容包围盒占整块画布的面积比</summary>
    internal static double Fill(Bitmap bmp)
    {
        var b = ContentBox(bmp);
        return b.IsEmpty ? 0 : b.Width * (double)b.Height / (bmp.Width * (double)bmp.Height);
    }

    /// <summary>内容是否贴住左 / 上边界（判定「图形被原样画在大画布角落」的关键特征）</summary>
    internal static bool IsCornerClipped(Rectangle box, Bitmap bmp)
        => box.Left <= Math.Max(1, bmp.Width / 8) && box.Top <= Math.Max(1, bmp.Height / 8);

    /// <summary>
    /// 修正畸形图标资源：有效图形只占大画布一小块、且贴着左上角 —— 这正是原实现里
    /// 「某些图标看起来非常小」的成因（资源没铺满画布，再叠加 GDI 不缩放就成了角落里的一点）。
    /// 这里按内容包围盒裁出有效区域，交回调用方继续缩放，使其与其他图标视觉等大。
    /// 未命中该特征时原样返回（不改变正常图标自己的留白）。
    /// </summary>
    private static Bitmap Normalize(Bitmap bmp, Rectangle box, double fillPct)
    {
        if (box.IsEmpty) return bmp;

        // 只处理「内容贴左上角 + 面积占比明显偏小」的畸形容器
        if (!IsCornerClipped(box, bmp)) return bmp;
        if (fillPct > 0.5) return bmp;

        int pad = Math.Max(1, (int)Math.Round(Math.Max(box.Width, box.Height) * 0.05));
        var crop = Rectangle.Intersect(
            Rectangle.Inflate(box, pad, pad),
            new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (crop.Width < 2 || crop.Height < 2) return bmp;

        Bump(SzNorm, crop.Width);
        var nb = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(nb))
        {
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(bmp, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
        }
        return nb;
    }

    // ───────── 旧方式一：系统 Imagelist（--icontest 对照用）─────────

    internal static Bitmap? ViaImageList(string path, int size, bool exists, int shil)
    {
        var shfi = new Native.SHFILEINFO();
        uint attrs = 0;
        string probe = path;
        if (!exists)
        {
            var ext = Path.GetExtension(path);
            if (ext.Length == 0) ext = ".dat";
            probe = "__desklite_probe__" + ext;
            attrs = 0x80;
        }

        var r = Native.SHGetFileInfo(probe, attrs, ref shfi,
            (uint)Marshal.SizeOf<Native.SHFILEINFO>(), Native.SHGFI_SYSICONINDEX);
        if (r == IntPtr.Zero) return null;

        var iid = Native.IID_IImageList;
        if (Native.SHGetImageList(shil, ref iid, out var il) != 0 || il == null) return null;

        try
        {
            IntPtr h = IntPtr.Zero;
            if (il.GetIcon(shfi.iIcon, Native.ILD_TRANSPARENT, ref h) != 0 || h == IntPtr.Zero)
                return null;
            try { return HIconToBitmap(h, size); }
            finally { DestroyIcon(h); }
        }
        catch { return null; }
        finally { if (il != null) Marshal.ReleaseComObject(il); }
    }

    // ───────── 方式二：SHGetFileInfo 直接给 HICON ─────────

    internal static Bitmap? ViaShellIcon(string path, int size, bool exists)
    {
        var shfi = new Native.SHFILEINFO();
        uint flags = Native.SHGFI_ICON | Native.SHGFI_LARGEICON;
        uint attrs = 0;
        string probe = path;
        if (!exists)
        {
            var ext = Path.GetExtension(path);
            if (ext.Length == 0) ext = ".dat";
            probe = "__desklite_probe__" + ext;
            flags |= Native.SHGFI_USEFILEATTRIBUTES;
            attrs = 0x80;
        }

        var r = Native.SHGetFileInfo(probe, attrs, ref shfi,
            (uint)Marshal.SizeOf<Native.SHFILEINFO>(), flags);
        if (r == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;

        try { return HIconToBitmap(shfi.hIcon, size); }
        catch { return null; }
        finally { DestroyIcon(shfi.hIcon); }
    }

    // ───────── HICON → 32bpp ARGB（保留 alpha）─────────

    private static Bitmap HIconToBitmap(IntPtr hIcon, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

            IntPtr hdc = g.GetHdc();
            try { Native.DrawIconEx(hdc, 0, 0, hIcon, size, size, 0, IntPtr.Zero, Native.DI_NORMAL); }
            finally { g.ReleaseHdc(hdc); }
        }
        return bmp;
    }

    /// <summary>判定「纯黑方块」：不透明且几乎全黑的像素占比过高即视为无效图标。</summary>
    internal static bool IsBlank(Bitmap bmp)
    {
        if (DiagNoLock) return false;     // 诊断：跳过扫描

        byte[] buf; int stride, h;
        try { (buf, stride, h, _) = Grab(bmp); }
        catch { return true; }

        int w = bmp.Width;
        int stepX = Math.Max(1, w / 24);
        int stepY = Math.Max(1, h / 24);

        int total = 0, black = 0;
        for (int row = 0; row < h; row += stepY)
        {
            int off = row * stride;
            for (int x = 0; x < w; x += stepX)
            {
                int i = off + x * 4;
                total++;
                // BGRA：i+3 是 alpha，i+2/i+1/i+0 是 R/G/B
                if (buf[i + 3] > 200 && buf[i + 2] < 26 && buf[i + 1] < 26 && buf[i] < 26) black++;
            }
        }
        // 采样点里超过 92% 是「不透明纯黑」→ 认为取到的是无效黑图
        return total > 0 && black * 100 / total > 92;
    }

    public void Dispose()
    {
        foreach (var v in _cache.Values) v.Dispose();
        foreach (var v in _byExt.Values) v.Dispose();
        _cache.Clear();
        _byExt.Clear();
    }
}

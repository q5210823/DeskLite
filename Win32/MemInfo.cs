using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskLite.Win32;

/// <summary>
/// 内存构成分析（VMMap 精简版）。
///
/// 目的：回答「这个进程的常驻内存到底花在哪儿」，而不是只看一个总数。
/// 做法：
///   ① <c>VirtualQuery</c> 遍历地址空间 → 按 状态/类型 把「已提交」内存分成
///      <b>映像(IMAGE) / 映射文件(MAPPED) / 私有(PRIVATE)</b> 三类；
///   ② <c>QueryWorkingSetEx</c> 逐页取 <c>Shared</c> 位 → 算出
///      <b>专用工作集（Private Working Set，即任务管理器「内存」列）</b>与共享工作集；
///   ③ 把映像页按模块基址归属到具体 DLL → 得到「每个模块占多少专用内存」；
///   ④ 再从 GC 侧读托管堆的真实尺寸（堆总量 / 已提交 / 各代 / 累计分配）。
///
/// 这样「36 MB 私有内存」就能被拆成可解释的几块，优化才有靶子。
/// </summary>
internal static class MemInfo
{
    // ───────── 系统 API ─────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WS_EX_INFO
    {
        public IntPtr VirtualAddress;
        public UIntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, ref MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool QueryWorkingSetEx(IntPtr hProcess, [Out] WS_EX_INFO[] pv, uint cb);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr hProcess, ref PROCESS_MEMORY_COUNTERS_EX ppsmemCounters, uint cb);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_PRIVATE = 0x20000;
    private const uint MEM_MAPPED = 0x40000;
    private const uint MEM_IMAGE = 0x1000000;

    /// <summary>对外暴露 MEM_PRIVATE，供差分工具筛选「私有块」</summary>
    public const uint TYPE_PRIVATE = MEM_PRIVATE;

    private const int CHUNK = 4096;              // QueryWorkingSetEx 每批页数
    private const long PAGE = 4096;

    // ───────── 结果模型 ─────────

    public sealed class ModuleRow
    {
        public string Name = "";
        public long PrivateWs;
        public long SharedWs;
    }

    public sealed class RegionRow
    {
        public string Label = "";
        public long Bytes;
        public uint Protect;
    }

    public sealed class RegionInfo
    {
        public long Base;
        public long Size;
        public uint Type;
        public uint Protect;
        public string Module = "";
        public string Desc =>
            $"0x{Base:X10}  {Size / 1048576.0,7:N1} MB  {TypeName(Type),-8} {ProtectName(Protect),-16} {Module}";
    }

    public static string TypeName(uint t) => t switch
    {
        MEM_IMAGE => "IMAGE",
        MEM_MAPPED => "MAPPED",
        MEM_PRIVATE => "PRIVATE",
        _ => $"0x{t:X}",
    };

    public static string ProtectName(uint p) => (p & 0xFF) switch
    {
        0x01 => "NOACCESS",
        0x02 => "R",
        0x04 => "RW",
        0x08 => "WC",
        0x10 => "X",
        0x20 => "RX",
        0x40 => "RWX",
        0x80 => "WCX",
        _ => $"0x{p:X}",
    };

    public sealed class Report
    {
        // 进程级
        public long WorkingSet, PrivateBytes, CommitBytes, PeakWorkingSet, PeakPrivate;

        // 工作集按共享性拆分（专用工作集 = 任务管理器「内存」列）
        public long WsPrivate, WsShared;

        // 已提交虚拟内存按类型
        public long CommitPrivate, CommitImage, CommitMapped;

        // 托管堆
        public long GcHeapBytes, GcCommitted, GcFragmented, GcAllocatedTotal;
        public long[] GcGen = new long[4];

        public int Threads, Handles;
        public uint Gdi, User;

        public readonly List<ModuleRow> Modules = new();
        public readonly List<RegionRow> PrivateRegions = new();
        public readonly List<RegionInfo> TopRegions = new();
        public int ModuleCount;
        public int WsQueryError;
    }

    // ───────── 采集 ─────────

    /// <summary>
    /// 只读「工作集 / 专用字节」两个计数器 —— 一次 <c>GetProcessMemoryInfo</c>，
    /// 不建 Process 对象、不枚举线程与模块。供空闲巡检反复调用而不产生额外负担。
    /// </summary>
    public static (long WorkingSet, long Private) Counters()
    {
        try
        {
            var pmc = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>() };
            if (GetProcessMemoryInfo(GetCurrentProcess(), ref pmc, pmc.cb))
                return ((long)pmc.WorkingSetSize, (long)pmc.PrivateUsage);
        }
        catch { }
        return (0, 0);
    }

    /// <summary>轻量快照：只读进程计数器 + GC 统计，不遍历地址空间。用于高频埋点。</summary>
    public static (long Priv, long Ws, long GcCommit, long GcHeap, int Thr, int Handles, uint Gdi, int Mods) Quick()
    {
        long priv = 0, ws = 0;
        int thr = 0, handles = 0, mods = 0;
        try
        {
            var pmc = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>() };
            if (GetProcessMemoryInfo(GetCurrentProcess(), ref pmc, pmc.cb))
            {
                priv = (long)pmc.PrivateUsage;
                ws = (long)pmc.WorkingSetSize;
            }
        }
        catch { }

        try
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            thr = p.Threads.Count;
            handles = p.HandleCount;
            try { mods = p.Modules.Count; } catch { }
        }
        catch { }

        long gcCommit = 0, gcHeap = 0;
        try
        {
            var gi = GC.GetGCMemoryInfo();
            gcCommit = gi.TotalCommittedBytes;
            gcHeap = gi.HeapSizeBytes;
        }
        catch { }

        return (priv, ws, gcCommit, gcHeap, thr, handles, SafeGui(Native.GR_GDIOBJECTS), mods);
    }

    public static Report Snapshot()
    {
        var r = new Report();

        // ① 进程计数器（与任务管理器同口径）
        try
        {
            var pmc = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>() };
            if (GetProcessMemoryInfo(GetCurrentProcess(), ref pmc, pmc.cb))
            {
                r.WorkingSet = (long)pmc.WorkingSetSize;
                r.PeakWorkingSet = (long)pmc.PeakWorkingSetSize;
                r.PrivateBytes = (long)pmc.PrivateUsage;
                r.CommitBytes = (long)pmc.PagefileUsage;
                r.PeakPrivate = (long)pmc.PeakPagefileUsage;
            }
        }
        catch { }

        // ② 模块表（用于把映像页归属到 DLL）
        var mods = new List<(IntPtr Base, long Size, string Name)>();
        try
        {
            using var p = Process.GetCurrentProcess();
            foreach (ProcessModule m in p.Modules)
            {
                mods.Add((m.BaseAddress, m.ModuleMemorySize, m.ModuleName));
                r.ModuleCount++;
            }
            mods.Sort((a, b) => a.Base.ToInt64().CompareTo(b.Base.ToInt64()));

            r.Threads = p.Threads.Count;
            r.Handles = p.HandleCount;
        }
        catch { }

        r.Gdi = SafeGui(Native.GR_GDIOBJECTS);
        r.User = SafeGui(Native.GR_USEROBJECTS);

        // ③ 托管堆
        try
        {
            var gi = GC.GetGCMemoryInfo();
            r.GcHeapBytes = gi.HeapSizeBytes;
            r.GcCommitted = gi.TotalCommittedBytes;
            r.GcFragmented = gi.FragmentedBytes;

            var gens = gi.GenerationInfo;
            for (int i = 0; i < gens.Length && i < 4; i++) r.GcGen[i] = gens[i].SizeAfterBytes;

            r.GcAllocatedTotal = GC.GetTotalAllocatedBytes(false);
        }
        catch { }

        // ④ 遍历地址空间
        var moduleAgg = new Dictionary<string, ModuleRow>(StringComparer.OrdinalIgnoreCase);
        var privAgg = new Dictionary<string, RegionRow>(StringComparer.OrdinalIgnoreCase);

        IntPtr addr = IntPtr.Zero;
        var mbi = new MEMORY_BASIC_INFORMATION();
        int guard = 0;

        while (guard++ < 2_000_000)
        {
            if (VirtualQuery(addr, ref mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero) break;

            long size = mbi.RegionSize.ToInt64();
            if (size <= 0) break;

            if (mbi.State == MEM_COMMIT)
            {
                if (mbi.Type == MEM_IMAGE) r.CommitImage += size;
                else if (mbi.Type == MEM_MAPPED) r.CommitMapped += size;
                else if (mbi.Type == MEM_PRIVATE) r.CommitPrivate += size;

                // 逐页取共享位
                var (priv, shared) = PageShares(mbi.BaseAddress, size);
                r.WsPrivate += priv;
                r.WsShared += shared;

                if (size >= 512 * 1024)
                    r.TopRegions.Add(new RegionInfo
                    {
                        Base = mbi.BaseAddress.ToInt64(),
                        Size = size,
                        Type = mbi.Type,
                        Protect = mbi.Protect,
                        Module = mbi.Type == MEM_IMAGE ? ModuleOf(mods, mbi.BaseAddress) : "",
                    });

                if (mbi.Type == MEM_IMAGE)
                {
                    string name = ModuleOf(mods, mbi.BaseAddress);
                    if (!moduleAgg.TryGetValue(name, out var row))
                        moduleAgg[name] = row = new ModuleRow { Name = name };
                    row.PrivateWs += priv;
                    row.SharedWs += shared;
                }
                else if (priv > 0)
                {
                    // 私有区域的归组：按保护属性分类，并标注大块
                    string label = (mbi.Type == MEM_PRIVATE)
                        ? (IsExec(mbi.Protect) ? "私有·可执行(JIT/原生代码)" : "私有·数据(堆/GC/栈)")
                        : "映射文件";
                    if (!privAgg.TryGetValue(label, out var rv))
                        privAgg[label] = rv = new RegionRow { Label = label, Protect = mbi.Protect };
                    rv.Bytes += priv;
                }
            }

            long next = mbi.BaseAddress.ToInt64() + size;
            if (next <= addr.ToInt64()) break;
            addr = (IntPtr)next;
        }

        r.Modules.AddRange(moduleAgg.Values);
        r.Modules.Sort((a, b) => (b.PrivateWs + b.SharedWs).CompareTo(a.PrivateWs + a.SharedWs));
        r.PrivateRegions.AddRange(privAgg.Values);
        r.PrivateRegions.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        r.TopRegions.Sort((a, b) => b.Size.CompareTo(a.Size));
        r.WsQueryError = LastQueryError;

        return r;
    }

    private static uint SafeGui(uint flag)
    {
        try { return Native.GetGuiResources(GetCurrentProcess(), flag); } catch { return 0; }
    }

    private static bool IsExec(uint protect)
    {
        uint p = protect & 0xFF;
        return p == 0x10 || p == 0x20 || p == 0x40 || p == 0x80;   // EXECUTE*
    }

    private static string ModuleOf(List<(IntPtr Base, long Size, string Name)> mods, IntPtr addr)
    {
        long a = addr.ToInt64();
        int lo = 0, hi = mods.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            long b = mods[mid].Base.ToInt64();
            if (b <= a) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (best < 0) return "(未归属映像)";
        var m = mods[best];
        return a < m.Base.ToInt64() + m.Size ? m.Name : "(未归属映像)";
    }

    /// <summary>对 [base, base+size) 逐页取共享位，返回 (专用字节数, 共享字节数)。</summary>
    private static int LastQueryError;

    private static IntPtr _wsHandle = IntPtr.Zero;

    /// <summary>
    /// 拿一个可用于 QueryWorkingSetEx 的进程句柄。
    /// 伪句柄 <c>GetCurrentProcess()</c> 在 psapi 的这类调用上会返回 ERROR_INVALID_HANDLE(6)，
    /// 因此优先用 OpenProcess 拿真实句柄，失败再退回 Process.Handle。
    /// </summary>
    private static IntPtr WsHandle()
    {
        if (_wsHandle != IntPtr.Zero) return _wsHandle;

        try
        {
            int pid = Environment.ProcessId;
            var h = Native.OpenProcess(0x0400 | 0x0010, false, (uint)pid);   // QUERY_INFORMATION | VM_READ
            if (h != IntPtr.Zero) { _wsHandle = h; return _wsHandle; }
        }
        catch { }

        try { _wsHandle = Process.GetCurrentProcess().Handle; } catch { }
        if (_wsHandle == IntPtr.Zero) _wsHandle = GetCurrentProcess();
        return _wsHandle;
    }

    /// <summary>自检：对一块已知已提交的地址做一次单页查询，返回诊断文字。</summary>
    public static string SelfTest()
    {
        var mbi = new MEMORY_BASIC_INFORMATION();
        if (VirtualQuery(IntPtr.Zero, ref mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero)
            return "VirtualQuery 失败";

        // 找第一块已提交的私有内存
        IntPtr addr = IntPtr.Zero;
        for (int i = 0; i < 100000; i++)
        {
            if (VirtualQuery(addr, ref mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero) break;
            if (mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE)
            {
                var buf = new WS_EX_INFO[1];
                buf[0].VirtualAddress = mbi.BaseAddress;
                bool ok = QueryWorkingSetEx(WsHandle(), buf, (uint)Marshal.SizeOf<WS_EX_INFO>());
                return ok
                    ? $"OK  句柄=0x{WsHandle():X}  addr=0x{mbi.BaseAddress:X}  attr=0x{(ulong)buf[0].Attributes:X}  valid={((ulong)buf[0].Attributes & 1)}"
                    : $"FAIL 句柄=0x{WsHandle():X}  err={Marshal.GetLastWin32Error()}";
            }
            long next = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
            if (next <= addr.ToInt64()) break;
            addr = (IntPtr)next;
        }
        return "未找到可测的私有已提交页";
    }

    /// <summary>
    /// 判断一块内存「是否真的被写过」：在整个区域上均匀取 16 个 4KB 页，统计零字节比例。
    ///
    /// 用途：区分两种完全不同的内存 ——
    ///   · <b>全零</b>：已提交但从未被访问过的预留页。它只占「提交额度 / 页面文件」，
    ///     不占物理内存，任务管理器里也看不到它；
    ///   · <b>有内容</b>：真正被用掉的内存，会实打实占物理页。
    /// </summary>
    public static string ProbeRegion(long baseAddr, long size)
    {
        const int page = 4096;
        long pages = Math.Max(1, size / page);
        int samples = (int)Math.Min(16, pages);

        var buf = new byte[page];
        int totalBytes = 0, zeroBytes = 0, okPages = 0;
        var head = new StringBuilder();

        for (int i = 0; i < samples; i++)
        {
            long off = samples == 1 ? 0 : i * (pages - 1) / (samples - 1) * page;
            long addr = baseAddr + off;
            if (!Native.ReadProcessMemory(WsHandle(), (IntPtr)addr, buf, (IntPtr)page, out var read)) continue;

            int n = (int)read.ToInt64();
            okPages++;
            totalBytes += n;
            for (int k = 0; k < n; k++) if (buf[k] == 0) zeroBytes++;
            if (i == 0)
                for (int k = 0; k < Math.Min(16, n); k++) head.Append(buf[k].ToString("X2"));
        }

        double ratio = totalBytes == 0 ? 0 : zeroBytes * 100.0 / totalBytes;
        string verdict = ratio > 99.5 ? "→ 全零：未触碰的预留，不占物理内存"
                       : ratio > 50 ? "→ 大部分未用"
                       : "→ 已实际使用";
        return $"采样 {okPages}/{samples} 页 · 零字节 {ratio:N1}%  {verdict} · 首16B {head}";
    }

    private static (long priv, long shared) PageShares(IntPtr baseAddr, long size)
    {
        long pages = (size + PAGE - 1) / PAGE;
        if (pages <= 0) return (0, 0);

        long priv = 0, shared = 0;
        var buf = new WS_EX_INFO[CHUNK];

        for (long i = 0; i < pages; i += CHUNK)
        {
            int n = (int)Math.Min(CHUNK, pages - i);
            for (int k = 0; k < n; k++)
            {
                buf[k].VirtualAddress = (IntPtr)(baseAddr.ToInt64() + (i + k) * PAGE);
                buf[k].Attributes = UIntPtr.Zero;
            }

            uint cb = (uint)(n * Marshal.SizeOf<WS_EX_INFO>());
            if (!QueryWorkingSetEx(WsHandle(), buf, cb))
            {
                // 整批失败就跳过（不计入工作集），并记下首个错误码
                if (LastQueryError == 0) LastQueryError = Marshal.GetLastWin32Error();
                continue;
            }

            for (int k = 0; k < n; k++)
            {
                ulong a = (ulong)buf[k].Attributes;
                if ((a & 1) == 0) continue;                 // bit0 = Valid（不在工作集）
                if ((a & (1UL << 15)) != 0) shared += PAGE; // bit15 = Shared
                else priv += PAGE;
            }
        }
        return (priv, shared);
    }

    // ───────── 文本报告 ─────────

    public static string Format(Report r)
    {
        static string M(long b) => $"{b / 1048576.0:N1} MB";
        static string K(long b) => b < 1048576 ? $"{b / 1024.0:N0} KB" : M(b);

        var sb = new System.Text.StringBuilder();

        sb.AppendLine("── 进程内存（与任务管理器同口径）──");
        sb.AppendLine($"  专用工作集(任务管理器「内存」): {M(r.WsPrivate)}");
        sb.AppendLine($"  共享工作集                    : {M(r.WsShared)}");
        sb.AppendLine($"  工作集合计                    : {M(r.WorkingSet)}");
        sb.AppendLine($"  提交内存(Commit/Pagefile)      : {M(r.CommitBytes)}");
        sb.AppendLine($"  私有内存(Private Bytes)        : {M(r.PrivateBytes)}");
        sb.AppendLine($"  峰值                            : 工作集 {M(r.PeakWorkingSet)} · 提交 {M(r.PeakPrivate)}");
        sb.AppendLine();

        sb.AppendLine("── 已提交虚拟内存按类型 ──");
        sb.AppendLine($"  私有(PRIVATE)  : {M(r.CommitPrivate)}   ← GC 堆 + 原生堆 + 线程栈 + JIT 代码");
        sb.AppendLine($"  映像(IMAGE)    : {M(r.CommitImage)}   ← 已加载 DLL（多数是共享页）");
        sb.AppendLine($"  映射(MAPPED)   : {M(r.CommitMapped)}");
        sb.AppendLine();

        sb.AppendLine("── 托管堆（GC 视角）──");
        sb.AppendLine($"  存活对象        : {K(r.GcHeapBytes)}");
        sb.AppendLine($"  GC 已提交       : {K(r.GcCommitted)}");
        sb.AppendLine($"  碎片            : {K(r.GcFragmented)}");
        sb.AppendLine($"  代际 Gen0/1/2   : {K(r.GcGen[0])} / {K(r.GcGen[1])} / {K(r.GcGen[2])}");
        sb.AppendLine($"  大对象堆 LOH    : {K(r.GcGen.Length > 3 ? r.GcGen[3] : 0)}");
        sb.AppendLine($"  累计分配总量    : {M(r.GcAllocatedTotal)}");
        sb.AppendLine();

        sb.AppendLine($"── 已加载模块 {r.ModuleCount} 个（按占用排序，前 12）──");
        sb.AppendLine($"  {"模块",-30} {"专用WS",10} {"共享WS",10}");
        foreach (var m in r.Modules.Take(12))
            sb.AppendLine($"  {Trim(m.Name, 30),-30} {M(m.PrivateWs),10} {M(m.SharedWs),10}");
        sb.AppendLine();

        sb.AppendLine("── 非映像私有工作集归组 ──");
        foreach (var p in r.PrivateRegions)
            sb.AppendLine($"  {p.Label,-28} {M(p.Bytes),10}");
        sb.AppendLine();

        sb.AppendLine($"── 其他 ──");
        sb.AppendLine($"  线程 {r.Threads} · 句柄 {r.Handles} · GDI {r.Gdi} · USER {r.User}");
        if (r.WsQueryError != 0)
            sb.AppendLine($"  ⚠ QueryWorkingSetEx 失败 (Win32 错误 {r.WsQueryError})，共享/专用工作集不可信");

        if (r.TopRegions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("── 最大的已提交内存块（≥512KB，前 18）──");
            foreach (var x in r.TopRegions.Take(18))
                sb.AppendLine("  " + x.Desc);
        }

        return sb.ToString();
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}

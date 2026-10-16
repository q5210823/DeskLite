using System.Drawing.Drawing2D;

namespace DeskLite.UI;

/// <summary>
/// 共用的绘制几何工具。
///
/// 抽出来的原因很实际：圆角矩形路径原先在四处各写了一遍
/// （<c>BoxForm.RoundRect</c>、<c>BoxView.Round</c>、设置窗与搜索窗的 <c>OnResize</c>、
/// 以及 Program.cs 里的离屏预览），实现完全一致 —— 改一处就得记得改四处，容易漏。
/// </summary>
internal static class Shapes
{
    /// <summary>
    /// 圆角矩形路径。<paramref name="radius"/> 是**圆角半径**，内部会乘 2 得到 GDI+ 需要的直径；
    /// 矩形太小放不下圆角时退化成直角矩形（避免产生畸形路径）。
    /// </summary>
    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();

        int d = Math.Max(2, radius * 2);
        if (r.Width < d || r.Height < d)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// 给窗口套圆角 <see cref="System.Drawing.Region"/>，并保证**上一个 Region 被释放**。
    ///
    /// <b>为什么需要这个 helper</b>：<c>Control.Region</c> 的 setter 只把引用存进属性表
    /// （内部再 <c>GetHrgn</c> 交给 <c>SetWindowRgn</c>，那个 HRGN 由系统接管），
    /// 它**不会**替调用方释放上一个 <c>Region</c> 对象。而原来的写法
    /// <c>Region = new Region(path)</c> 直接挂在 <c>OnResize</c> 上 ——
    /// 拖动窗口改一次尺寸会连续触发几十次 WM_SIZE / OnResize，每次都新建一个 Region，
    /// 旧的就此失去引用、只能等 GC 走终结器。
    ///
    /// <b>实测口径（别被"GDI 泄漏"的说法带偏）</b>：<c>System.Drawing.Region</c> 底层是
    /// GDI+ 的 <c>GpRegion</c>，<b>不计入 GDI 句柄表</b> ——
    /// <c>GetGuiResources(GR_GDIOBJECTS)</c> 全程恒定（实测 20000 次改尺寸前后都是 41），
    /// 而且它有终结器兜底，所以这是**释放不及时**，不是永久泄漏。
    /// 用 <c>--gdiresize</c> 做过带/不带 Region 的分进程对照：扣掉「改窗口尺寸本身」
    /// 的开销后，Region 的净成本在噪声级（约 1.4 MB / 20000 次改尺寸）。
    ///
    /// 那还改它干什么：把「等 GC 来决定什么时候还」换成「换下去就立刻还」，
    /// 少一批对象排在终结器队列里，所有权也一眼看得出 —— 常驻程序的资源账
    /// 应该是确定的，不该看 GC 心情。
    ///
    /// <paramref name="holder"/> 由调用方持有，用来记住「当前装上去的那个」；
    /// 传 <paramref name="radius"/> = 0 表示不需要圆角（分层窗口自带按 alpha 的裁剪），
    /// 此时同样会把旧的那个释放掉。
    ///
    /// 顺序是「先赋新、再放旧」——中间不留「旧 Region 已释放、新 Region 还没装上」的空档，
    /// 否则窗口会闪一下直角边框。
    /// </summary>
    public static void ApplyRoundedRegion(Control c, int radius, ref Region? holder)
    {
        Region? fresh = null;
        if (radius > 0)
        {
            using var path = RoundedPath(new Rectangle(0, 0, c.Width, c.Height), radius);
            fresh = new Region(path);
        }

        c.Region = fresh;
        holder?.Dispose();
        holder = fresh;
    }
}

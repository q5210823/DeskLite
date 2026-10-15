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
}

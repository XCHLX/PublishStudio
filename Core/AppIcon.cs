using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PublishStudio.Core;

/// <summary>
/// 运行时画一个托盘图标，省掉外部 .ico 文件依赖。
/// 蓝底圆角方块 + 白色向上箭头 = 发布/上传。
/// </summary>
public static class AppIcon
{
    /// <summary>销毁用 <c>Bitmap.GetHicon</c> 拿到的原生图标句柄，避免 GDI 句柄泄漏。</summary>
    /// <param name="handle">要销毁的图标句柄。</param>
    /// <returns>成功返回 true（这里不关心结果）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>生成 32x32 图标。调用方负责 Dispose。</summary>
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var body = RoundedRect(1, 1, 30, 30, 8);
            using var fill = new SolidBrush(ColorTranslator.FromHtml("#0078D4"));
            g.FillPath(fill, body);

            // 箭头：三角箭身 + 竖杆，16px 缩放下也能认出是「往上发」。
            using var arrow = new GraphicsPath();
            arrow.AddPolygon(new[]
            {
                new PointF(16, 6.5f), new PointF(25, 16.5f), new PointF(7, 16.5f),
            });
            arrow.AddRectangle(new RectangleF(13, 15, 6, 10.5f));
            g.FillPath(Brushes.White, arrow);
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Icon.FromHandle 不接管句柄，Clone 一份出来再销毁原句柄，避免 GDI 泄漏。
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>画一个圆角矩形路径（四个角用 90° 圆弧拼出来），供填充图标底色。</summary>
    /// <param name="x">左上角 X 坐标。</param>
    /// <param name="y">左上角 Y 坐标。</param>
    /// <param name="width">矩形宽度。</param>
    /// <param name="height">矩形高度。</param>
    /// <param name="radius">圆角半径。</param>
    /// <returns>可直接交给 <c>Graphics.FillPath</c> 的路径，调用方负责释放。</returns>
    private static GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        path.AddArc(x, y, radius, radius, 180, 90);
        path.AddArc(x + width - radius, y, radius, radius, 270, 90);
        path.AddArc(x + width - radius, y + height - radius, radius, radius, 0, 90);
        path.AddArc(x, y + height - radius, radius, radius, 90, 90);
        path.CloseFigure();
        return path;
    }
}

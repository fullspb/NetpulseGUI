using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Microsoft.Win32;
using NetpulseGUI.Helpers;

namespace NetpulseGUI.Services;

/// <summary>
/// Рисует иконку трея с двумя строками скорости (↓ сверху, ↑ снизу).
/// Размер берётся из системных метрик для текущего DPI основного монитора (16/20/24/32 px…),
/// цифры масштабируются под ширину иконки, цвета — под светлую/тёмную панель задач.
/// Вызывающий обязан освободить HICON через DestroyIcon (см. <see cref="TrayService"/>).
/// </summary>
public sealed class TrayIconRenderer : IDisposable
{
    private readonly FontFamily _family;
    private bool _lightTaskbar;
    private DateTime _themeCheckedUtc = DateTime.MinValue;

    public TrayIconRenderer()
    {
        _family = TryFamily("Segoe UI Variable Display") ?? TryFamily("Segoe UI") ?? FontFamily.GenericSansSerif;
    }

    private static FontFamily? TryFamily(string name)
    {
        try { return new FontFamily(name); }
        catch (ArgumentException) { return null; }
    }

    public int CurrentSize { get; private set; }

    /// <summary>Создаёт HICON. Владение handle переходит к вызывающему.</summary>
    public IntPtr Render(string down, string up, bool paused)
    {
        var dpi = NativeMethods.GetPrimaryMonitorDpi();
        var size = 16;
        try { size = Math.Max(16, NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi)); }
        catch (EntryPointNotFoundException) { size = (int)Math.Round(16 * dpi / 96.0); }
        CurrentSize = size;

        RefreshTaskbarTheme();
        var downColor = paused ? Color.Gray : _lightTaskbar ? Color.FromArgb(0, 95, 184) : Color.FromArgb(96, 205, 255);
        var upColor = paused ? Color.Gray : _lightTaskbar ? Color.FromArgb(196, 82, 0) : Color.FromArgb(255, 178, 92);

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var half = size / 2f;
            DrawLine(g, down, new RectangleF(0, 0, size, half), downColor);
            DrawLine(g, up, new RectangleF(0, half, size, half), upColor);
        }
        return bmp.GetHicon();
    }

    /// <summary>Рисует строку контуром, вписывая её в прямоугольник с максимальной высотой.</summary>
    private void DrawLine(Graphics g, string text, RectangleF box, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var path = new GraphicsPath();
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        path.AddString(text, _family, (int)FontStyle.Bold, 100f, PointF.Empty, fmt);
        var b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;

        // Цифры занимают почти всю высоту строки; ширину сжимаем, если текст не помещается.
        var padY = Math.Max(0.5f, box.Height * 0.06f);
        var targetH = box.Height - padY * 2;
        var scaleY = targetH / b.Height;
        var scaleX = Math.Min(scaleY * 1.05f, (box.Width - 0.5f) / b.Width);

        using var m = new Matrix();
        var w = b.Width * scaleX;
        m.Translate(box.X + (box.Width - w) / 2f, box.Y + padY);
        m.Scale(scaleX, scaleY);
        m.Translate(-b.X, -b.Y);
        path.Transform(m);

        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private void RefreshTaskbarTheme()
    {
        if (DateTime.UtcNow - _themeCheckedUtc < TimeSpan.FromSeconds(30)) return;
        _themeCheckedUtc = DateTime.UtcNow;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            _lightTaskbar = key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch (Exception)
        {
            _lightTaskbar = false;
        }
    }

    public void InvalidateTheme() => _themeCheckedUtc = DateTime.MinValue;

    public void Dispose() => _family.Dispose();
}

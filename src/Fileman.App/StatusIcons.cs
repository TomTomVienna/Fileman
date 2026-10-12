using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Fileman.Core.Automation;

namespace Fileman.App;

/// <summary>
/// Zeichnet die Tray-Symbole zur Laufzeit (farbiger Kreis mit "F"), damit keine Bilddateien gepflegt werden müssen.
/// grün = alles gesichert, blau = Änderungen/Sicherung läuft, gelb = Platte fehlt, rot = Fehler, grau = pausiert/nicht eingerichtet.
/// </summary>
internal static partial class StatusIcons
{
    private static readonly Dictionary<Color, Icon> Cache = [];

    public static Color ColorFor(ServiceStatus status) => status switch
    {
        ServiceStatus.Idle => Color.FromArgb(46, 160, 67),
        ServiceStatus.Pending or ServiceStatus.Running => Color.FromArgb(31, 111, 235),
        ServiceStatus.TargetMissing => Color.FromArgb(219, 160, 0),
        ServiceStatus.Error => Color.FromArgb(207, 34, 46),
        _ => Color.FromArgb(120, 120, 120),
    };

    public static Icon For(ServiceStatus status)
    {
        var color = ColorFor(status);
        if (Cache.TryGetValue(color, out var icon))
            return icon;

        var size = SystemInformation.SmallIconSize.Width >= 24 ? 32 : 16;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var fill = new SolidBrush(color);
            using var border = new Pen(ControlPaint.Dark(color), size / 16f);
            g.FillEllipse(fill, 0.5f, 0.5f, size - 1.5f, size - 1.5f);
            g.DrawEllipse(border, 0.5f, 0.5f, size - 1.5f, size - 1.5f);
            using var font = new Font("Segoe UI", size * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            var text = g.MeasureString("F", font);
            g.DrawString("F", font, Brushes.White, (size - text.Width) / 2 + size / 32f, (size - text.Height) / 2);
        }
        var handle = bitmap.GetHicon();
        icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        Cache[color] = icon;
        return icon;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace T4Power.Ui;

/// <summary>
/// Draws the tray icon at runtime instead of shipping .ico files. It keeps binary assets out of
/// the repo, and lets the icon carry live information: the ring colour is the profile and the
/// fill height is power draw against the cap, so the tray tells you the state at a glance.
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);

    static readonly Color Eco = Color.FromArgb(0x76, 0xB9, 0x00);   // NVIDIA green
    static readonly Color Mid = Color.FromArgb(0xE8, 0xA3, 0x3D);
    static readonly Color Hot = Color.FromArgb(0xD2, 0x4B, 0x4B);
    static readonly Color Off = Color.FromArgb(0x9A, 0xA0, 0xA6);

    /// <summary>The T, matching the application icon so the tray and the logo read as one thing.</summary>
    static readonly Color Letter = Color.FromArgb(0xFF, 0xCC, 0x33);

    /// <param name="load">Power draw as a fraction of the current cap, 0..1. Null when unknown.</param>
    /// <param name="available">False when the service is unreachable, which greys the icon.</param>
    public static Icon Create(double? load, bool available)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var fraction = Math.Clamp(load ?? 0, 0, 1);
            var colour = !available ? Off
                : fraction >= 0.85 ? Hot
                : fraction >= 0.5 ? Mid
                : Eco;

            // Same open dial as the application icon: 270 degrees with the gap at the bottom.
            const float stroke = 4f;
            const float inset = stroke / 2f + 1.5f;
            var rect = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
            const float start = 135f;
            const float sweep = 270f;

            using (var track = new Pen(Color.FromArgb(80, colour), stroke)
                   { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(track, rect, start, sweep);

            // The filled portion is the live reading: draw against the cap, so the tray shows
            // how hard the card is working, not just which profile is set.
            if (available && fraction > 0.01)
            {
                using var arc = new Pen(colour, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, rect, start, sweep * (float)fraction);
            }

            // Greyed out with the rest when the service is unreachable, so a dead service is
            // obvious at a glance rather than looking like an idle GPU.
            using var brush = new SolidBrush(available ? Letter : Off);
            const float barW = size * 0.40f;
            const float barH = size * 0.12f;
            const float stemW = size * 0.14f;
            const float stemH = size * 0.40f;
            const float top = size * 0.30f;

            g.FillRectangle(brush, (size - barW) / 2f, top, barW, barH);
            g.FillRectangle(brush, (size - stemW) / 2f, top, stemW, stemH);
        }

        // Icon.FromHandle does not own the handle, so clone and release it immediately to avoid
        // leaking a GDI handle on every refresh.
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}

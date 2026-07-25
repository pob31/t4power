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

            // Track ring.
            using (var track = new Pen(Color.FromArgb(70, colour), 4f))
                g.DrawEllipse(track, 3, 3, size - 7, size - 7);

            // Fill proportional to load, sweeping clockwise from the top.
            if (available && fraction > 0.01)
            {
                using var arc = new Pen(colour, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, 3, 3, size - 7, size - 7, -90, (float)(360 * fraction));
            }

            using var centre = new SolidBrush(colour);
            g.FillEllipse(centre, size / 2 - 4, size / 2 - 4, 8, 8);
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

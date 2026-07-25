using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Generates src/T4Power/Assets/t4power.ico — the application logo.
//
// A gauge ring matching the tray icon, with a yellow T over it. The tray icon is drawn at
// runtime, but Windows needs a real .ico embedded in the executable for Explorer, the taskbar
// and the title bar, so this one has to exist as a file.
//
//     dotnet run --project tools/IconGen
//
// Written in C# rather than a shell script because an .ico is a binary container and the byte
// layout has to be exact.

var repoRoot = FindRepoRoot();
var outPath = Path.Combine(repoRoot, "src", "T4Power", "Assets", "t4power.ico");
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

// Matching the tray icon and the window's card titles.
var track = Color.FromArgb(255, 60, 66, 77);
var arc = Color.FromArgb(255, 118, 185, 0);      // NVIDIA green
var letter = Color.FromArgb(255, 255, 204, 51);  // the yellow used for GPU names

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var entries = new List<(int Size, byte[] Data)>();

foreach (var size in sizes)
{
    using var bitmap = Render(size);
    // PNG only at 256: a raw DIB that big is a quarter of a megabyte, and the shell reads PNG
    // entries fine. Below that, stay with DIB — System.Drawing.Icon cannot decode PNG entries,
    // so a PNG-only icon would break anything loading it through GDI+.
    entries.Add((size, size >= 256 ? ToPng(bitmap) : ToDib(bitmap)));
}

File.WriteAllBytes(outPath, BuildIco(entries));
Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length:N0} bytes, {entries.Count} sizes)");

Bitmap Render(int s)
{
    var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.Transparent);

    // Gauge ring: an open dial with a gap at the bottom, so it reads as a gauge rather than a
    // plain circle. Stroke scales with the canvas or it disappears at 16 px.
    var stroke = Math.Max(2f, s * 0.13f);
    var inset = stroke / 2f + s * 0.05f;
    var rect = new RectangleF(inset, inset, s - 2 * inset, s - 2 * inset);

    const float start = 135f;
    const float sweep = 270f;

    using (var pen = new Pen(track, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        g.DrawArc(pen, rect, start, sweep);

    // The "reading": about three quarters of the dial, so it looks like a gauge under load.
    using (var pen = new Pen(arc, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        g.DrawArc(pen, rect, start, sweep * 0.72f);

    // The T is drawn from two rectangles rather than rendered as text: font hinting makes a
    // glyph wobble between sizes, and at 16 px a drawn bar stays crisp where a letter blurs.
    using var brush = new SolidBrush(letter);
    var barW = s * 0.40f;
    var barH = Math.Max(2f, s * 0.12f);
    var stemW = Math.Max(2f, s * 0.14f);
    var stemH = s * 0.40f;
    var top = s * 0.30f;

    g.FillRectangle(brush, (s - barW) / 2f, top, barW, barH);
    g.FillRectangle(brush, (s - stemW) / 2f, top, stemW, stemH);

    return bmp;
}

static byte[] ToPng(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

// Encodes a bitmap as the classic .ico payload: BITMAPINFOHEADER, bottom-up BGRA rows, then an
// AND mask. The header's height is doubled because the DIB nominally stacks the colour bitmap
// on top of the mask, even though the 32-bit alpha channel does the real masking.
static byte[] ToDib(Bitmap bmp)
{
    var w = bmp.Width;
    var h = bmp.Height;

    var locked = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var pixels = new byte[locked.Stride * h];
    System.Runtime.InteropServices.Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length);
    bmp.UnlockBits(locked);

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);

    bw.Write(40);                 // biSize
    bw.Write(w);                  // biWidth
    bw.Write(h * 2);              // biHeight - colour bitmap plus mask
    bw.Write((ushort)1);          // biPlanes
    bw.Write((ushort)32);         // biBitCount
    bw.Write(0);                  // biCompression: BI_RGB
    bw.Write(w * h * 4);          // biSizeImage
    bw.Write(0); bw.Write(0);     // pixels-per-metre
    bw.Write(0); bw.Write(0);     // palette

    for (var y = h - 1; y >= 0; y--)
        bw.Write(pixels, y * locked.Stride, w * 4);

    // AND mask, all zeros, rows padded to a 4-byte boundary.
    var maskStride = (w + 31) / 32 * 4;
    bw.Write(new byte[maskStride * h]);

    bw.Flush();
    return ms.ToArray();
}

static byte[] BuildIco(List<(int Size, byte[] Data)> entries)
{
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);

    bw.Write((ushort)0);                // reserved
    bw.Write((ushort)1);                // type: icon
    bw.Write((ushort)entries.Count);

    var offset = 6 + 16 * entries.Count;
    foreach (var (size, data) in entries)
    {
        var dim = (byte)(size >= 256 ? 0 : size);   // 0 means 256 in the directory
        bw.Write(dim);                  // width
        bw.Write(dim);                  // height
        bw.Write((byte)0);              // palette entries
        bw.Write((byte)0);              // reserved
        bw.Write((ushort)1);            // colour planes
        bw.Write((ushort)32);           // bits per pixel
        bw.Write(data.Length);
        bw.Write(offset);
        offset += data.Length;
    }

    foreach (var (_, data) in entries) bw.Write(data);

    bw.Flush();
    return ms.ToArray();
}

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !File.Exists(Path.Combine(dir, "T4Power.slnx")))
        dir = Path.GetDirectoryName(dir);
    return dir ?? throw new InvalidOperationException("could not locate the repository root");
}

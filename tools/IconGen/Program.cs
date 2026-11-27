using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// MailClient 아이콘: 파란 둥근 사각형 + 흰 봉투 + 주황 "새 메일" 점
// 사용: icongen <출력 .ico> <미리보기 .png>
var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
var frames = sizes.Select(s => (Size: s, Png: Render(s))).ToList();
WriteIco(args[0], frames);

// 미리보기: 흰 배경 / 어두운 배경에 모든 크기
using (var sheet = new Bitmap(sizes.Sum() + 12 * sizes.Length + 12, 256 * 2 + 36))
using (var g = Graphics.FromImage(sheet))
{
    g.Clear(Color.White);
    g.FillRectangle(new SolidBrush(Color.FromArgb(32, 32, 32)), 0, 256 + 18, sheet.Width, 256 + 18);
    int x = 12;
    foreach (var (s, png) in frames)
    {
        using var bmp = new Bitmap(new MemoryStream(png));
        g.DrawImageUnscaled(bmp, x, 256 - s + 6);
        g.DrawImageUnscaled(bmp, x, 256 * 2 + 24 - s);
        x += s + 12;
    }
    sheet.Save(args[1], ImageFormat.Png);
}
// 작은 크기 확대 (픽셀 그대로 8배)
using (var zoom = new Bitmap((16 + 20 + 24 + 32) * 8 + 50, 32 * 8 + 10))
using (var g = Graphics.FromImage(zoom))
{
    g.Clear(Color.FromArgb(240, 240, 240));
    g.InterpolationMode = InterpolationMode.NearestNeighbor;
    g.PixelOffsetMode = PixelOffsetMode.Half;
    int x = 5;
    foreach (var (s, png) in frames.Where(f => f.Size <= 32))
    {
        using var bmp = new Bitmap(new MemoryStream(png));
        g.DrawImage(bmp, x, 5, s * 8, s * 8);
        x += s * 8 + 10;
    }
    zoom.Save(Path.ChangeExtension(args[1], ".zoom.png"), ImageFormat.Png);
}
Console.WriteLine($"wrote {args[0]} ({new FileInfo(args[0]).Length} bytes)");

static byte[] Render(int size)
{
    using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float s = size;
        bool tiny = size <= 24;   // 작은 크기는 선과 여백을 단순·굵게

        // 배경: 둥근 사각형, 위→아래 파란 그라데이션
        float pad = tiny ? 0.5f : s * 0.04f;
        var tile = new RectangleF(pad, pad, s - pad * 2, s - pad * 2);
        float radius = s * (tiny ? 0.2f : 0.22f);
        using (var path = RoundRect(tile, radius))
        using (var bg = new LinearGradientBrush(tile, Color.FromArgb(0x2F, 0x8C, 0xF5), Color.FromArgb(0x0E, 0x4F, 0xB5), 90f))
        {
            g.FillPath(bg, path);
            if (!tiny)
                using (var rim = new Pen(Color.FromArgb(60, 0, 0, 40), Math.Max(1f, s / 128f)))
                    g.DrawPath(rim, path);
        }

        // 봉투
        float ew = s * (tiny ? 0.72f : 0.64f), eh = ew * 0.70f;
        float ex = (s - ew) / 2, ey = (s - eh) / 2 + s * (tiny ? 0.02f : 0.03f);
        var env = new RectangleF(ex, ey, ew, eh);
        float er = tiny ? 1f : s * 0.045f;
        if (!tiny)
            using (var shadowPath = RoundRect(new RectangleF(ex, ey + s * 0.025f, ew, eh), er))
            using (var shadow = new SolidBrush(Color.FromArgb(55, 0, 20, 70)))
                g.FillPath(shadow, shadowPath);
        using (var envPath = RoundRect(env, er))
        using (var white = new SolidBrush(Color.White))
            g.FillPath(white, envPath);

        // 봉투 덮개 (V자 선)
        float lw = tiny ? Math.Max(1.2f, s / 13f) : s * 0.045f;
        using (var flap = new Pen(Color.FromArgb(0x1A, 0x5F, 0xC8), lw) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float inset = lw * 0.9f;
            g.DrawLines(flap, new[]
            {
                new PointF(ex + inset, ey + inset),
                new PointF(ex + ew / 2, ey + eh * 0.56f),
                new PointF(ex + ew - inset, ey + inset)
            });
        }

        // 새 메일 표시 점 (오른쪽 위, 흰 테두리)
        float dot = s * (tiny ? 0.36f : 0.30f);
        float dx = s - dot - (tiny ? 0 : s * 0.03f), dy = tiny ? 0 : s * 0.03f;
        using (var ring = new SolidBrush(Color.White))
            g.FillEllipse(ring, dx - s * 0.02f, dy - s * 0.02f, dot + s * 0.04f, dot + s * 0.04f);
        using (var orange = new LinearGradientBrush(new RectangleF(dx, dy, dot, dot), Color.FromArgb(0xFF, 0xA9, 0x3A), Color.FromArgb(0xF2, 0x6B, 0x1D), 90f))
            g.FillEllipse(orange, dx, dy, dot, dot);
    }
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static GraphicsPath RoundRect(RectangleF r, float radius)
{
    float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
    var p = new GraphicsPath();
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

// ICO: 헤더 + 디렉터리 + PNG 이미지 (Windows Vista 이상은 모든 크기의 PNG 항목 지원)
static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)frames.Count);
    int offset = 6 + 16 * frames.Count;
    foreach (var (size, png) in frames)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(png.Length); w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in frames) w.Write(png);
}

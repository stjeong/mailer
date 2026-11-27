using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MailClient.Forms;

/// <summary>
/// 응용 프로그램 아이콘: 파란 둥근 사각형 + 흰 봉투. 새 메일이 있으면 오른쪽 위에 주황색 점을 표시합니다.
/// 모든 크기(16~256px)를 코드로 그려 아이콘을 만들므로 제목 표시줄·작업 표시줄·알림 영역이 각자 맞는 크기를 고릅니다.
/// (실행 파일 아이콘 app.ico 는 이 코드로 만든 점 없는 버전)
/// </summary>
internal static class AppIcon
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    private static readonly Lazy<Icon> Plain = new(() => Build(newMail: false));
    private static readonly Lazy<Icon> WithBadge = new(() => Build(newMail: true));

    /// <summary>창 아이콘 (제목 표시줄·Alt+Tab·작업 표시줄)</summary>
    public static Icon Window => Plain.Value;

    /// <summary>창 아이콘. <paramref name="newMail"/> 이면 새 메일 표시(주황색 점) 포함</summary>
    public static Icon ForWindow(bool newMail) => newMail ? WithBadge.Value : Plain.Value;

    /// <summary>
    /// 알림 영역(트레이) 아이콘: 작은 아이콘 크기 이미지를 골라 흐려지지 않게.
    /// 작업 표시줄은 주 모니터에 있으므로 창이 있는 모니터가 아니라 시스템 배율을 기준으로 합니다.
    /// </summary>
    public static Icon Tray(bool newMail = false)
    {
        int size = (int)Math.Round(16 * DpiForm.SystemDpi / 96.0);
        return new Icon(ForWindow(newMail), size, size);
    }

    private static Icon Build(bool newMail)
    {
        using var ms = new MemoryStream(BuildIco(newMail));
        return new Icon(ms);
    }

    /// <summary>모든 크기를 PNG 로 담은 .ico 파일 내용</summary>
    internal static byte[] BuildIco(bool newMail)
    {
        var images = Sizes.Select(s =>
        {
            using var bmp = Render(s, newMail);
            using var png = new MemoryStream();
            bmp.Save(png, ImageFormat.Png);
            return (Size: s, Data: png.ToArray());
        }).ToList();

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);   // ICONDIR
        int offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(data.Length); w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>아이콘 한 장 그리기 (비율은 128px 기준 디자인에서)</summary>
    internal static Bitmap Render(int size, bool newMail)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float u = size / 128f;
        RectangleF R(float x1, float y1, float x2, float y2) => new(x1 * u, y1 * u, (x2 - x1) * u, (y2 - y1) * u);

        // 파란 둥근 사각형 (위 → 아래로 진해짐)
        var tile = R(5, 5, 123, 123);
        using var tilePath = RoundRect(tile, 28 * u);
        using var blue = new LinearGradientBrush(tile, Color.FromArgb(0x2E, 0x89, 0xF2), Color.FromArgb(0x0F, 0x51, 0xB7), LinearGradientMode.Vertical);
        g.FillPath(blue, tilePath);

        // 흰 봉투 + 덮개 선(V)
        using var envelope = RoundRect(R(24, 40, 105, 96), 5 * u);
        g.FillPath(Brushes.White, envelope);
        using var flap = new Pen(blue, Math.Max(1f, 7 * u)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.SetClip(envelope);
        g.DrawLines(flap, [new PointF(24 * u, 41 * u), new PointF(64.5f * u, 71 * u), new PointF(105 * u, 41 * u)]);
        g.ResetClip();

        // 새 메일 표시: 흰 테두리가 있는 주황색 점
        if (newMail)
        {
            float cx = 104.5f, cy = 22.5f, r = 18.5f, ring = Math.Max(2.5f, 1.2f / u);   // 작은 크기에서도 테두리가 보이게
            using var white = new SolidBrush(Color.White);
            g.FillEllipse(white, R(cx - r - ring, cy - r - ring, cx + r + ring, cy + r + ring));
            var dot = R(cx - r, cy - r, cx + r, cy + r);
            using var orange = new LinearGradientBrush(dot, Color.FromArgb(0xFC, 0x9B, 0x34), Color.FromArgb(0xF3, 0x76, 0x1E), LinearGradientMode.Vertical);
            g.FillEllipse(orange, dot);
        }
        return bmp;
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

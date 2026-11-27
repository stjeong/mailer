using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MailClient.Forms;

/// <summary>Windows 기본 아이콘 글꼴(Segoe Fluent Icons / Segoe MDL2 Assets)의 글리프</summary>
public static class RibbonGlyph
{
    public const string Mail = "";
    public const string Read = "";
    public const string Sync = "";
    public const string Stop = "";
    public const string Reply = "";
    public const string ReplyAll = "";
    public const string Forward = "";
    public const string Delete = "";
    public const string Restore = "";
    public const string Junk = "";
    public const string NotJunk = "";
    public const string MoveToFolder = "";
    public const string Settings = "";
    public const string Print = "";
    public const string Close = "";
    public const string Search = "";
    public const string PreviewPane = "";
    public const string Refresh = "";
    public const string AddAccount = "";
    public const string Account = "";
    public const string Filter = "";
    public const string Save = "";
    public const string OpenInNew = "";
    public const string ChevronDown = "";

    /// <summary>설치된 아이콘 글꼴 (Windows 11: Segoe Fluent Icons, Windows 10: Segoe MDL2 Assets)</summary>
    public static readonly string FontFamilyName = ResolveFamily();

    private static string ResolveFamily()
    {
        using var fonts = new InstalledFontCollection();
        var names = fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    private static readonly Dictionary<int, Font> Cache = new();

    public static Font GetFont(int pixelSize)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(pixelSize, out var f))
                Cache[pixelSize] = f = new Font(FontFamilyName, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel);
            return f;
        }
    }
}

internal static class RibbonDpi
{
    /// <summary>논리 크기(96 DPI 기준)를 컨트롤이 놓인 창의 DPI 로 변환</summary>
    public static int Scale(Control c, int logical)
    {
        int dpi = c.FindForm()?.DeviceDpi ?? c.DeviceDpi;
        return (int)Math.Round(logical * dpi / 96.0);
    }
}

/// <summary>
/// 간단한 리본: [탭 머리글] + [선택된 탭의 그룹들]. 모든 크기는 DPI 에 맞게 변환합니다.
/// 탭을 바꿔도 높이가 변하지 않도록 가장 높은 탭 기준으로 본문 높이를 고정합니다.
/// </summary>
public class RibbonBar : TableLayoutPanel, IDpiAware
{
    internal static readonly Color BodyBack = Color.FromArgb(250, 250, 250);
    internal static readonly Color HeaderBack = Color.FromArgb(240, 240, 240);
    internal static readonly Color Accent = Color.FromArgb(0, 95, 184);
    internal static readonly Color Border = Color.FromArgb(218, 218, 218);

    private readonly FlowLayoutPanel _headers = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly List<RibbonTab> _tabs = [];
    private readonly ToolTip _toolTip = new() { ShowAlways = true };
    private readonly RibbonButton _overflow = new() { Glyph = "", Text = "더 보기", Large = true, Visible = false };   // E712 = More
    private readonly ContextMenuStrip _overflowMenu = new();
    private RibbonTab? _selected;
    private bool _reflowing;

    public RibbonBar()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        Margin = Padding.Empty;
        BackColor = BodyBack;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 10));   // 본문 높이: UpdateBodyHeight 에서 설정
        RowCount = 2;
        _headers.BackColor = HeaderBack;
        ApplyMetrics();
        Controls.Add(_headers, 0, 0);
        Controls.Add(_host, 0, 1);

        // 창이 좁으면 오른쪽 그룹부터 숨기고, 숨긴 그룹의 명령을 [더 보기] 메뉴로 제공
        _overflow.DropDown = _overflowMenu;
        _overflowMenu.Opening += (_, e) => e.Cancel = !FillOverflowMenu();
        SetToolTip(_overflow, "창이 좁아 숨겨진 명령");
        _host.Controls.Add(_overflow);
        _host.Resize += (_, _) => Reflow();
    }

    /// <summary>선택된 탭의 그룹이 폭에 다 들어가지 않으면 오른쪽 그룹부터 숨기고 [더 보기] 버튼 표시</summary>
    private void Reflow()
    {
        if (_reflowing || _selected == null || _host.Width <= 0) return;
        _reflowing = true;
        try
        {
            var groups = _selected.Panel.Controls.OfType<RibbonGroup>().ToList();
            foreach (var g in groups) g.Visible = true;
            _selected.Panel.PerformLayout();

            int available = _host.ClientSize.Width;
            bool overflow = _selected.Panel.PreferredSize.Width > available;
            if (overflow)
            {
                int limit = available - _overflow.Width - LogicalToDeviceUnits(8);
                int x = _selected.Panel.Padding.Left;
                bool hideRest = false;
                foreach (var g in groups)
                {
                    int w = g.PreferredSize.Width + g.Margin.Horizontal;
                    if (hideRest || x + w > limit) { g.Visible = false; hideRest = true; }
                    else x += w;
                }
            }
            _overflow.Visible = overflow;
            if (overflow)
            {
                _overflow.Location = new Point(available - _overflow.Width - LogicalToDeviceUnits(4), _selected.Panel.Padding.Top);
                _overflow.BringToFront();
            }
        }
        finally
        {
            _reflowing = false;
        }
    }

    /// <summary>숨겨진 그룹의 버튼들을 메뉴 항목으로 (그룹 이름은 구분용 비활성 항목)</summary>
    private bool FillOverflowMenu()
    {
        _overflowMenu.Items.Clear();
        if (_selected == null) return false;
        foreach (var g in _selected.Panel.Controls.OfType<RibbonGroup>().Where(g => !g.Visible))
        {
            if (_overflowMenu.Items.Count > 0) _overflowMenu.Items.Add(new ToolStripSeparator());
            _overflowMenu.Items.Add(new ToolStripMenuItem(g.Caption) { Enabled = false, Font = new Font(_overflowMenu.Font, FontStyle.Bold) });
            foreach (var b in g.Buttons)
            {
                var button = b;
                var mi = new ToolStripMenuItem(button.Text.Replace("\n", " ")) { Enabled = button.Enabled, Checked = button.Checked };
                mi.Click += (_, _) =>
                {
                    if (button.DropDown != null) button.DropDown.Show(_overflow, new Point(0, _overflow.Height));
                    else button.PerformClick();
                };
                _overflowMenu.Items.Add(mi);
            }
        }
        return _overflowMenu.Items.Count > 0;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<RibbonTab> Tabs => _tabs;

    public RibbonTab AddTab(string text)
    {
        var tab = new RibbonTab(this, text);
        _tabs.Add(tab);
        _headers.Controls.Add(tab.Header);
        _host.Controls.Add(tab.Panel);
        tab.Panel.Visible = false;
        tab.Header.Click += (_, _) => SelectTab(tab);
        if (_selected == null) SelectTab(tab);
        return tab;
    }

    public void SelectTab(RibbonTab tab)
    {
        _selected = tab;
        foreach (var t in _tabs)
        {
            t.Panel.Visible = t == tab;
            t.Header.Selected = t == tab;
        }
        Reflow();
    }

    /// <summary>DPI 에 따른 여백 (논리 값 → 현재 DPI)</summary>
    private void ApplyMetrics()
    {
        _headers.Padding = new Padding(RibbonDpi.Scale(this, 6), RibbonDpi.Scale(this, 2), 0, 0);
    }

    internal void SetToolTip(Control c, string? text)
    {
        if (!string.IsNullOrEmpty(text)) _toolTip.SetToolTip(c, text);
    }

    /// <summary>모든 탭 중 가장 높은 탭에 맞춰 본문 높이 고정 (컨트롤을 다 추가한 뒤, 또는 글꼴이 정해진 뒤 호출)</summary>
    public void UpdateBodyHeight()
    {
        // 모든 그룹의 버튼 영역 높이를 같게 맞춰 그룹 이름이 한 줄로 정렬되게 함
        var groups = _tabs.SelectMany(t => t.Panel.Controls.OfType<RibbonGroup>()).ToList();
        foreach (var g in groups) g.ItemsArea.MinimumSize = Size.Empty;
        int itemsHeight = groups.Count == 0 ? 0 : groups.Max(g => g.ItemsArea.GetPreferredSize(Size.Empty).Height);
        foreach (var g in groups) g.ItemsArea.MinimumSize = new Size(0, itemsHeight);

        int h = _tabs.Count == 0 ? 0 : _tabs.Max(t => t.Panel.GetPreferredSize(Size.Empty).Height);
        RowStyles[1] = new RowStyle(SizeType.Absolute, h + 1);
        PerformLayout();
        Reflow();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateBodyHeight();
    }

    /// <summary>화면 배율이 바뀐 뒤: 버튼·탭 머리글 크기를 새 글꼴/아이콘 크기로 다시 계산하고 본문 높이를 맞춤</summary>
    public void OnDpiRescaled(int oldDpi, int newDpi)
    {
        // 여백은 비율을 곱하면 반올림 오차가 그룹마다 쌓이므로 논리 값에서 다시 계산
        ApplyMetrics();
        foreach (var t in _tabs)
        {
            t.ApplyMetrics();
            foreach (var g in t.Panel.Controls.OfType<RibbonGroup>()) g.ApplyMetrics();
        }
        foreach (var c in DpiForm.Descendants(this))
        {
            if (c is RibbonButton b) b.RefreshSize();
            else if (c is RibbonTabHeader h) h.RefreshSize();
        }
        UpdateBodyHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Border);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

public class RibbonTab
{
    internal RibbonTab(RibbonBar owner, string text)
    {
        Owner = owner;
        Header = new RibbonTabHeader { Text = text };
        Panel = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, Location = Point.Empty, Margin = Padding.Empty,
            BackColor = RibbonBar.BodyBack
        };
        ApplyMetrics();
    }

    internal void ApplyMetrics()
    {
        int S(int v) => RibbonDpi.Scale(Owner, v);
        Panel.Padding = new Padding(S(4), S(3), 0, S(2));
    }

    internal RibbonBar Owner { get; }
    internal RibbonTabHeader Header { get; }
    internal FlowLayoutPanel Panel { get; }

    public RibbonGroup AddGroup(string caption)
    {
        var g = new RibbonGroup(Owner, caption);
        Panel.Controls.Add(g);
        return g;
    }
}

/// <summary>리본 그룹: [버튼들] 아래에 그룹 이름, 오른쪽에 구분선</summary>
public class RibbonGroup : TableLayoutPanel
{
    private readonly RibbonBar _owner;
    private readonly FlowLayoutPanel _items;
    private readonly Label _caption;

    public string Caption { get; }

    /// <summary>버튼이 놓이는 영역 (높이 정렬용)</summary>
    internal FlowLayoutPanel ItemsArea => _items;

    /// <summary>그룹 안의 모든 리본 버튼 (넘침 메뉴 구성용)</summary>
    internal IEnumerable<RibbonButton> Buttons => Descendants(_items).OfType<RibbonButton>();

    private static IEnumerable<Control> Descendants(Control c) =>
        c.Controls.Cast<Control>().SelectMany(x => Descendants(x).Prepend(x));

    internal RibbonGroup(RibbonBar owner, string caption)
    {
        _owner = owner;
        Caption = caption;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        RowCount = 2;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _items = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Dock = DockStyle.Fill };
        _caption = new Label
        {
            Text = caption, AutoSize = true, Anchor = AnchorStyles.None, ForeColor = Color.FromArgb(96, 96, 96), UseMnemonic = false
        };
        Controls.Add(_items, 0, 0);
        Controls.Add(_caption, 0, 1);
        ApplyMetrics();
    }

    /// <summary>DPI 에 따른 여백 (논리 값 → 현재 DPI)</summary>
    internal void ApplyMetrics()
    {
        int S(int v) => RibbonDpi.Scale(_owner, v);
        Margin = new Padding(0, 0, S(4), 0);
        Padding = new Padding(S(2), 0, S(6), 0);
        _caption.Margin = new Padding(0, S(1), 0, S(1));
    }

    /// <summary>아이콘이 위, 글자가 아래인 큰 버튼</summary>
    public RibbonButton AddLarge(string glyph, string text, string? toolTip, Action? onClick)
    {
        var b = Make(glyph, text, toolTip, onClick, large: true);
        _items.Controls.Add(b);
        return b;
    }

    /// <summary>작은 버튼을 세로로 최대 3개 쌓은 열</summary>
    public RibbonButton[] AddSmallStack(params (string Glyph, string Text, string? ToolTip, Action? OnClick)[] buttons)
    {
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var result = buttons.Select(x => Make(x.Glyph, x.Text, x.ToolTip, x.OnClick, large: false)).ToArray();
        stack.Controls.AddRange(result);
        _items.Controls.Add(stack);
        return result;
    }

    /// <summary>임의 컨트롤 (예: 검색 상자)</summary>
    public void AddControl(Control c)
    {
        _items.Controls.Add(c);
    }

    private RibbonButton Make(string glyph, string text, string? toolTip, Action? onClick, bool large)
    {
        var b = new RibbonButton { Glyph = glyph, Text = text, Large = large };
        if (onClick != null) b.Click += (_, _) => onClick();
        _owner.SetToolTip(b, toolTip);
        return b;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int inset = _owner.LogicalToDeviceUnits(6);
        using var pen = new Pen(RibbonBar.Border);
        e.Graphics.DrawLine(pen, Width - 1, inset, Width - 1, Height - inset);
    }
}

/// <summary>리본 탭 머리글 (선택된 탭은 강조색 글자 + 밑줄)</summary>
public class RibbonTabHeader : Control
{
    private bool _selected, _hover;

    public RibbonTabHeader()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        AutoSize = true;
        Margin = Padding.Empty;
        BackColor = Color.Transparent;
    }

    [DefaultValue(false)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(t.Width + RibbonDpi.Scale(this, 24), t.Height + RibbonDpi.Scale(this, 10));
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); PerformAutoSize(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); PerformAutoSize(); }
    private void PerformAutoSize() { Size = GetPreferredSize(Size.Empty); Invalidate(); }
    internal void RefreshSize() => PerformAutoSize();
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_selected)
        {
            using var b = new SolidBrush(RibbonBar.BodyBack);
            g.FillRectangle(b, ClientRectangle);
        }
        else if (_hover)
        {
            using var b = new SolidBrush(Color.FromArgb(229, 229, 229));
            g.FillRectangle(b, ClientRectangle);
        }
        var color = _selected ? RibbonBar.Accent : ForeColor;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (_selected)
        {
            int bar = Math.Max(2, RibbonDpi.Scale(this, 3));
            int inset = RibbonDpi.Scale(this, 10);
            using var b = new SolidBrush(RibbonBar.Accent);
            g.FillRectangle(b, inset, Height - bar, Width - inset * 2, bar);
        }
    }
}

/// <summary>
/// 리본 버튼. Large: 아이콘(위) + 글자(아래, "\n" 으로 두 줄 가능). Small: 아이콘(왼쪽) + 글자.
/// Checked 로 토글 상태 표시, DropDown 을 지정하면 클릭 시 메뉴를 펼칩니다.
/// </summary>
public class RibbonButton : Control
{
    private bool _large, _checked, _hover, _pressed;
    private string _glyph = "";
    private ContextMenuStrip? _dropDown;

    public RibbonButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);   // 리본은 마우스용, 키보드는 메뉴/단축키로
        AutoSize = true;
        BackColor = Color.Transparent;
        Margin = new Padding(1);
    }

    [DefaultValue("")]
    public string Glyph { get => _glyph; set { _glyph = value; PerformAutoSize(); } }

    [DefaultValue(false)]
    public bool Large { get => _large; set { _large = value; PerformAutoSize(); } }

    [DefaultValue(false)]
    public bool Checked { get => _checked; set { _checked = value; Invalidate(); } }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ContextMenuStrip? DropDown { get => _dropDown; set { _dropDown = value; PerformAutoSize(); } }

    /// <summary>
    /// 창의 DPI 기준 변환. 숨겨진 탭의 버튼은 아직 핸들이 없어 자기 DeviceDpi 가 모니터 배율 변경을 따라가지 않으므로
    /// 창(Form)의 DPI 를 씁니다.
    /// </summary>
    private int S(int v) => RibbonDpi.Scale(this, v);
    private int GlyphSize => _large ? S(26) : S(16);
    private int ArrowWidth => _dropDown != null ? S(12) : 0;
    private string[] Lines => Text.Split('\n');

    public override Size GetPreferredSize(Size proposedSize)
    {
        int lineH = TextRenderer.MeasureText("가Ag", Font).Height;
        if (_large)
        {
            int textW = Lines.Max(l => TextRenderer.MeasureText(l, Font, Size.Empty, TextFormatFlags.NoPrefix).Width);
            int w = Math.Max(textW + ArrowWidth, GlyphSize) + S(14);
            // 실제 글자 줄 수만큼만 높이를 잡음 (항상 2줄을 잡으면 1줄 버튼 아래에 빈 공간이 생김)
            int h = S(4) + GlyphSize + S(2) + lineH * Lines.Length + S(4);
            return new Size(Math.Max(w, S(52)), h);
        }
        else
        {
            int textW = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width;
            return new Size(S(6) + GlyphSize + S(6) + textW + ArrowWidth + S(8), Math.Max(lineH, GlyphSize) + S(6));
        }
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); PerformAutoSize(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); PerformAutoSize(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); _hover = _pressed = false; Invalidate(); }
    protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); PerformAutoSize(); }

    private void PerformAutoSize()
    {
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    internal void RefreshSize() => PerformAutoSize();

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnClick(EventArgs e)
    {
        if (_dropDown != null)
        {
            _dropDown.Show(this, new Point(0, Height));
            return;
        }
        base.OnClick(e);
    }

    /// <summary>코드에서 클릭 (테스트/단축키용)</summary>
    public void PerformClick()
    {
        if (Enabled) OnClick(EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        Color? back = null, border = null;
        if (Enabled && (_pressed || (_checked && _hover))) { back = Color.FromArgb(188, 218, 244); border = Color.FromArgb(120, 174, 229); }
        else if (Enabled && _checked) { back = Color.FromArgb(204, 228, 247); border = Color.FromArgb(153, 201, 239); }
        else if (Enabled && _hover) { back = Color.FromArgb(229, 241, 251); border = Color.FromArgb(204, 228, 247); }
        if (back != null)
        {
            using var path = RoundRect(rect, S(4));
            using var b = new SolidBrush(back.Value);
            using var p = new Pen(border!.Value);
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }

        var glyphColor = Enabled ? RibbonBar.Accent : SystemColors.GrayText;
        var textColor = Enabled ? ForeColor : SystemColors.GrayText;
        var glyphFont = RibbonGlyph.GetFont(GlyphSize);
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        if (_large)
        {
            var glyphRect = new Rectangle(0, S(4), Width, GlyphSize);
            TextRenderer.DrawText(g, _glyph, glyphFont, glyphRect, glyphColor, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            int lineH = TextRenderer.MeasureText("가Ag", Font).Height;
            int y = glyphRect.Bottom + S(2);
            var lines = Lines;
            for (int i = 0; i < lines.Length; i++)
            {
                var text = lines[i] + (_dropDown != null && i == lines.Length - 1 ? " ▾" : "");
                TextRenderer.DrawText(g, text, Font, new Rectangle(0, y + i * lineH, Width, lineH), textColor,
                    flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
            }
        }
        else
        {
            var glyphRect = new Rectangle(S(6), 0, GlyphSize, Height);
            TextRenderer.DrawText(g, _glyph, glyphFont, glyphRect, glyphColor, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var textRect = new Rectangle(glyphRect.Right + S(6), 0, Width - glyphRect.Right - S(6), Height);
            TextRenderer.DrawText(g, Text + (_dropDown != null ? " ▾" : ""), Font, textRect, textColor, flags | TextFormatFlags.VerticalCenter);
        }
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

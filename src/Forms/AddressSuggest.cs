using MimeKit;

namespace MailClient.Forms;

/// <summary>
/// 받는 사람 입력란의 주소 추천. 커서가 있는 주소 조각(';' 또는 ',' 로 구분)에 입력한 글자가 들어 있는
/// 주소를 입력란 아래 목록으로 보여 줍니다.
/// <para>↑/↓ 선택, Enter/Tab 넣기, Esc 닫기, 마우스로 눌러 넣기. 목록 창은 포커스를 가져가지 않아 계속 입력할 수 있습니다.</para>
/// </summary>
public sealed class AddressSuggest : IDisposable
{
    private const int MaxItems = 8;

    private readonly TextBox _box;
    private readonly Func<string, int, List<(string Address, string Name)>> _search;
    private SuggestPopup? _popup;
    private bool _applying;

    /// <param name="search">(입력한 글자, 최대 개수) → 추천 주소</param>
    public AddressSuggest(TextBox box, Func<string, int, List<(string Address, string Name)>> search)
    {
        _box = box;
        _search = search;
        _box.TextChanged += (_, _) => { if (!_applying && _box.Focused) UpdateSuggestions(); };
        _box.PreviewKeyDown += (_, e) =>
        {
            // 목록이 떠 있을 때 Tab/Enter/Esc 를 입력란이 받도록 (대화 상자 키로 처리되지 않게)
            if (IsOpen && e.KeyCode is Keys.Tab or Keys.Enter or Keys.Escape && !e.Control && !e.Alt) e.IsInputKey = true;
        };
        _box.KeyDown += OnKeyDown;
        _box.Leave += (_, _) => Close();
        _box.MouseDown += (_, _) => Close();
    }

    private bool IsOpen => _popup is { Visible: true };

    private Form? _hookedForm;

    /// <summary>창을 옮기거나 크기를 바꾸거나 다른 창으로 가면 닫음 (목록을 처음 띄울 때 연결)</summary>
    private void HookForm(Form form)
    {
        if (form == _hookedForm) return;
        _hookedForm = form;
        form.Move += (_, _) => Close();
        form.Resize += (_, _) => Close();
        form.Deactivate += (_, _) => Close();
        form.DpiChanged += (_, _) => Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsOpen || e.Control || e.Alt) return;
        switch (e.KeyCode)
        {
            case Keys.Down:
                _popup!.MoveSelection(+1);
                break;
            case Keys.Up:
                _popup!.MoveSelection(-1);
                break;
            case Keys.Enter:
            case Keys.Tab:
                Accept(_popup!.SelectedIndex);
                break;
            case Keys.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    /// <summary>커서가 있는 주소 조각의 범위 [start, end)</summary>
    private (int Start, int End) CurrentToken()
    {
        var text = _box.Text;
        int caret = Math.Clamp(_box.SelectionStart, 0, text.Length);
        int start = text.LastIndexOfAny([';', ','], Math.Max(caret - 1, 0)) is var s and >= 0 && s < caret ? s + 1 : 0;
        int end = text.IndexOfAny([';', ','], caret) is var n and >= 0 ? n : text.Length;
        return (start, end);
    }

    private void UpdateSuggestions()
    {
        var (start, end) = CurrentToken();
        var query = _box.Text[start..end].Trim();
        if (query.Length == 0) { Close(); return; }

        // 이미 입력한 다른 주소는 빼고
        var others = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rest = _box.Text[..start] + _box.Text[end..];
        if (InternetAddressList.TryParse(rest.Replace(';', ','), out var list))
            foreach (var m in list.Mailboxes) others.Add(m.Address);

        var matches = _search(query, MaxItems + others.Count)
            .Where(m => !others.Contains(m.Address))
            .Take(MaxItems)
            .ToList();
        // 이미 그 주소를 끝까지 입력했으면 띄우지 않음
        if (matches.Count == 0 || (matches.Count == 1 && string.Equals(matches[0].Address, query, StringComparison.OrdinalIgnoreCase)))
        {
            Close();
            return;
        }
        Show(matches);
    }

    private void Show(List<(string Address, string Name)> matches)
    {
        var owner = _box.FindForm();
        if (owner == null) return;
        HookForm(owner);
        if (_popup == null || _popup.IsDisposed)
        {
            _popup = new SuggestPopup();
            _popup.Picked += Accept;
        }
        _popup.SetItems(matches, _box.Font, _box.LogicalToDeviceUnits(4));
        var origin = _box.PointToScreen(new Point(0, _box.Height));
        int width = Math.Max(_box.Width, _box.LogicalToDeviceUnits(260));
        int height = _popup.PreferredHeight;
        // 화면 아래가 모자라면 입력란 위로
        var work = Screen.FromControl(_box).WorkingArea;
        int y = origin.Y + height <= work.Bottom ? origin.Y : _box.PointToScreen(Point.Empty).Y - height;
        _popup.Bounds = new Rectangle(Math.Min(origin.X, work.Right - width), y, width, height);
        if (!_popup.Visible)
        {
            _popup.Owner ??= owner;   // 쓰기 창 위에 표시되고, 창을 닫으면 함께 닫힘
            _popup.Show();
        }
        _popup.Invalidate();
    }

    private void Accept(int index)
    {
        if (_popup == null || index < 0 || index >= _popup.Items.Count) { Close(); return; }
        var (address, name) = _popup.Items[index];
        // 이름에 구분 문자가 있으면 주소 나누기가 깨지므로 주소만
        var display = name.Length > 0 && name.IndexOfAny([';', ',', '"', '<', '>']) < 0 ? $"{name} <{address}>" : address;

        var (start, end) = CurrentToken();
        var text = _box.Text;
        var before = text[..start];
        var after = text[end..];
        var prefix = start > 0 && !before.EndsWith(' ') ? " " : "";
        // 뒤에 이어지는 주소가 없으면 다음 주소를 바로 입력할 수 있게 "; " 를 붙임
        var suffix = after.TrimStart().Length == 0 ? "; " : "";
        if (suffix.Length == 0) after = after.TrimStart();

        _applying = true;
        try
        {
            _box.Text = before + prefix + display + suffix + after;
            _box.SelectionStart = before.Length + prefix.Length + display.Length + suffix.Length;
            _box.SelectionLength = 0;
        }
        finally
        {
            _applying = false;
        }
        Close();
    }

    public void Close()
    {
        if (_popup is { IsDisposed: false, Visible: true }) _popup.Hide();
    }

    public void Dispose() => _popup?.Dispose();

    /// <summary>포커스를 가져가지 않는 추천 목록 창</summary>
    private sealed class SuggestPopup : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

        public List<(string Address, string Name)> Items { get; } = [];
        public int SelectedIndex { get; private set; }
        public event Action<int>? Picked;

        private int _itemHeight;
        private int _pad;

        public SuggestPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = SystemColors.Window;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }   // 눌러도 입력란 포커스 유지
            base.WndProc(ref m);
        }

        public void SetItems(List<(string Address, string Name)> items, Font font, int pad)
        {
            Items.Clear();
            Items.AddRange(items);
            SelectedIndex = 0;
            Font = font;
            _pad = pad;
            _itemHeight = font.Height + pad * 2;
        }

        public int PreferredHeight => _itemHeight * Items.Count + 2;

        public void MoveSelection(int delta)
        {
            if (Items.Count == 0) return;
            SelectedIndex = (SelectedIndex + delta + Items.Count) % Items.Count;
            Invalidate();
        }

        private int IndexAt(Point p) => p.Y < 1 ? -1 : Math.Min((p.Y - 1) / Math.Max(_itemHeight, 1), Items.Count - 1);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Location);
            if (i >= 0 && i != SelectedIndex) { SelectedIndex = i; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && IndexAt(e.Location) is var i and >= 0) Picked?.Invoke(i);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(SystemColors.Window);
            using (var border = new Pen(SystemColors.ControlDark)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            for (int i = 0; i < Items.Count; i++)
            {
                var row = new Rectangle(1, 1 + i * _itemHeight, Width - 2, _itemHeight);
                bool selected = i == SelectedIndex;
                if (selected) g.FillRectangle(SystemBrushes.Highlight, row);
                var (address, name) = Items[i];
                var text = new Rectangle(row.X + _pad * 2, row.Y, row.Width - _pad * 4, row.Height);
                const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                              TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                var fore = selected ? SystemColors.HighlightText : SystemColors.WindowText;
                if (name.Length == 0)
                {
                    TextRenderer.DrawText(g, address, Font, text, fore, flags);
                    continue;
                }
                // "이름  주소" (주소는 흐리게)
                TextRenderer.DrawText(g, name, Font, text, fore, flags);
                int nameWidth = TextRenderer.MeasureText(g, name + "  ", Font, text.Size, flags).Width;
                var addrRect = text with { X = text.X + nameWidth, Width = Math.Max(0, text.Width - nameWidth) };
                TextRenderer.DrawText(g, address, Font, addrRect, selected ? SystemColors.HighlightText : SystemColors.GrayText, flags);
            }
        }
    }
}

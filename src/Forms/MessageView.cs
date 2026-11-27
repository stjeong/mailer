using System.Diagnostics;
using MailClient.Services;
using MimeKit;

namespace MailClient.Forms;

/// <summary>메일 헤더 + 첨부파일 + 본문(HTML)을 표시하는 컨트롤 (미리보기 창 / 읽기 창 공용)</summary>
public class MessageView : UserControl, IDpiAware
{
    private readonly TableLayoutPanel _header;
    private readonly Label _subject;
    private readonly Label _from;
    private readonly Label _to;
    private readonly Label _cc;
    private readonly Label _date;
    private readonly FlowLayoutPanel _attachments;
    private readonly List<(Label Caption, Control Value)> _fields = [];
    private readonly WebBrowser _browser;
    private MimeMessage? _message;
    private Font? _metricsFont;

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding S(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    // WebBrowser 를 처음 만들기 전에 IE 기능 설정 (프로세스 안에서 처음 한 번 읽음)
    static MessageView() => EnableBrowserDpiScaling();

    /// <summary>
    /// 본문(WebBrowser, IE 엔진)이 CSS 픽셀(px)도 화면 배율로 확대하도록 IE 기능 설정 FEATURE_96DPI_PIXEL 을 켭니다.
    /// 설정이 없으면 pt 글자는 배율에 맞게 커지지만 px 로 만든 메일(대부분의 뉴스레터)은 100% 크기 그대로라
    /// 175% 화면에서는 아주 작게 보입니다. 사용자 레지스트리(HKCU)의 이 실행 파일 이름 항목만 씁니다.
    /// </summary>
    private static void EnableBrowserDpiScaling()
    {
        try
        {
            var exe = Path.GetFileName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(exe)) return;
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_96DPI_PIXEL");
            if (key.GetValue(exe) is not int v || v != 1)
                key.SetValue(exe, 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Error("browser dpi feature", ex);
        }
    }

    /// <remarks>
    /// 머리글은 [제목 열 | 값 열] 표 레이아웃이며 행 높이는 내용에 맞춰 자동으로 정해집니다.
    /// 값 레이블 높이는 실제 글꼴로 측정합니다 (<see cref="UpdateMetrics"/>). 고정 픽셀 높이는 화면 배율이 크면 줄이 겹침.
    /// </remarks>
    public MessageView()
    {
        _header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2,
            BackColor = SystemColors.ControlLightLight, Padding = S(12, 8, 12, 8)
        };
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _subject = ValueLabel();
        _subject.Margin = S(0, 0, 0, 6);
        AddRow(null, _subject);

        _from = ValueLabel();
        _to = ValueLabel();
        _cc = ValueLabel();
        _date = ValueLabel();
        _attachments = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = S(0, 2, 0, 0) };

        AddRow("보낸 사람", _from);
        AddRow("받는 사람", _to);
        AddRow("참조", _cc);
        AddRow("날짜", _date);
        AddRow("첨부", _attachments);

        _browser = new WebBrowser
        {
            Dock = DockStyle.Fill,
            ScriptErrorsSuppressed = true,
            IsWebBrowserContextMenuEnabled = true,
            WebBrowserShortcutsEnabled = true,
            AllowWebBrowserDrop = false
        };
        _browser.Navigating += Browser_Navigating;
        _browser.NewWindow += (s, e) => e.Cancel = true;
        _browser.DocumentCompleted += (_, _) => { ApplyBrowserZoom(); FlushPendingHtml(); };
        _browser.HandleCreated += (_, _) => FlushPendingHtml();

        var separator = new Label { Dock = DockStyle.Top, Height = 1, BackColor = SystemColors.ControlDark };

        Controls.Add(_browser);
        Controls.Add(separator);
        Controls.Add(_header);

        Clear();
    }

    private static Label ValueLabel() => new()
    {
        Dock = DockStyle.Fill,
        AutoSize = false,
        AutoEllipsis = true,      // 긴 주소는 … 로 표시 (마우스를 올리면 전체 내용 툴팁)
        UseMnemonic = false,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private void AddRow(string? caption, Control value)
    {
        int row = _header.RowCount++;
        _header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (caption == null)
        {
            _header.Controls.Add(value, 0, row);
            _header.SetColumnSpan(value, 2);
            return;
        }
        var cap = new Label
        {
            Text = caption, AutoSize = true, ForeColor = SystemColors.GrayText, UseMnemonic = false,
            Anchor = AnchorStyles.Left, Margin = S(0, 1, 14, 1)
        };
        if (value is not FlowLayoutPanel) value.Margin = S(0, 1, 0, 1);
        _header.Controls.Add(cap, 0, row);
        _header.Controls.Add(value, 1, row);
        _fields.Add((cap, value));
    }

    private void SetRowVisible(Control value, bool visible)
    {
        value.Visible = visible;
        foreach (var (cap, v) in _fields)
            if (v == value) cap.Visible = visible;
    }

    /// <summary>현재 글꼴(화면 배율 반영)로 줄 높이와 제목 글꼴을 다시 계산</summary>
    private void UpdateMetrics()
    {
        if (ReferenceEquals(_metricsFont, Font)) return;
        _metricsFont = Font;
        // 본문 글꼴보다 2pt 큰 굵은 글꼴. 다른 배율 모니터에서는 Font 크기 자체가 배율에 맞춰 조정돼 있으므로
        // 더하지 않고 비율(9pt → 11pt)로 키움
        _subject.Font = new Font(Font.FontFamily, Font.SizeInPoints * 11f / 9f, FontStyle.Bold);
        int LineHeight(Font f) => TextRenderer.MeasureText("가Agy|", f).Height + S(2);
        _subject.Height = LineHeight(_subject.Font);
        foreach (var l in new[] { _from, _to, _cc, _date }) l.Height = LineHeight(Font);
    }

    // ---------- 본문 표시 ----------
    // WebBrowser 는 이전 문서를 불러오는 중에 DocumentText 를 다시 설정하면 새 내용을 무시하는 경우가 있습니다.
    // (예: 읽기 창에서 생성자의 빈 문서 직후 Load 에서 메일 본문 설정 → 빈 화면)
    // 그래서 불러오는 중이거나 핸들이 아직 없으면 보관해 두었다가 DocumentCompleted/HandleCreated 에서 설정합니다.
    private string? _pendingHtml;
    private DateTime? _documentLoadingSince;
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(3);

    private void SetHtml(string html)
    {
        // 완료 이벤트가 오지 않는 경우를 대비해 일정 시간이 지나면 불러오는 중 상태를 해제
        bool loading = _documentLoadingSince is { } since && DateTime.UtcNow - since < LoadTimeout;
        if (!_browser.IsHandleCreated || loading)
        {
            _pendingHtml = html;   // 가장 마지막 요청만 유지
            return;
        }
        _documentLoadingSince = DateTime.UtcNow;
        _browser.DocumentText = html;
    }

    private void FlushPendingHtml()
    {
        _documentLoadingSince = null;
        if (_pendingHtml == null || !_browser.IsHandleCreated) return;
        var html = _pendingHtml;
        _pendingHtml = null;
        if (IsHandleCreated) BeginInvoke(() => SetHtml(html));   // 현재 이벤트 처리가 끝난 뒤 설정
        else SetHtml(html);
    }

    // ---------- 화면 배율 ----------

    /// <summary>화면 배율이 바뀐 뒤: 머리글 줄 높이·제목 글꼴을 새 글꼴로 다시 계산하고 본문 확대 비율을 맞춤</summary>
    public void OnDpiRescaled(int oldDpi, int newDpi)
    {
        if (_message != null)
        {
            _metricsFont = null;
            UpdateMetrics();
        }
        ApplyBrowserZoom();
    }

    /// <summary>WebBrowser 의 기본 확대 비율(%) = 시스템 배율 (예: 175%)</summary>
    private static int DefaultBrowserZoom => (int)Math.Round(100.0 * DpiForm.SystemDpi / 96);

    /// <summary>본문(WebBrowser)에 마지막으로 적용한 확대 비율(%)</summary>
    internal int BrowserZoom { get; private set; } = DefaultBrowserZoom;

    /// <summary>
    /// WebBrowser(IE 엔진)는 시스템 배율 기준으로만 그리고 모니터별 배율 변경에 반응하지 않으므로,
    /// 시스템 배율과 다른 모니터에서는 확대 비율을 그 모니터 배율로 맞춥니다.
    /// (IE 의 확대 비율은 절대값이며 기본값이 시스템 배율: 시스템 175%, 모니터 100% → 100 으로 설정하면 100/175 크기)
    /// 문서를 새로 불러올 때마다 다시 적용합니다.
    /// </summary>
    private void ApplyBrowserZoom()
    {
        int zoom = (int)Math.Round(100.0 * DeviceDpi / 96);
        if (zoom == DefaultBrowserZoom && BrowserZoom == DefaultBrowserZoom) return;   // 시스템 배율 모니터: 아무것도 하지 않음
        if (!_browser.IsHandleCreated || _browser.ActiveXInstance is not { } ax) return;
        try
        {
            const int OLECMDID_OPTICAL_ZOOM = 63, OLECMDEXECOPT_DONTPROMPTUSER = 2;
            ((dynamic)ax).ExecWB(OLECMDID_OPTICAL_ZOOM, OLECMDEXECOPT_DONTPROMPTUSER, zoom, null);
            BrowserZoom = zoom;
        }
        catch (Exception ex)
        {
            Log.Error("browser zoom", ex);   // 문서를 불러오기 전에는 실패할 수 있음 (DocumentCompleted 에서 다시 적용)
        }
    }

    public MimeMessage? Message => _message;

    /// <summary>HTML 메일을 왼쪽 정렬로 표시 (메일 자체의 가운데 배치 무시)</summary>
    [System.ComponentModel.DefaultValue(true)]
    public bool LeftAlignHtml { get; set; } = true;

    /// <summary>표시 중인 메일을 현재 설정으로 다시 그림</summary>
    public void Rerender()
    {
        if (_message != null) SetHtml(HtmlUtil.BuildDisplayHtml(_message, LeftAlignHtml));
    }

    public void Clear()
    {
        _message = null;
        _header.Visible = false;
        SetHtml("<html><body style='font-family:Malgun Gothic;color:#888;margin:20px'></body></html>");
    }

    public void ShowMessage(MimeMessage message)
    {
        _message = message;
        UpdateMetrics();
        _header.SuspendLayout();
        _subject.Text = string.IsNullOrEmpty(message.Subject) ? "(제목 없음)" : message.Subject;
        _from.Text = MailStore.FormatAddresses(message.From);
        _to.Text = MailStore.FormatAddresses(message.To);
        _cc.Text = MailStore.FormatAddresses(message.Cc);
        SetRowVisible(_cc, message.Cc.Count > 0);
        _date.Text = message.Date == DateTimeOffset.MinValue ? "" : message.Date.LocalDateTime.ToString("yyyy-MM-dd (ddd) HH:mm:ss");

        _attachments.SuspendLayout();
        _attachments.Controls.Clear();
        var atts = message.Attachments.ToList();
        foreach (var att in atts)
        {
            var link = new LinkLabel { Text = AttachmentName(att), AutoSize = true, Tag = att, UseMnemonic = false, Margin = S(0, 1, 12, 1) };
            link.LinkClicked += Attachment_Clicked;
            _attachments.Controls.Add(link);
        }
        _attachments.ResumeLayout();
        SetRowVisible(_attachments, atts.Count > 0);

        _header.Visible = true;
        _header.ResumeLayout(true);

        SetHtml(HtmlUtil.BuildDisplayHtml(message, LeftAlignHtml));
    }

    private static string AttachmentName(MimeEntity entity) => entity switch
    {
        MimePart p => p.FileName ?? p.ContentType.Name ?? "attachment",
        MessagePart m => (m.Message?.Subject ?? "message") + ".eml",
        _ => "attachment"
    };

    private void Attachment_Clicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        if (sender is not LinkLabel { Tag: MimeEntity entity }) return;
        var name = AttachmentName(entity);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

        using var dlg = new SaveFileDialog { FileName = name, Title = "첨부파일 저장" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            using (var stream = File.Create(dlg.FileName))
            {
                if (entity is MessagePart { Message: { } inner }) inner.WriteTo(stream);
                else if (entity is MimePart { Content: { } content }) content.DecodeTo(stream);
            }

            if (MessageBox.Show(this, "저장했습니다. 파일을 여시겠습니까?", "첨부파일", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Browser_Navigating(object? sender, WebBrowserNavigatingEventArgs e)
    {
        var url = e.Url?.ToString() ?? "";
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;

        e.Cancel = true;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("open link", ex); }
        }
    }

    public void Print() => _browser.ShowPrintDialog();

    // ---------- 키보드 스크롤 ----------

    /// <summary>위/아래 화살표 한 번에 스크롤할 양 (CSS 픽셀 = 화면 배율이 적용된 본문 기준, 약 2~3줄)</summary>
    internal const int ArrowScrollStep = 40;

    /// <summary>
    /// 본문에 포커스가 있을 때 위/아래 화살표로 본문을 스크롤합니다.
    /// (WinForms 창이 화살표 키를 컨트롤 사이 이동 키로 먼저 가로채서 WebBrowser 까지 가지 않음. PgUp/PgDn 은 가로채지 않아 원래 동작)
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Up or Keys.Down && _browser.ContainsFocus && ScrollBody(keyData == Keys.Down ? ArrowScrollStep : -ArrowScrollStep))
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>본문을 세로로 스크롤 (CSS 픽셀). 입력 칸에 커서가 있으면 그 칸의 화살표 동작을 그대로 둠</summary>
    private bool ScrollBody(int dy)
    {
        try
        {
            var doc = _browser.Document;
            if (doc?.Window?.DomWindow is not { } window) return false;
            var active = doc.ActiveElement?.TagName?.ToUpperInvariant();
            if (active is "INPUT" or "TEXTAREA" or "SELECT") return false;
            ((dynamic)window).scrollBy(0, dy);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("scroll body", ex);
            return false;
        }
    }
}

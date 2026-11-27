using MailClient.Models;
using MailClient.Services;

namespace MailClient.Forms;

/// <summary>환경 설정 (확인 주기, 정크 메일 필터 및 예외 목록)</summary>
/// <remarks>
/// 고정 높이 대신 AutoSize 행을 쓰고, 남는 픽셀 값은 S() 로 현재 DPI 에 맞게 변환합니다.
/// </remarks>
public class SettingsForm : DpiForm
{
    private readonly AppSettings _settings;

    private readonly NumericUpDown _interval = new() { Minimum = 1, Maximum = 1440, TextAlign = HorizontalAlignment.Right };
    private readonly CheckBox _checkOnStartup = new() { Text = "프로그램 시작 시 메일 확인", AutoSize = true };
    private readonly CheckBox _notify = new() { Text = "새 메일 도착 시 알림 표시", AutoSize = true };
    private readonly CheckBox _confirmDelete = new() { Text = "영구 삭제 전에 확인", AutoSize = true };
    private readonly CheckBox _htmlLeftAlign = new() { Text = "HTML 메일을 왼쪽 정렬로 보기 (메일 자체의 가운데 배치를 무시)", AutoSize = true };
    private readonly TextBox _dbPath = new() { ReadOnly = true };
    private readonly Button _dbBrowse = new() { Text = "찾아보기...", AutoSize = true };
    private readonly Button _dbDefault = new() { Text = "기본 위치", AutoSize = true };
    private string _dbConfigured = "";
    private readonly ToolTip _dbPathTip = new();

    private readonly CheckBox _junkEnabled = new() { Text = "정크 메일 필터 사용", AutoSize = true };
    private readonly NumericUpDown _threshold = new() { Minimum = 1, Maximum = 50, TextAlign = HorizontalAlignment.Right };
    private readonly ListEditor _safeSenders = new("이 주소에서 온 메일은 항상 정상 메일로 처리합니다. (예: boss@company.com)", "이메일 주소");
    private readonly ListEditor _safeDomains = new("이 도메인(하위 도메인 포함)에서 온 메일은 항상 정상 메일로 처리합니다. (예: company.com)", "도메인");
    private readonly ListEditor _blockedSenders = new("이 주소에서 온 메일은 항상 Junk Email 로 이동합니다.", "이메일 주소");
    private readonly ListEditor _blockedDomains = new("이 도메인(하위 도메인 포함)에서 온 메일은 항상 Junk Email 로 이동합니다.", "도메인");
    private readonly ListEditor _keywords = new("제목에 있으면 +3점, 본문에 있으면 +1점 (키워드 점수 최대 8점)", "키워드");

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding S(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        SuspendLayout();
        Text = "설정";
        Font = new Font("Malgun Gothic", 9f);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(S(660), S(560));
        MinimumSize = new Size(S(560), S(460));

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(S(12), S(4)) };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildJunkTab());

        // ---- 하단 버튼 ----
        var ok = new Button { Text = "확인", AutoSize = true, MinimumSize = new Size(S(92), S(30)), Margin = S(0, 0, 8, 0) };
        var cancel = new Button { Text = "취소", AutoSize = true, MinimumSize = new Size(S(92), S(30)), Margin = Padding.Empty, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = S(0, 12, 0, 12), WrapContents = false
        };
        buttons.Controls.AddRange([cancel, ok]);

        var body = new Panel { Dock = DockStyle.Fill, Padding = S(12, 12, 12, 0) };
        body.Controls.Add(tabs);
        body.Controls.Add(buttons);
        Controls.Add(body);
        CancelButton = cancel;   // AcceptButton 은 두지 않음 (목록 입력란의 Enter 와 충돌)

        // 값 로드
        _interval.Value = Math.Clamp(settings.CheckIntervalMinutes, 1, 1440);
        _checkOnStartup.Checked = settings.CheckOnStartup;
        _notify.Checked = settings.ShowNotification;
        _confirmDelete.Checked = settings.ConfirmPermanentDelete;
        _htmlLeftAlign.Checked = settings.HtmlLeftAlign;
        SetDbPath(settings.DatabasePath);
        _junkEnabled.Checked = settings.Junk.Enabled;
        _threshold.Value = Math.Clamp(settings.Junk.Threshold, 1, 50);
        _safeSenders.Items = settings.Junk.SafeSenders;
        _safeDomains.Items = settings.Junk.SafeDomains;
        _blockedSenders.Items = settings.Junk.BlockedSenders;
        _blockedDomains.Items = settings.Junk.BlockedDomains;
        _keywords.Items = settings.Junk.SpamKeywords;

        ok.Click += (_, _) =>
        {
            _settings.CheckIntervalMinutes = (int)_interval.Value;
            _settings.CheckOnStartup = _checkOnStartup.Checked;
            _settings.ShowNotification = _notify.Checked;
            _settings.ConfirmPermanentDelete = _confirmDelete.Checked;
            _settings.HtmlLeftAlign = _htmlLeftAlign.Checked;
            _settings.DatabasePath = _dbConfigured;
            _settings.Junk.Enabled = _junkEnabled.Checked;
            _settings.Junk.Threshold = (int)_threshold.Value;
            _settings.Junk.SafeSenders = _safeSenders.Items;
            _settings.Junk.SafeDomains = _safeDomains.Items;
            _settings.Junk.BlockedSenders = _blockedSenders.Items;
            _settings.Junk.BlockedDomains = _blockedDomains.Items;
            _settings.Junk.SpamKeywords = _keywords.Items;
            DialogResult = DialogResult.OK;
            Close();
        };
        ResumeLayout(true);
    }

    private TableLayoutPanel NewTable(int columns)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, Padding = S(14, 12, 14, 12) };
        return t;
    }

    private static void AddAutoRow(TableLayoutPanel t, Control c, int column = 0, int span = 1)
    {
        int row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(c, column, row);
        if (span > 1) t.SetColumnSpan(c, span);
    }

    /// <summary>"[레이블] [숫자] [설명]" 한 줄</summary>
    private FlowLayoutPanel NumberRow(string label, NumericUpDown number, int width, string suffix)
    {
        number.Width = S(width);
        number.Margin = S(8, 0, 0, 0);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = S(0, 0, 0, 10) };
        row.Controls.AddRange(
        [
            new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty },
            number,
            new Label { Text = suffix, AutoSize = true, Anchor = AnchorStyles.Left, Margin = S(6, 0, 0, 0), ForeColor = SystemColors.GrayText }
        ]);
        return row;
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("일반") { UseVisualStyleBackColor = true };
        var t = NewTable(1);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddAutoRow(t, NumberRow("새 메일 확인 주기", _interval, 80, "분마다 (1~1440, 기본 5분)"));
        foreach (var cb in new[] { _checkOnStartup, _notify, _confirmDelete, _htmlLeftAlign })
        {
            cb.Margin = S(0, 4, 0, 4);
            AddAutoRow(t, cb);
        }

        // ---- 메일 저장소 (SQLite DB) 위치 ----
        AddAutoRow(t, new Label
        {
            Text = "메일 저장소 (SQLite DB 파일)", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = S(0, 18, 0, 4)
        });
        var dbRow = new TableLayoutPanel
        {
            ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = Padding.Empty
        };
        dbRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dbRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dbRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _dbPath.Dock = DockStyle.Fill;
        _dbPath.Margin = S(0, 2, 0, 2);
        foreach (var b in new[] { _dbBrowse, _dbDefault })
        {
            b.MinimumSize = new Size(S(90), S(28));
            b.Margin = S(6, 0, 0, 0);
        }
        dbRow.Controls.Add(_dbPath, 0, 0);
        dbRow.Controls.Add(_dbBrowse, 1, 0);
        dbRow.Controls.Add(_dbDefault, 2, 0);
        AddAutoRow(t, dbRow);
        AddAutoRow(t, new Label
        {
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = S(0, 4, 0, 0),
            Text = "다른 파일을 지정하고 [확인]을 누르면 그 파일의 메일을 불러옵니다.\n" +
                   "파일이 없으면 현재 메일을 복사할지, 빈 저장소로 시작할지 묻습니다."
        });
        _dbBrowse.Click += (_, _) => BrowseDatabase();
        _dbDefault.Click += (_, _) => SetDbPath("");

        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowCount++;
        page.Controls.Add(t);
        return page;
    }

    private void SetDbPath(string configured)
    {
        _dbConfigured = configured.Trim();
        var resolved = AppPaths.ResolveDatabase(_dbConfigured);
        _dbPath.Text = _dbConfigured.Length == 0 ? resolved + "  (기본 위치)" : resolved;
        _dbPathTip.SetToolTip(_dbPath, resolved);   // 긴 경로는 칸에서 잘리므로 전체 경로를 풍선 도움말로
        _dbDefault.Enabled = _dbConfigured.Length > 0;
    }

    private void BrowseDatabase()
    {
        var current = AppPaths.ResolveDatabase(_dbConfigured);
        using var dlg = new SaveFileDialog
        {
            Title = "메일 저장소 파일 선택 (기존 파일을 고르거나 새 파일 이름 입력)",
            Filter = "SQLite 데이터베이스 (*.db)|*.db|모든 파일 (*.*)|*.*",
            DefaultExt = "db",
            OverwritePrompt = false,   // 기존 파일 선택 = 그 파일을 불러옴 (덮어쓰지 않음)
            CheckPathExists = true,
            InitialDirectory = Path.GetDirectoryName(current),
            FileName = Path.GetFileName(current)
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        // 기본 위치를 고르면 빈 값(기본값 따름)으로 저장
        SetDbPath(AppPaths.SamePath(dlg.FileName, AppPaths.DatabaseFile) ? "" : dlg.FileName);
    }

    private TabPage BuildJunkTab()
    {
        var page = new TabPage("정크 메일") { UseVisualStyleBackColor = true };
        var t = NewTable(1);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _junkEnabled.Margin = S(0, 0, 0, 8);
        AddAutoRow(t, _junkEnabled);
        var thr = NumberRow("스팸 판정 기준", _threshold, 64, "점 이상이면 정크 메일 (낮을수록 엄격, 기본 5)");
        thr.Margin = S(0, 0, 0, 6);
        AddAutoRow(t, thr);
        AddAutoRow(t, new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = S(0, 0, 0, 10),
            Text = "허용 목록은 다른 모든 규칙보다 우선합니다. 그다음 차단 목록, 마지막으로 점수 규칙을 적용합니다."
        });

        var junkTabs = new TabControl { Dock = DockStyle.Fill, Margin = Padding.Empty };
        junkTabs.TabPages.Add(Page("허용 주소", _safeSenders));
        junkTabs.TabPages.Add(Page("허용 도메인", _safeDomains));
        junkTabs.TabPages.Add(Page("차단 주소", _blockedSenders));
        junkTabs.TabPages.Add(Page("차단 도메인", _blockedDomains));
        junkTabs.TabPages.Add(Page("스팸 키워드", _keywords));

        int row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(junkTabs, 0, row);

        // 필터를 끄면 하위 설정도 비활성
        void Sync() => thr.Enabled = junkTabs.Enabled = _junkEnabled.Checked;
        _junkEnabled.CheckedChanged += (_, _) => Sync();
        page.HandleCreated += (_, _) => Sync();

        page.Controls.Add(t);
        return page;
    }

    private TabPage Page(string title, Control content)
    {
        var p = new TabPage(title) { Padding = S(10, 10, 10, 10), UseVisualStyleBackColor = true };
        content.Dock = DockStyle.Fill;
        p.Controls.Add(content);
        return p;
    }
}

/// <summary>문자열 목록 편집 컨트롤 (설명 / 입력+추가 / 목록 / 개수+삭제)</summary>
public class ListEditor : UserControl
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended, Sorted = true };
    private readonly TextBox _input = new() { Dock = DockStyle.Fill };
    private readonly Label _count = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    private readonly Button _remove = new() { Text = "선택 삭제", AutoSize = true };

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding S(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    public ListEditor(string description, string itemName)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowCount = 4;

        var desc = new Label { Text = description, AutoSize = true, Dock = DockStyle.Fill, Margin = S(0, 0, 0, 8) };
        t.Controls.Add(desc, 0, 0);
        t.SetColumnSpan(desc, 2);

        _input.PlaceholderText = $"추가할 {itemName} 입력 (쉼표로 여러 개)";
        _input.Margin = S(0, 0, 6, 6);
        var add = new Button { Text = "추가", AutoSize = true, MinimumSize = new Size(S(80), 0), Margin = S(0, 0, 0, 6), Anchor = AnchorStyles.Top | AnchorStyles.Bottom };
        t.Controls.Add(_input, 0, 1);
        t.Controls.Add(add, 1, 1);

        _list.Margin = Padding.Empty;
        t.Controls.Add(_list, 0, 2);
        t.SetColumnSpan(_list, 2);

        _count.Margin = S(0, 6, 0, 0);
        _remove.MinimumSize = new Size(S(96), S(28));
        _remove.Margin = S(0, 6, 0, 0);
        t.Controls.Add(_count, 0, 3);
        t.Controls.Add(_remove, 1, 3);

        Controls.Add(t);

        add.Click += (_, _) => AddInput();
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { AddInput(); e.SuppressKeyPress = true; }
        };
        _remove.Click += (_, _) => RemoveSelected();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
        _list.SelectedIndexChanged += (_, _) => UpdateState();
        UpdateState();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public List<string> Items
    {
        get => _list.Items.Cast<string>().ToList();
        set
        {
            _list.Items.Clear();
            foreach (var v in value.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
                _list.Items.Add(v);
            UpdateState();
        }
    }

    private void UpdateState()
    {
        _count.Text = $"{_list.Items.Count}개";
        _remove.Enabled = _list.SelectedItems.Count > 0;
    }

    private void AddInput()
    {
        // 쉼표/세미콜론으로 여러 개 입력 가능
        foreach (var raw in _input.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var v = raw.Trim();
            if (v.Length == 0) continue;
            if (!_list.Items.Cast<string>().Any(x => x.Equals(v, StringComparison.OrdinalIgnoreCase)))
                _list.Items.Add(v);
        }
        _input.Clear();
        _input.Focus();
        UpdateState();
    }

    private void RemoveSelected()
    {
        foreach (var item in _list.SelectedItems.Cast<object>().ToList())
            _list.Items.Remove(item);
        UpdateState();
    }
}

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
    private readonly CheckBox _runAtStartup = new() { Text = "Windows 시작 시 자동 실행 (Run at startup)", AutoSize = true };
    private readonly ToolTip _runAtStartupTip = new();
    private bool _runAtStartupWas;
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

    /// <summary>규칙 목록 (체크 상자 = 사용 여부)</summary>
    private readonly ListView _ruleList = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true,
        CheckBoxes = true, Margin = Padding.Empty
    };
    private readonly Button _ruleAdd = new() { Text = "추가(&A)...", AutoSize = true };
    private readonly Button _ruleEdit = new() { Text = "편집(&E)...", AutoSize = true };
    private readonly Button _ruleDelete = new() { Text = "삭제(&D)", AutoSize = true };
    private readonly Label _ruleCount = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    /// <summary>[확인] 전까지 편집하는 규칙 사본</summary>
    private readonly List<MailRule> _rules;
    private bool _reloadingRules;

    private readonly MailStore? _store;
    /// <summary>저장된 보낸 사람 목록 (읽은 메일의 보낸 사람, 받는 사람 추천용)</summary>
    private readonly ListView _senderList = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, Margin = Padding.Empty
    };
    private readonly TextBox _senderFilter = new() { Dock = DockStyle.Fill, PlaceholderText = "검색 (이름 또는 주소)" };
    private readonly Button _senderImport = new() { Text = "지금 추가(&I)", AutoSize = true };
    private readonly Button _senderDelete = new() { Text = "삭제(&R)", AutoSize = true };
    private readonly Label _senderCount = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private List<KnownSender> _senders = [];
    /// <summary>[확인]을 누르면 삭제할 주소</summary>
    private readonly HashSet<string> _deletedSenders = new(StringComparer.OrdinalIgnoreCase);

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding S(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    /// <param name="store">메일 저장소 (보낸 사람 탭). null 이면 그 탭을 표시하지 않음</param>
    public SettingsForm(AppSettings settings, MailStore? store = null)
    {
        _settings = settings;
        _store = store;
        _rules = settings.Rules.Select(Clone).ToList();

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
        tabs.TabPages.Add(BuildRuleTab());
        if (store != null) tabs.TabPages.Add(BuildSenderTab(store));

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
        LoadRunAtStartup();
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
        ReloadRules();

        ok.Click += (_, _) =>
        {
            ApplyRunAtStartup();
            _settings.Rules = _rules.Select(Clone).ToList();
            if (_store != null && _deletedSenders.Count > 0) _store.DeleteKnownSenders(_deletedSenders);
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

    /// <summary>자동 실행 체크는 레지스트리(Run 키)의 현재 상태로 표시</summary>
    private void LoadRunAtStartup()
    {
        if (!StartupRegistration.IsSupported)
        {
            _runAtStartup.Enabled = false;
            _runAtStartup.Text += " - msix 설치 버전에서는 지원하지 않음";
            return;
        }
        try
        {
            _runAtStartupWas = _runAtStartup.Checked = StartupRegistration.IsEnabled();
            var registered = StartupRegistration.RegisteredCommand();
            _runAtStartupTip.SetToolTip(_runAtStartup, registered == null
                ? @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run 에 이 프로그램을 등록합니다."
                : "등록된 명령: " + registered +
                  (StartupRegistration.PointsElsewhere() ? "\n(지금 실행 중인 프로그램과 위치가 다릅니다. [확인]을 누르면 이 위치로 바꿉니다.)" : ""));
        }
        catch (Exception ex)
        {
            Log.Error("run at startup read", ex);
            _runAtStartup.Enabled = false;
        }
    }

    /// <summary>[확인]: 바뀌었거나 다른 위치를 가리키면 레지스트리에 반영 (실패해도 다른 설정은 저장)</summary>
    private void ApplyRunAtStartup()
    {
        if (!_runAtStartup.Enabled) return;
        bool want = _runAtStartup.Checked;
        try
        {
            if (want != _runAtStartupWas || (want && StartupRegistration.PointsElsewhere()))
                StartupRegistration.SetEnabled(want);
        }
        catch (Exception ex)
        {
            Log.Error("run at startup write", ex);
            MessageBox.Show(this, "Windows 시작 시 자동 실행 설정을 바꾸지 못했습니다.\n" + ex.Message, "설정",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
        foreach (var cb in new[] { _runAtStartup, _checkOnStartup, _notify, _confirmDelete, _htmlLeftAlign })
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

    private TabPage BuildRuleTab()
    {
        var page = new TabPage("규칙") { UseVisualStyleBackColor = true };
        var t = NewTable(2);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddAutoRow(t, new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Margin = S(0, 0, 0, 8),
            Text = "보낸 사람에게서 새 메일이 오면 지정한 폴더에 .eml 파일로도 저장합니다.\n체크를 끈 규칙은 쓰지 않습니다."
        }, span: 2);

        _ruleList.Columns.Add("보낸 사람", S(210));
        _ruleList.Columns.Add("저장할 폴더", S(300));
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = S(10, 0, 0, 0), Padding = Padding.Empty
        };
        foreach (var b in new[] { _ruleAdd, _ruleEdit, _ruleDelete })
        {
            b.MinimumSize = new Size(S(100), S(30));
            b.Margin = S(0, 0, 0, 8);
        }
        buttons.Controls.AddRange([_ruleAdd, _ruleEdit, _ruleDelete]);

        int row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(_ruleList, 0, row);
        t.Controls.Add(buttons, 1, row);

        _ruleCount.Margin = S(0, 6, 0, 0);
        AddAutoRow(t, _ruleCount, span: 2);
        AddAutoRow(t, new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = S(0, 8, 0, 0),
            Text = "· 보낸 사람(From) 주소가 같으면 저장합니다 (대소문자 무시). 여러 규칙에 맞으면 각 폴더에 모두 저장합니다.\n" +
                   "· 파일 이름: 받은 날짜와 시각 + 제목 (예: 2026-11-27 093015 회의 자료.eml)\n" +
                   "· 메일은 받은 편지함에도 그대로 들어옵니다. 정크 메일로 판정돼도 저장합니다.\n" +
                   "· 규칙을 켠 뒤 새로 받는 메일에만 적용됩니다. 폴더가 없으면 만듭니다."
        }, span: 2);

        // 폴더 열은 남는 폭을 채움
        _ruleList.Resize += (_, _) => FitFillColumn(_ruleList, 1);
        _ruleList.ItemChecked += (_, e) =>
        {
            if (_reloadingRules || e.Item.Tag is not MailRule r) return;
            r.Enabled = e.Item.Checked;
            e.Item.ForeColor = r.Enabled ? SystemColors.WindowText : SystemColors.GrayText;
            UpdateRuleState();
        };
        _ruleList.SelectedIndexChanged += (_, _) => UpdateRuleState();
        _ruleList.DoubleClick += (_, _) => EditRule();
        _ruleList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { EditRule(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteRules(); e.Handled = true; }
        };
        _ruleAdd.Click += (_, _) => AddRule();
        _ruleEdit.Click += (_, _) => EditRule();
        _ruleDelete.Click += (_, _) => DeleteRules();

        page.Controls.Add(t);
        return page;
    }

    private TabPage BuildSenderTab(MailStore store)
    {
        var page = new TabPage("보낸 사람") { UseVisualStyleBackColor = true };
        var t = NewTable(2);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddAutoRow(t, new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Margin = S(0, 0, 0, 8),
            Text = "받은 편지함에서 읽은 메일의 보낸 사람입니다. 메일 쓰기에서 받는 사람을 입력할 때 추천합니다.\n" +
                   "no-reply 주소는 저장하지 않습니다. 삭제해도 그 사람의 메일을 다시 읽으면 다시 저장됩니다."
        }, span: 2);
        _senderFilter.Margin = S(0, 0, 0, 6);
        AddAutoRow(t, _senderFilter);

        _senderList.Columns.Add("이름", S(130));
        _senderList.Columns.Add("주소", S(220));
        _senderList.Columns.Add("읽은 수", S(64), HorizontalAlignment.Right);
        _senderList.Columns.Add("마지막으로 읽음", S(120));
        foreach (var b in new[] { _senderImport, _senderDelete })
        {
            b.MinimumSize = new Size(S(100), S(30));
            b.Margin = S(10, 0, 0, 8);
        }
        new ToolTip().SetToolTip(_senderImport, "현재 계정들의 받은 편지함에서 이미 읽은 메일의 보낸 사람을 바로 추가합니다.");
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        buttons.Controls.AddRange([_senderImport, _senderDelete]);
        int row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(_senderList, 0, row);
        t.Controls.Add(buttons, 1, row);
        _senderCount.Margin = S(0, 6, 0, 0);
        AddAutoRow(t, _senderCount, span: 2);

        _senderList.Resize += (_, _) => FitFillColumn(_senderList, 1);
        _senderList.SelectedIndexChanged += (_, _) => UpdateSenderState();
        _senderList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { DeleteSenders(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                _senderList.BeginUpdate();
                foreach (ListViewItem i in _senderList.Items) i.Selected = true;
                _senderList.EndUpdate();
                e.Handled = e.SuppressKeyPress = true;
            }
        };
        _senderDelete.Click += (_, _) => DeleteSenders();
        _senderImport.Click += (_, _) => ImportSenders(store);
        _senderFilter.TextChanged += (_, _) => ReloadSenders();
        _senderFilter.KeyDown += (_, e) =>
        {
            // ↓ 로 목록으로 이동
            if (e.KeyCode == Keys.Down && _senderList.Items.Count > 0)
            {
                _senderList.Focus();
                if (_senderList.SelectedItems.Count == 0) { _senderList.Items[0].Selected = true; _senderList.Items[0].Focused = true; }
                e.Handled = true;
            }
        };

        try { _senders = store.GetKnownSenders(); }
        catch (Exception ex) { Log.Error("known senders", ex); }
        ReloadSenders();

        page.Controls.Add(t);
        return page;
    }

    /// <summary>[지금 추가]: 받은 편지함의 읽은 메일에서 보낸 사람을 바로 저장하고 목록을 다시 읽음</summary>
    private void ImportSenders(MailStore store)
    {
        (int Added, int Existing) result;
        try
        {
            Cursor = Cursors.WaitCursor;
            result = store.ImportSendersFromReadInbox(_settings.Accounts.Select(a => a.Id).ToList());
            _senders = store.GetKnownSenders();
        }
        catch (Exception ex)
        {
            Log.Error("import senders", ex);
            MessageBox.Show(this, "보낸 사람을 추가하지 못했습니다.\n" + ex.Message, "보낸 사람", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            Cursor = Cursors.Default;
        }
        ReloadSenders();
        Log.Write($"Known senders imported from read Inbox mail: {result.Added} added, {result.Existing} already saved");

        string message;
        if (result.Added == 0 && result.Existing == 0)
            message = "추가할 보낸 사람이 없습니다.\n받은 편지함에 읽은 메일이 없거나 모두 no-reply 주소입니다.";
        else
        {
            message = result.Added > 0 ? $"보낸 사람 {result.Added}명을 새로 추가했습니다." : "새로 추가할 보낸 사람이 없습니다.";
            if (result.Existing > 0) message += $"\n이미 저장돼 있던 {result.Existing}명은 중복으로 추가하지 않았습니다.";
            int pending = _deletedSenders.Count;
            if (pending > 0) message += $"\n\n삭제 예정인 {pending}명은 목록에 다시 표시하지 않으며, [확인]을 누르면 삭제됩니다.";
        }
        MessageBox.Show(this, message, "보낸 사람", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ReloadSenders()
    {
        var q = _senderFilter.Text.Trim();
        var shown = _senders.Where(x => !_deletedSenders.Contains(x.Address) &&
            (q.Length == 0 || x.Address.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)));
        _senderList.BeginUpdate();
        try
        {
            _senderList.Items.Clear();
            foreach (var x in shown)
            {
                var last = x.LastRead == default ? "" : x.LastRead.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                _senderList.Items.Add(new ListViewItem([x.Name, x.Address, x.ReadCount.ToString(), last]) { Tag = x });
            }
        }
        finally { _senderList.EndUpdate(); }
        UpdateSenderState();
    }

    private void UpdateSenderState()
    {
        int total = _senders.Count - _deletedSenders.Count;
        var text = _senderList.Items.Count == total ? $"{total}명" : $"{total}명 중 {_senderList.Items.Count}명 표시";
        if (_deletedSenders.Count > 0) text += $"  ·  삭제 예정 {_deletedSenders.Count}명 ([확인]을 누르면 삭제)";
        _senderCount.Text = total == 0 && _deletedSenders.Count == 0 ? "저장된 보낸 사람이 없습니다." : text;
        _senderDelete.Enabled = _senderList.SelectedItems.Count > 0;
    }

    private void DeleteSenders()
    {
        var selected = _senderList.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<KnownSender>().ToList();
        if (selected.Count == 0) return;
        int first = _senderList.SelectedIndices[0];
        foreach (var x in selected) _deletedSenders.Add(x.Address);
        ReloadSenders();
        if (_senderList.Items.Count > 0)
        {
            var next = _senderList.Items[Math.Min(first, _senderList.Items.Count - 1)];
            next.Selected = next.Focused = true;
            next.EnsureVisible();
        }
        _senderList.Focus();
    }

    private static MailRule Clone(MailRule r) => new() { Enabled = r.Enabled, Sender = r.Sender, SaveDirectory = r.SaveDirectory };

    /// <summary>
    /// 배율이 바뀌면 열 너비가 비율대로 조정되므로 폴더 열을 목록 폭에 다시 맞춤.
    /// ListView 는 배율 변경 뒤 가로 스크롤바를 다시 계산하지 않아, 너비가 맞아도 스크롤바가 남으므로
    /// 너비를 한 번 다른 값으로 바꿨다가 맞춰 다시 계산하게 합니다.
    /// </summary>
    protected override void OnDpiRescaled(int oldDpi, int newDpi)
    {
        base.OnDpiRescaled(oldDpi, newDpi);
        FitFillColumn(_ruleList, 1, force: true);
        FitFillColumn(_senderList, 1, force: true);
    }

    /// <summary>목록의 fill 번째 열이 나머지 폭을 채우도록</summary>
    private void FitFillColumn(ListView list, int fill, bool force = false)
    {
        if (list.Columns.Count <= fill) return;
        int others = list.Columns.Cast<ColumnHeader>().Where(c => c.Index != fill).Sum(c => c.Width);
        int rest = list.ClientSize.Width - others;
        if (rest <= S(80)) return;
        if (force) list.Columns[fill].Width = rest - 1;
        list.Columns[fill].Width = rest;
    }

    private void ReloadRules(MailRule? select = null)
    {
        _reloadingRules = true;
        _ruleList.BeginUpdate();
        try
        {
            _ruleList.Items.Clear();
            foreach (var r in _rules)
            {
                var item = new ListViewItem([r.Sender, r.SaveDirectory])
                {
                    Tag = r, Checked = r.Enabled, ForeColor = r.Enabled ? SystemColors.WindowText : SystemColors.GrayText
                };
                _ruleList.Items.Add(item);
                if (ReferenceEquals(r, select)) { item.Selected = true; item.Focused = true; }
            }
        }
        finally
        {
            _ruleList.EndUpdate();
            _reloadingRules = false;
        }
        _ruleList.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.EnsureVisible();
        UpdateRuleState();
    }

    private void UpdateRuleState()
    {
        int selected = _ruleList.SelectedItems.Count;
        _ruleEdit.Enabled = selected == 1;
        _ruleDelete.Enabled = selected > 0;
        int on = _rules.Count(r => r.Enabled);
        _ruleCount.Text = _rules.Count == 0 ? "규칙 없음 - [추가]로 만드세요." : $"규칙 {_rules.Count}개 (사용 {on}개)";
    }

    /// <summary>같은 보낸 사람 + 같은 폴더 규칙이 이미 있으면 오류 문구</summary>
    private string? CheckDuplicate(MailRule candidate, MailRule? self) => MailRules.DuplicateError(_rules, candidate, self);

    private void AddRule()
    {
        using var dlg = new RuleEditForm(null, r => CheckDuplicate(r, null));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _rules.Add(dlg.Rule);
        ReloadRules(dlg.Rule);
        _ruleList.Focus();
    }

    private void EditRule()
    {
        if (_ruleList.SelectedItems.Count != 1 || _ruleList.SelectedItems[0].Tag is not MailRule rule) return;
        using var dlg = new RuleEditForm(Clone(rule), r => CheckDuplicate(r, rule));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _rules[_rules.IndexOf(rule)] = dlg.Rule;
        ReloadRules(dlg.Rule);
        _ruleList.Focus();
    }

    private void DeleteRules()
    {
        var selected = _ruleList.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<MailRule>().ToList();
        if (selected.Count == 0) return;
        int first = _ruleList.SelectedIndices[0];
        foreach (var r in selected) _rules.Remove(r);
        ReloadRules(_rules.Count == 0 ? null : _rules[Math.Min(first, _rules.Count - 1)]);
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

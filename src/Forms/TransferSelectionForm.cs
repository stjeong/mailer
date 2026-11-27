namespace MailClient.Forms;

/// <summary>선택 창의 한 행 (계정 1개)</summary>
public sealed record TransferRow(string Name, string Email, string Protocol, string? Status = null, bool StatusIsWarning = false);

/// <summary>
/// 내보내기/가져오기할 계정과 "도구 > 설정" 포함 여부를 고르는 창.
/// 계정 또는 설정 중 하나 이상을 골라야 [확인]이 켜집니다.
/// </summary>
public class TransferSelectionForm : DpiForm
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true,
        HideSelection = false, MultiSelect = false
    };
    private readonly CheckBox _settings = new() { AutoSize = true };
    private readonly Label _settingsNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _count = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    private readonly Button _ok = new() { AutoSize = true };
    private readonly Button _cancel = new() { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly bool _hasStatus;

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding S(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    /// <summary>체크된 계정의 순번 (생성자에 넘긴 rows 기준)</summary>
    public List<int> CheckedIndices => _list.CheckedItems.Cast<ListViewItem>().Select(i => i.Index).ToList();
    public bool IncludeSettings => _settings.Enabled && _settings.Checked;

    /// <param name="settingsAvailable">설정을 포함할 수 있는지 (가져오기: 파일에 설정이 있는지)</param>
    /// <param name="settingsChecked">설정 체크박스 초기값</param>
    /// <param name="settingsNote">설정 체크박스 아래 안내 문구</param>
    public TransferSelectionForm(string title, string intro, IReadOnlyList<TransferRow> rows, bool checkAll,
        bool settingsAvailable, bool settingsChecked, string settingsNote, string okText)
    {
        _hasStatus = rows.Any(r => r.Status != null);

        SuspendLayout();
        Text = title;
        Font = new Font("Malgun Gothic", 9f);
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        // 폭: 설정 체크 상자의 긴 설명이 100% 배율(글자가 상대적으로 넓게 그려짐)에서도 잘리지 않을 만큼
        ClientSize = new Size(S(_hasStatus ? 740 : 640), S(470));
        MinimumSize = new Size(S(480), S(380));

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = S(16, 14, 16, 12) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Auto(Control c) { t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.Controls.Add(c, 0, t.RowCount++); }

        Auto(new Label { Text = intro, AutoSize = true, Dock = DockStyle.Fill, Margin = S(0, 0, 0, 10) });

        // 계정 목록 머리: "계정" + 모두 선택/해제
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = S(0, 0, 0, 4) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var selectAll = new Button { Text = "모두 선택", AutoSize = true, MinimumSize = new Size(S(84), 0), Margin = S(0, 0, 6, 0) };
        var selectNone = new Button { Text = "모두 해제", AutoSize = true, MinimumSize = new Size(S(84), 0), Margin = Padding.Empty };
        header.Controls.Add(new Label { Text = "계정", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Font = new Font(Font, FontStyle.Bold), Margin = Padding.Empty }, 0, 0);
        header.Controls.Add(selectAll, 1, 0);
        header.Controls.Add(selectNone, 2, 0);
        Auto(header);

        _list.Columns.Add("계정 이름", S(150));
        _list.Columns.Add("이메일 주소", S(200));
        _list.Columns.Add("방식", S(60));
        if (_hasStatus) _list.Columns.Add("상태", S(150));
        foreach (var r in rows)
        {
            var item = new ListViewItem(_hasStatus ? [r.Name, r.Email, r.Protocol, r.Status ?? ""] : [r.Name, r.Email, r.Protocol])
            {
                Checked = checkAll, UseItemStyleForSubItems = false
            };
            if (r.StatusIsWarning && _hasStatus) item.SubItems[3].ForeColor = Color.FromArgb(176, 96, 0);
            _list.Items.Add(item);
        }
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _list.Margin = Padding.Empty;
        t.Controls.Add(_list, 0, t.RowCount++);

        _count.Margin = S(0, 6, 0, 0);
        Auto(_count);

        // 설정 포함
        var settingsBox = new GroupBox { Text = "설정", Dock = DockStyle.Fill, AutoSize = true, Padding = S(10, 6, 10, 8), Margin = S(0, 12, 0, 0) };
        var sp = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _settings.Text = "\"도구 > 설정\" 내용 포함 (메일 확인 주기, 알림, 정크 메일 필터와 허용/차단 목록, 키워드)";
        _settings.Enabled = settingsAvailable;
        _settings.Checked = settingsAvailable && settingsChecked;
        _settings.Margin = Padding.Empty;
        _settingsNote.Text = settingsNote;
        _settingsNote.Margin = S(18, 2, 0, 0);
        _settingsNote.Visible = settingsNote.Length > 0;
        sp.Controls.AddRange([_settings, _settingsNote]);
        settingsBox.Controls.Add(sp);
        Auto(settingsBox);

        // 버튼
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = S(0, 14, 0, 0) };
        _ok.Text = okText;
        foreach (var b in new[] { _ok, _cancel }) b.MinimumSize = new Size(S(92), S(30));
        _cancel.Margin = Padding.Empty;
        _ok.Margin = S(0, 0, 8, 0);
        buttons.Controls.AddRange([_cancel, _ok]);
        Auto(buttons);

        Controls.Add(t);
        AcceptButton = _ok;
        CancelButton = _cancel;

        selectAll.Click += (_, _) => SetAll(true);
        selectNone.Click += (_, _) => SetAll(false);
        selectAll.Enabled = selectNone.Enabled = rows.Count > 0;
        _list.ItemChecked += (_, _) => UpdateState();
        _settings.CheckedChanged += (_, _) => UpdateState();
        _ok.Click += (_, _) =>
        {
            if (!_ok.Enabled) return;
            DialogResult = DialogResult.OK;
            Close();
        };

        UpdateState();
        ResumeLayout(true);
    }

    private void SetAll(bool value)
    {
        _list.BeginUpdate();
        foreach (ListViewItem i in _list.Items) i.Checked = value;
        _list.EndUpdate();
        UpdateState();
    }

    private void UpdateState()
    {
        int n = _list.CheckedItems.Count;
        _count.Text = _list.Items.Count == 0
            ? "계정이 없습니다."
            : $"{_list.Items.Count}개 중 {n}개 선택" + (IncludeSettings ? " + 설정" : "");
        _ok.Enabled = n > 0 || IncludeSettings;
    }
}

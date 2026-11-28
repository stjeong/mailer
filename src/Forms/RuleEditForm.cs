using MailClient.Models;
using MailClient.Services;

namespace MailClient.Forms;

/// <summary>규칙 하나 추가/편집 (보낸 사람 → 저장할 폴더)</summary>
public class RuleEditForm : DpiForm
{
    private readonly CheckBox _enabled = new() { Text = "이 규칙 사용", AutoSize = true };
    private readonly TextBox _sender = new() { PlaceholderText = "예: boss@company.com" };
    private readonly TextBox _dir = new() { PlaceholderText = @"예: D:\Mail\boss" };
    private readonly Button _browse = new() { Text = "찾아보기...", AutoSize = true };
    private readonly Func<MailRule, string?>? _validate;

    /// <summary>[확인]으로 닫았을 때의 규칙 (보낸 사람은 주소만 남김)</summary>
    public MailRule Rule { get; private set; }

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding S(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    /// <param name="rule">편집할 규칙 (null 이면 새 규칙)</param>
    /// <param name="validate">추가 검사 (예: 중복). 오류 문구, 문제 없으면 null</param>
    /// <param name="isNew">rule 을 미리 채운 새 규칙으로 여김 (제목 "규칙 추가")</param>
    public RuleEditForm(MailRule? rule, Func<MailRule, string?>? validate = null, bool isNew = false)
    {
        _validate = validate;
        Rule = rule ?? new MailRule();

        SuspendLayout();
        Text = rule == null || isNew ? "규칙 추가" : "규칙 편집";
        Font = new Font("Malgun Gothic", 9f);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var t = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = S(16, 14, 16, 12)
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _enabled.Margin = S(0, 0, 0, 10);
        t.Controls.Add(_enabled, 0, 0);
        t.SetColumnSpan(_enabled, 3);

        Label FieldLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = S(0, 0, 10, 8) };
        _sender.Width = S(420);
        _sender.Margin = S(0, 0, 0, 8);
        t.Controls.Add(FieldLabel("보낸 사람"), 0, 1);
        t.Controls.Add(_sender, 1, 1);
        t.SetColumnSpan(_sender, 2);
        _sender.Dock = DockStyle.Fill;

        _dir.Width = S(326);
        _dir.Margin = S(0, 2, 0, 8);
        _browse.MinimumSize = new Size(S(88), S(28));
        _browse.Margin = S(6, 0, 0, 8);
        t.Controls.Add(FieldLabel("저장할 폴더"), 0, 2);
        t.Controls.Add(_dir, 1, 2);
        t.Controls.Add(_browse, 2, 2);

        var hint = new Label
        {
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = S(0, 2, 0, 14),
            Text = "보낸 사람(From) 주소가 같은 새 메일을 이 폴더에 .eml 파일로 저장합니다.\n폴더가 없으면 만듭니다."
        };
        t.Controls.Add(hint, 0, 3);
        t.SetColumnSpan(hint, 3);

        var ok = new Button { Text = "확인", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        foreach (var b in new[] { ok, cancel })
        {
            b.MinimumSize = new Size(S(84), S(28));
            b.Margin = S(8, 0, 0, 0);
        }
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false
        };
        buttons.Controls.AddRange([cancel, ok]);
        t.Controls.Add(buttons, 0, 4);
        t.SetColumnSpan(buttons, 3);

        Controls.Add(t);
        AcceptButton = ok;
        CancelButton = cancel;

        _enabled.Checked = Rule.Enabled;
        _sender.Text = Rule.Sender;
        _dir.Text = Rule.SaveDirectory;

        _browse.Click += (_, _) => Browse();
        ok.Click += (_, _) =>
        {
            if (!TryRead()) DialogResult = DialogResult.None;
        };
        Shown += (_, _) => (_sender.Text.Length == 0 ? _sender : _dir).Focus();
        ResumeLayout(true);
    }

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = ".eml 파일을 저장할 폴더",
            UseDescriptionForTitle = true,
            InitialDirectory = MailRules.ResolveDirectory(_dir.Text) is { } d && Directory.Exists(d) ? d : "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _dir.Text = dlg.SelectedPath;
    }

    private bool TryRead()
    {
        var sender = MailRules.NormalizeAddress(_sender.Text);
        string? problem = null;
        Control focus = _sender;
        if (sender == null) problem = "보낸 사람의 이메일 주소를 입력하세요. (예: boss@company.com)";
        else if (MailRules.ResolveDirectory(_dir.Text) == null) { problem = @"저장할 폴더를 전체 경로로 지정하세요. (예: D:\Mail\boss)"; focus = _dir; }

        var rule = new MailRule { Enabled = _enabled.Checked, Sender = sender ?? "", SaveDirectory = _dir.Text.Trim() };
        problem ??= _validate?.Invoke(rule);
        if (problem != null)
        {
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            focus.Focus();
            return false;
        }
        Rule = rule;
        return true;
    }
}

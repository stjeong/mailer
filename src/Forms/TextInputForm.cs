namespace MailClient.Forms;

/// <summary>한 줄 입력 대화창 (보관함 폴더 이름 등)</summary>
public class TextInputForm : DpiForm
{
    private readonly TextBox _text = new();
    private readonly Button _ok = new() { Text = "확인", AutoSize = true, DialogResult = DialogResult.OK };
    private readonly Button _cancel = new() { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Func<string, string?>? _validate;

    public string Value => _text.Text.Trim();

    private int S(int logical) => LogicalToDeviceUnits(logical);

    /// <param name="validate">입력값 검사 (오류 문구, 문제 없으면 null)</param>
    public TextInputForm(string title, string prompt, string initial = "", Func<string, string?>? validate = null)
    {
        _validate = validate;
        Text = title;
        Font = new Font("Malgun Gothic", 9f);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
            Padding = new Padding(S(16), S(14), S(16), S(12))
        };
        var label = new Label { Text = prompt, AutoSize = true, Margin = new Padding(0, 0, 0, S(6)) };
        _text.Text = initial;
        _text.Width = S(320);
        _text.Margin = new Padding(0, 0, 0, S(14));
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Padding.Empty };
        foreach (var b in new[] { _cancel, _ok })
        {
            b.MinimumSize = new Size(S(84), S(28));
            b.Margin = new Padding(S(8), 0, 0, 0);
        }
        buttons.Controls.AddRange([_cancel, _ok]);
        root.Controls.Add(label);
        root.Controls.Add(_text);
        root.Controls.Add(buttons);
        Controls.Add(root);
        AcceptButton = _ok;
        CancelButton = _cancel;

        _ok.Enabled = Value.Length > 0;
        _text.TextChanged += (_, _) => _ok.Enabled = Value.Length > 0;
        _ok.Click += (_, _) =>
        {
            var error = _validate?.Invoke(Value);
            if (error == null) return;
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            _text.Focus();
        };
        Shown += (_, _) => { _text.Focus(); _text.SelectAll(); };
    }
}

using System.Text.Json;
using MailClient.Models;
using MailClient.Services;
using MailKit.Security;

namespace MailClient.Forms;

/// <summary>메일 계정 추가 / 편집</summary>
/// <remarks>
/// 모든 행은 AutoSize 로 내용 높이에 맞추고, 픽셀 값은 <see cref="S(int)"/> 로 현재 DPI 에 맞게 변환합니다.
/// 창 크기도 내용에 맞춰 자동으로 늘거나 줄어듭니다 (OAuth 행 표시/숨김 등).
/// </remarks>
public class AccountForm : DpiForm
{
    private sealed record Choice<T>(T Value, string Name)
    {
        public override string ToString() => Name;
    }

    private static readonly Choice<SecureSocketOptions>[] SecurityChoices =
    [
        new(SecureSocketOptions.SslOnConnect, "SSL/TLS"),
        new(SecureSocketOptions.StartTls, "STARTTLS"),
        new(SecureSocketOptions.StartTlsWhenAvailable, "STARTTLS (가능 시)"),
        new(SecureSocketOptions.Auto, "자동"),
        new(SecureSocketOptions.None, "없음"),
    ];

    private static readonly Choice<AuthMethod>[] AuthChoices =
    [
        new(AuthMethod.Password, "비밀번호 / 앱 비밀번호"),
        new(AuthMethod.OAuthGoogle, "OAuth 2.0 - Google (Gmail)"),
        new(AuthMethod.OAuthMicrosoft, "OAuth 2.0 - Microsoft (Outlook)"),
    ];

    // 논리 픽셀(96 DPI 기준) 치수
    private const int FieldWidth = 250;   // 서버 주소 칸 너비
    private const int PortWidth = 72;
    private const int SecurityWidth = 150;

    private static readonly Color AccentColor = Color.FromArgb(0, 84, 166);
    private static readonly Color NoteBack = Color.FromArgb(255, 248, 225);
    private static readonly Color NoteFore = Color.FromArgb(120, 72, 0);

    private readonly MailAccount _account;

    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _note = new() { AutoSize = true };
    private readonly TextBox _displayName = new();
    private readonly TextBox _email = new() { PlaceholderText = "user@example.com" };
    private readonly TextBox _senderName = new() { PlaceholderText = "받는 사람에게 표시될 이름" };
    private readonly ComboBox _protocol = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _inHost = new();
    private readonly NumericUpDown _inPort = new() { Minimum = 1, Maximum = 65535, TextAlign = HorizontalAlignment.Right };
    private readonly ComboBox _inSecurity = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _smtpHost = new();
    private readonly NumericUpDown _smtpPort = new() { Minimum = 1, Maximum = 65535, TextAlign = HorizontalAlignment.Right };
    private readonly ComboBox _smtpSecurity = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _auth = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _userName = new() { PlaceholderText = "비워 두면 이메일 주소 사용" };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _showPassword = new() { Text = "비밀번호 표시", AutoSize = true };
    /// <summary>앱 기본 클라이언트 대신 직접 등록한 클라이언트 사용 (고급). 기본값이 없는 서비스면 항상 켜짐</summary>
    private readonly CheckBox _customClient = new() { AutoSize = true };
    private readonly TextBox _clientId = new();
    private readonly TextBox _clientSecret = new() { UseSystemPasswordChar = true, PlaceholderText = "Google 데스크톱 앱 클라이언트만 필요" };
    private readonly Button _oauthLogin = new() { Text = "OAuth 로그인...", AutoSize = true };
    private readonly Label _oauthStatus = new() { AutoSize = true };
    private readonly FlowLayoutPanel _oauthRow = new() { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
    private readonly CheckBox _leaveOnServer = new() { Text = "서버에 메시지 사본 남기기 (POP3)", AutoSize = true, Checked = true };
    private readonly CheckBox _syncToServer = new() { Text = "이동·삭제·읽음 표시를 서버에도 반영 (IMAP)", AutoSize = true };
    private readonly NumericUpDown _initialLimit = new() { Minimum = 1, Maximum = 100000, TextAlign = HorizontalAlignment.Right };
    private readonly CheckBox _enabled = new() { Text = "이 계정의 메일 확인 사용", AutoSize = true, Checked = true };
    private readonly Button _test = new() { Text = "연결 테스트", AutoSize = true };
    private readonly Button _ok = new() { Text = "확인", AutoSize = true };
    private readonly Button _cancel = new() { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };

    private readonly TableLayoutPanel _table = new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, Dock = DockStyle.Top
    };
    private readonly Dictionary<Control, Label> _labels = new();
    private bool _loading;
    private CancellationTokenSource? _oauthCts;

    public MailAccount Account => _account;

    /// <summary>96 DPI 기준 논리 픽셀 → 현재 DPI 픽셀</summary>
    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding S(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    public AccountForm(MailAccount? existing)
    {
        _account = existing == null
            ? new MailAccount()
            : JsonSerializer.Deserialize<MailAccount>(JsonSerializer.Serialize(existing, SettingsService.JsonOptions), SettingsService.JsonOptions)!;

        SuspendLayout();
        Text = existing == null ? "메일 계정 추가" : "메일 계정 편집";
        Font = new Font("Malgun Gothic", 9f);
        AutoScaleMode = AutoScaleMode.None;   // 크기는 S() 로 직접 DPI 변환
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;


        _preset.Items.AddRange(ProviderPresets.All);
        _protocol.Items.AddRange([new Choice<IncomingProtocol>(IncomingProtocol.Imap, "IMAP"), new Choice<IncomingProtocol>(IncomingProtocol.Pop3, "POP3")]);
        _inSecurity.Items.AddRange(SecurityChoices);
        _smtpSecurity.Items.AddRange(SecurityChoices);
        _auth.Items.AddRange(AuthChoices);

        BuildLayout();

        LoadFromAccount(existing == null);

        _preset.SelectedIndexChanged += (_, _) => ApplyPreset();
        _protocol.SelectedIndexChanged += (_, _) => ProtocolChanged();
        _inPort.ValueChanged += (_, _) => MatchSecurityToPort(_inPort, _inSecurity);
        _smtpPort.ValueChanged += (_, _) => MatchSecurityToPort(_smtpPort, _smtpSecurity);
        _auth.SelectedIndexChanged += (_, _) => UpdateAuthVisibility();
        _clientId.TextChanged += (_, _) => UpdateOAuthStatus();
        _customClient.CheckedChanged += (_, _) => { UpdateAuthVisibility(); UpdateOAuthStatus(); };
        _email.Leave += (_, _) => GuessFromEmail();
        _showPassword.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_showPassword.Checked;
        _oauthLogin.Click += async (_, _) => await OAuthLoginAsync();
        _test.Click += async (_, _) => await TestAsync();
        _ok.Click += (_, _) => OkClicked();
        FormClosing += (_, _) => _oauthCts?.Cancel();   // 로그인 대기 중 닫으면 대기 중단

        ResumeLayout(true);
    }

    // ---------- 레이아웃 ----------

    private void BuildLayout()
    {
        // 열: 0 = 레이블, 1 = 서버 주소(일반 입력 칸은 1~3 열을 차지), 2 = 포트, 3 = 보안
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(FieldWidth)));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (var c in new Control[] { _inPort, _smtpPort }) c.Width = S(PortWidth);
        foreach (var c in new Control[] { _inSecurity, _smtpSecurity }) c.Width = S(SecurityWidth);
        _initialLimit.Width = S(90);

        _note.Padding = S(8, 6, 8, 6);
        _note.BackColor = NoteBack;
        _note.ForeColor = NoteFore;
        _note.MaximumSize = new Size(S(FieldWidth + PortWidth + SecurityWidth + 16), 0);

        AddSection("서비스", first: true);
        AddRow("메일 서비스", _preset);
        AddRow("", _note);

        AddSection("사용자 정보");
        AddRow("계정 이름", _displayName);
        AddRow("이메일 주소", _email);
        AddRow("보내는 사람 이름", _senderName);

        AddSection("서버 정보");
        AddRow("받는 메일 방식", _protocol, span: 1);
        AddServerCaptions();
        AddServerRow("받는 메일 서버", _inHost, _inPort, _inSecurity);
        AddServerRow("보내는 메일 (SMTP)", _smtpHost, _smtpPort, _smtpSecurity);

        AddSection("로그인");
        AddRow("인증 방식", _auth);
        AddRow("사용자 이름", _userName);
        AddRow("비밀번호", _password, span: 1, extra: _showPassword);
        _oauthLogin.MinimumSize = new Size(S(120), S(28));
        _oauthLogin.Margin = Padding.Empty;
        _oauthStatus.Margin = S(10, 0, 0, 0);
        _oauthStatus.Anchor = AnchorStyles.Left;
        _oauthRow.Controls.AddRange([_oauthLogin, _oauthStatus]);
        AddRow("", _oauthRow);
        AddRow("", _customClient);
        AddRow("클라이언트 ID", _clientId);
        AddRow("클라이언트 비밀", _clientSecret);

        AddSection("옵션");
        AddRow("", _leaveOnServer);
        AddRow("", _syncToServer);
        var limitRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _initialLimit.Margin = Padding.Empty;
        limitRow.Controls.AddRange([_initialLimit, new Label
        {
            Text = "개  (처음 연결할 때 가져올 최근 메일 수)", AutoSize = true, ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Left, Margin = S(6, 0, 0, 0)
        }]);
        AddRow("최초 동기화", limitRow);
        AddRow("", _enabled);

        // ---- 하단 버튼 ----
        var line = new Label { AutoSize = false, Height = 1, Dock = DockStyle.Top, BackColor = SystemColors.ControlDark };
        var buttons = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 4,
            Padding = S(0, 12, 0, 14)
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var b in new[] { _test, _ok, _cancel })
        {
            b.MinimumSize = new Size(S(92), S(30));
            b.Margin = Padding.Empty;
        }
        _ok.Margin = S(0, 0, 8, 0);
        buttons.Controls.Add(_test, 0, 0);
        buttons.Controls.Add(_ok, 2, 0);
        buttons.Controls.Add(_cancel, 3, 0);

        // 창 AutoSize 는 Dock 되지 않은 자식 기준으로 계산되므로, 전체를 비도킹 루트 패널 하나에 담음
        var root = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
            Location = Point.Empty, Padding = S(18, 8, 18, 0), Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var c in new Control[] { _table, line, buttons })
        {
            c.Dock = DockStyle.Fill;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(c);
        }
        _table.Margin = S(0, 0, 0, 16);
        line.Margin = Padding.Empty;
        buttons.Margin = Padding.Empty;
        Controls.Add(root);

        AcceptButton = _ok;
        CancelButton = _cancel;
    }

    private Label MakeLabel(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = S(0, 4, 16, 4)
    };

    private void AddSection(string title, bool first = false)
    {
        int row = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = new Label
        {
            Text = title, AutoSize = true, Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold), ForeColor = AccentColor,
            Margin = S(0, first ? 6 : 16, 0, 2)
        };
        _table.Controls.Add(header, 0, row);
        _table.SetColumnSpan(header, 4);

        row = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var line = new Label
        {
            AutoSize = false, Height = 1, Dock = DockStyle.Fill, BackColor = Color.FromArgb(200, 214, 230), Margin = S(0, 0, 0, 6)
        };
        _table.Controls.Add(line, 0, row);
        _table.SetColumnSpan(line, 4);
    }

    /// <param name="span">입력 칸이 차지할 열 수 (1~3)</param>
    /// <param name="extra">같은 행의 나머지 열에 둘 컨트롤</param>
    private void AddRow(string label, Control control, int span = 3, Control? extra = null)
    {
        int row = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var lbl = MakeLabel(label);
        _table.Controls.Add(lbl, 0, row);

        if (control is TextBox or ComboBox) control.Dock = DockStyle.Fill;
        else control.Anchor = AnchorStyles.Left;
        control.Margin = S(0, 3, 0, 3);
        _table.Controls.Add(control, 1, row);
        _table.SetColumnSpan(control, span);

        if (extra != null)
        {
            extra.Anchor = AnchorStyles.Left;
            extra.Margin = S(10, 3, 0, 3);
            _table.Controls.Add(extra, 1 + span, row);
            _table.SetColumnSpan(extra, 3 - span);
        }
        _labels[control] = lbl;
    }

    private void AddServerCaptions()
    {
        int row = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label Caption(string t, int left) => new()
        {
            Text = t, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = S(left, 6, 0, 0),
            Font = new Font(Font.FontFamily, 8.25f)
        };
        _table.Controls.Add(Caption("서버 주소", 0), 1, row);
        _table.Controls.Add(Caption("포트", 8), 2, row);
        _table.Controls.Add(Caption("보안 연결", 8), 3, row);
    }

    private void AddServerRow(string label, TextBox host, NumericUpDown port, ComboBox security)
    {
        int row = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.Controls.Add(MakeLabel(label), 0, row);
        host.Dock = DockStyle.Fill;
        host.Margin = S(0, 3, 0, 3);
        port.Anchor = AnchorStyles.Left;
        port.Margin = S(8, 3, 0, 3);
        security.Anchor = AnchorStyles.Left;
        security.Margin = S(8, 3, 0, 3);
        _table.Controls.Add(host, 1, row);
        _table.Controls.Add(port, 2, row);
        _table.Controls.Add(security, 3, row);
    }

    private void SetRowVisible(Control c, bool visible)
    {
        c.Visible = visible;
        if (_labels.TryGetValue(c, out var l)) l.Visible = visible;
    }

    private void SetNote(string text)
    {
        _note.Text = text;
        SetRowVisible(_note, !string.IsNullOrWhiteSpace(text));
    }

    private void UpdateAuthVisibility()
    {
        var auth = Selected<AuthMethod>(_auth);
        bool oauth = auth != AuthMethod.Password;
        SuspendLayout();
        SetRowVisible(_password, !oauth);
        _showPassword.Visible = !oauth;

        // 앱 기본 클라이언트가 있으면 직접 입력은 선택(고급), 없으면 필수
        bool hasDefault = oauth && OAuthClients.HasDefault(auth);
        if (oauth && !hasDefault) _customClient.Checked = true;
        _customClient.Enabled = hasDefault;
        _customClient.Text = hasDefault
            ? "직접 등록한 클라이언트 ID 사용 (고급: 회사 정책 등으로 자체 앱 등록이 필요한 경우)"
            : "직접 등록한 클라이언트 ID 사용 (이 앱에 기본 클라이언트 ID가 없어 필수)";
        bool custom = oauth && _customClient.Checked;
        SetRowVisible(_customClient, oauth);
        SetRowVisible(_clientId, custom);
        SetRowVisible(_clientSecret, custom && auth == AuthMethod.OAuthGoogle);
        SetRowVisible(_oauthRow, oauth);
        SetRowVisible(_leaveOnServer, Selected<IncomingProtocol>(_protocol) == IncomingProtocol.Pop3);
        SetRowVisible(_syncToServer, Selected<IncomingProtocol>(_protocol) == IncomingProtocol.Imap);
        ResumeLayout(true);
        UpdateOAuthStatus();
    }

    private static void Select<T>(ComboBox combo, T value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is Choice<T> c && EqualityComparer<T>.Default.Equals(c.Value, value))
            {
                combo.SelectedIndex = i;
                return;
            }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static T Selected<T>(ComboBox combo) => ((Choice<T>)combo.SelectedItem!).Value;

    // ---------- 데이터 ----------

    private void LoadFromAccount(bool isNew)
    {
        _loading = true;
        var a = _account;
        _displayName.Text = a.DisplayName;
        _email.Text = a.EmailAddress;
        _senderName.Text = a.SenderName;
        Select(_protocol, a.Protocol);
        _inHost.Text = a.IncomingHost;
        _inPort.Value = Math.Clamp(a.IncomingPort, 1, 65535);
        Select(_inSecurity, a.IncomingSecurity);
        _smtpHost.Text = a.SmtpHost;
        _smtpPort.Value = Math.Clamp(a.SmtpPort, 1, 65535);
        Select(_smtpSecurity, a.SmtpSecurity);
        _userName.Text = a.UserName;
        Select(_auth, a.AuthMethod);
        _password.Text = a.Password;
        _clientId.Text = a.OAuthClientId;
        _clientSecret.Text = a.OAuthClientSecret;
        _customClient.Checked = a.OAuthClientId.Length > 0;   // 비어 있으면 앱 기본값 사용
        _leaveOnServer.Checked = a.LeaveMessagesOnServer;
        _syncToServer.Checked = isNew || a.SyncChangesToServer;   // 새 계정은 기본으로 켬
        _initialLimit.Value = Math.Clamp(a.InitialSyncLimit, 1, 100000);
        _enabled.Checked = a.Enabled;

        var match = ProviderPresets.All.FirstOrDefault(p =>
            p.IncomingHost.Equals(a.IncomingHost, StringComparison.OrdinalIgnoreCase) && p.Protocol == a.Protocol && p.IncomingHost.Length > 0);
        _preset.SelectedItem = match ?? (isNew ? ProviderPresets.All[0] : ProviderPresets.All.First(p => p.Name.StartsWith("직접") && p.Protocol == a.Protocol));
        SetNote((_preset.SelectedItem as ProviderPreset)?.Note ?? "");
        _loading = false;

        if (isNew) ApplyPreset();
        UpdateAuthVisibility();
        UpdateOAuthStatus();
    }

    private void ApplyToAccount(MailAccount a)
    {
        a.DisplayName = _displayName.Text.Trim();
        a.EmailAddress = _email.Text.Trim();
        a.SenderName = _senderName.Text.Trim();
        a.Protocol = Selected<IncomingProtocol>(_protocol);
        a.IncomingHost = _inHost.Text.Trim();
        a.IncomingPort = (int)_inPort.Value;
        a.IncomingSecurity = Selected<SecureSocketOptions>(_inSecurity);
        a.SmtpHost = _smtpHost.Text.Trim();
        a.SmtpPort = (int)_smtpPort.Value;
        a.SmtpSecurity = Selected<SecureSocketOptions>(_smtpSecurity);
        a.UserName = _userName.Text.Trim();
        var newAuth = Selected<AuthMethod>(_auth);
        bool authChanged = newAuth != a.AuthMethod;
        a.AuthMethod = newAuth;
        a.Password = _password.Text;
        // 직접 입력한 클라이언트만 계정에 저장 (기본값과 같으면 비워서 기본값을 따름)
        bool custom = newAuth != AuthMethod.Password && _customClient.Checked;
        var newClientId = custom ? _clientId.Text.Trim() : "";
        if (newClientId == OAuthClients.Default(newAuth).ClientId) newClientId = "";
        a.OAuthClientId = newClientId;
        a.OAuthClientSecret = newClientId.Length > 0 ? _clientSecret.Text.Trim() : "";
        // 인증 방식이나 사용할 클라이언트가 바뀌면 토큰 무효화 (토큰은 발급받은 클라이언트에 묶임)
        if (authChanged || (a.OAuthTokenClientId.Length > 0 && a.OAuthTokenClientId != OAuthClients.EffectiveClientId(a)))
        {
            a.RefreshToken = "";
            a.OAuthTokenClientId = "";
            OAuthService.ClearCache(a.Id);
        }
        a.LeaveMessagesOnServer = _leaveOnServer.Checked;
        a.SyncChangesToServer = _syncToServer.Checked;
        a.InitialSyncLimit = (int)_initialLimit.Value;
        a.Enabled = _enabled.Checked;
    }

    private void ApplyPreset()
    {
        if (_loading || _preset.SelectedItem is not ProviderPreset p) return;
        SetNote(p.Note);
        _loading = true;
        Select(_protocol, p.Protocol);
        _loading = false;
        if (p.IncomingHost.Length > 0 || p.Name.StartsWith("직접"))
        {
            _inHost.Text = p.IncomingHost;
            _smtpHost.Text = p.SmtpHost;
        }
        _inPort.Value = p.IncomingPort;
        Select(_inSecurity, p.IncomingSecurity);
        _smtpPort.Value = p.SmtpPort;
        Select(_smtpSecurity, p.SmtpSecurity);
        Select(_auth, p.DefaultAuth);
        UpdateAuthVisibility();
    }

    /// <summary>
    /// 잘 알려진 포트를 입력하면 보안 방식을 맞춰 줍니다.
    /// 465(SMTPS)/993(IMAPS)/995(POP3S) 는 접속 즉시 TLS, 587/25/143/110 은 STARTTLS.
    /// (포트와 보안 방식이 어긋나면 연결이 멈추거나 실패함)
    /// </summary>
    private void MatchSecurityToPort(NumericUpDown port, ComboBox security)
    {
        if (_loading) return;   // 저장된 계정을 불러올 때는 사용자의 설정을 그대로 유지
        SecureSocketOptions? match = (int)port.Value switch
        {
            465 or 993 or 995 => SecureSocketOptions.SslOnConnect,
            587 or 25 or 143 or 110 => SecureSocketOptions.StartTls,
            _ => null
        };
        if (match is { } m) Select(security, m);
    }

    private void ProtocolChanged()
    {
        if (_loading) return;
        var proto = Selected<IncomingProtocol>(_protocol);
        if (proto == IncomingProtocol.Pop3 && _inPort.Value == 993) _inPort.Value = 995;
        if (proto == IncomingProtocol.Imap && _inPort.Value == 995) _inPort.Value = 993;
        if (proto == IncomingProtocol.Pop3 && _inPort.Value == 143) _inPort.Value = 110;
        if (proto == IncomingProtocol.Imap && _inPort.Value == 110) _inPort.Value = 143;

        // 같은 서비스의 다른 프로토콜 프리셋으로 전환
        if (_preset.SelectedItem is ProviderPreset cur && cur.Protocol != proto)
        {
            var baseName = cur.Name[..cur.Name.LastIndexOf('(')];
            var other = ProviderPresets.All.FirstOrDefault(p => p.Protocol == proto && p.Name.StartsWith(baseName));
            if (other != null) _preset.SelectedItem = other;
        }
        UpdateAuthVisibility();
    }

    private void GuessFromEmail()
    {
        if (string.IsNullOrWhiteSpace(_displayName.Text)) _displayName.Text = _email.Text.Trim();
        if (_preset.SelectedItem is ProviderPreset cur && !cur.Name.StartsWith("직접") && _inHost.Text.Length > 0) return;
        var guess = ProviderPresets.Guess(_email.Text, Selected<IncomingProtocol>(_protocol));
        if (guess != null) _preset.SelectedItem = guess;
    }

    private void UpdateOAuthStatus()
    {
        // 화면에서 고른 클라이언트가 토큰을 받은 클라이언트와 같아야 로그인 상태
        var auth = Selected<AuthMethod>(_auth);
        var chosen = auth != AuthMethod.Password && _customClient.Checked && _clientId.Text.Trim().Length > 0
            ? _clientId.Text.Trim()
            : OAuthClients.Default(auth).ClientId;
        bool hasToken = !string.IsNullOrEmpty(_account.RefreshToken)
                        && _account.AuthMethod == auth
                        && OAuthClients.TokenClientId(_account) == chosen;
        _oauthStatus.Text = hasToken ? "✔ 로그인됨" : "로그인 필요";
        _oauthStatus.ForeColor = hasToken ? Color.Green : Color.Firebrick;
    }

    /// <summary>
    /// 브라우저 로그인 대기. 대기 중에는 버튼이 [로그인 취소]로 바뀌고,
    /// 대화창의 취소/닫기로도 언제든 중단할 수 있습니다. (창 전체를 비활성화하지 않음)
    /// </summary>
    private async Task OAuthLoginAsync()
    {
        if (_oauthCts != null)
        {
            _oauthCts.Cancel();   // 대기 중에 다시 누르면 취소
            return;
        }

        ApplyToAccount(_account);
        if (string.IsNullOrEmpty(_account.EmailAddress))
        {
            MessageBox.Show(this, "이메일 주소를 입력하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(OAuthClients.EffectiveClientId(_account)))
        {
            MessageBox.Show(this, "직접 등록한 OAuth 클라이언트 ID를 입력하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _oauthCts = new CancellationTokenSource();
        SetOAuthBusy(true);
        string? failure = null;
        bool cancelled = false;
        try
        {
            await OAuthService.AuthorizeInteractiveAsync(_account, _oauthCts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            Log.Error("oauth login", ex);
            failure = ex.Message;
        }
        finally
        {
            _oauthCts.Dispose();
            _oauthCts = null;
        }

        if (IsDisposed) return;   // 대기 중에 대화창이 닫힌 경우

        SetOAuthBusy(false);
        UpdateOAuthStatus();
        if (cancelled)
        {
            _oauthStatus.Text = "로그인 취소됨";
            _oauthStatus.ForeColor = Color.Firebrick;
            return;
        }
        Activate();
        if (failure != null)
            MessageBox.Show(this, failure, "OAuth 로그인 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void SetOAuthBusy(bool busy)
    {
        _oauthLogin.Text = busy ? "로그인 취소" : "OAuth 로그인...";
        // 로그인 결과에 영향을 주는 입력과 확인/테스트만 잠금. [로그인 취소], [취소], 닫기(X)는 항상 사용 가능
        foreach (var c in new Control[] { _ok, _test, _preset, _email, _userName, _auth, _clientId, _clientSecret })
            c.Enabled = !busy;
        _customClient.Enabled = !busy && OAuthClients.HasDefault(Selected<AuthMethod>(_auth));
        if (busy)
        {
            _oauthStatus.Text = "브라우저에서 로그인을 완료하세요... (최대 5분)";
            _oauthStatus.ForeColor = SystemColors.ControlText;
        }
    }

    private string? Validate(MailAccount a)
    {
        if (string.IsNullOrWhiteSpace(a.EmailAddress) || !a.EmailAddress.Contains('@')) return "올바른 이메일 주소를 입력하세요.";
        if (string.IsNullOrWhiteSpace(a.IncomingHost)) return "받는 메일 서버를 입력하세요.";
        if (string.IsNullOrWhiteSpace(a.SmtpHost)) return "보내는 메일(SMTP) 서버를 입력하세요.";
        if (a.AuthMethod == AuthMethod.Password && string.IsNullOrEmpty(a.Password)) return "비밀번호를 입력하세요.";
        if (a.AuthMethod != AuthMethod.Password && string.IsNullOrWhiteSpace(OAuthClients.EffectiveClientId(a)))
            return "직접 등록한 OAuth 클라이언트 ID를 입력하세요.";
        return null;
    }

    private async Task TestAsync()
    {
        ApplyToAccount(_account);
        var error = Validate(_account);
        if (error != null)
        {
            MessageBox.Show(this, error, "연결 테스트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Enabled = false;
        UseWaitCursor = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await MailService.TestConnectionAsync(_account, cts.Token);
            MessageBox.Show(this, result, "연결 테스트 성공", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "연결 테스트 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            Enabled = true;
        }
    }

    private void OkClicked()
    {
        ApplyToAccount(_account);
        var error = Validate(_account);
        if (error != null)
        {
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_account.AuthMethod != AuthMethod.Password && string.IsNullOrEmpty(_account.RefreshToken) &&
            MessageBox.Show(this, "아직 OAuth 로그인을 하지 않았습니다. 로그인하지 않으면 메일을 받을 수 없습니다.\n그래도 저장하시겠습니까?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (string.IsNullOrWhiteSpace(_account.DisplayName)) _account.DisplayName = _account.EmailAddress;
        DialogResult = DialogResult.OK;
        Close();
    }
}

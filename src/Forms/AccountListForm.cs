using MailClient.Models;
using MailClient.Services;

namespace MailClient.Forms;

/// <summary>등록된 메일 계정 목록. 계정 추가·편집·삭제, 계정/설정 내보내기·가져오기(암호화된 JSON).</summary>
public class AccountListForm : DpiForm
{
    private readonly List<MailAccount> _accounts;
    private readonly Action<MailAccount> _onEdited;
    private readonly Action<List<MailAccount>> _onAdded;
    private readonly Func<AccountExport.ExportedSettings> _getSettings;
    private readonly Action<AccountExport.ExportedSettings> _onSettingsImported;
    private readonly Action<MailAccount> _onCreated;
    private readonly Action<MailAccount> _onDeleted;

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
        MultiSelect = false, GridLines = false
    };
    private readonly Button _add = new() { Text = "추가(&A)...", AutoSize = true };
    private readonly Button _edit = new() { Text = "편집(&E)...", AutoSize = true };
    private readonly Button _delete = new() { Text = "삭제(&D)", AutoSize = true };
    private readonly Button _export = new() { Text = "내보내기(&X)...", AutoSize = true };
    private readonly Button _import = new() { Text = "가져오기(&I)...", AutoSize = true };
    private readonly Button _close = new() { Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label _summary = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding S(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    /// <param name="accounts">설정의 계정 목록 (편집 반영 후 다시 읽음)</param>
    /// <param name="selectAccountId">처음 선택할 계정</param>
    /// <param name="onEdited">편집(또는 가져오기로 덮어쓰기) 완료 시 호출 (저장 및 트리 갱신은 호출자 담당)</param>
    /// <param name="onAdded">가져오기로 새 계정이 추가될 때 호출</param>
    /// <param name="getSettings">내보낼 "도구 > 설정" 내용</param>
    /// <param name="onSettingsImported">가져온 설정 반영 (저장/적용은 호출자 담당)</param>
    /// <param name="onCreated">[추가]로 새 계정을 만들었을 때 호출 (등록·저장·트리 갱신은 호출자 담당)</param>
    /// <param name="onDeleted">[삭제] 확인 후 호출 (계정과 로컬 메일 삭제는 호출자 담당)</param>
    public AccountListForm(List<MailAccount> accounts, string? selectAccountId, Action<MailAccount> onEdited, Action<List<MailAccount>> onAdded,
        Func<AccountExport.ExportedSettings> getSettings, Action<AccountExport.ExportedSettings> onSettingsImported,
        Action<MailAccount> onCreated, Action<MailAccount> onDeleted)
    {
        _accounts = accounts;
        _onEdited = onEdited;
        _onAdded = onAdded;
        _getSettings = getSettings;
        _onSettingsImported = onSettingsImported;
        _onCreated = onCreated;
        _onDeleted = onDeleted;

        SuspendLayout();
        Text = "메일 계정 목록";
        Font = new Font("Malgun Gothic", 9f);
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(S(930), S(380));
        MinimumSize = new Size(S(560), S(280));

        _list.Columns.Add("계정 이름", S(130));
        _list.Columns.Add("이메일 주소", S(185));
        _list.Columns.Add("방식", S(55));
        _list.Columns.Add("받는 메일 서버", S(165));
        _list.Columns.Add("인증", S(120));
        _list.Columns.Add("상태", S(75));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = S(12, 0, 0, 0)
        };
        foreach (var b in AllButtons)
        {
            b.MinimumSize = new Size(S(120), S(30));
            b.Margin = S(0, 0, 0, 8);
        }
        _export.Margin = S(0, 16, 0, 8);   // 추가/편집/삭제와 내보내기/가져오기 사이 간격
        _close.Margin = S(0, 16, 0, 8);
        buttons.Controls.AddRange(AllButtons);

        var hint = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = S(0, 0, 0, 8),
            Text = "[추가]로 새 계정을 등록하고, 계정을 선택한 뒤 [편집](또는 두 번 클릭) / [삭제](Delete 키)를 누르세요.\n" +
                   "[내보내기]/[가져오기]에서는 옮길 계정과 \"도구 > 설정\" 포함 여부를 고를 수 있습니다."
        };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(26) };
        _summary.Location = new Point(0, S(8));
        bottom.Controls.Add(_summary);

        var body = new Panel { Dock = DockStyle.Fill, Padding = S(14, 12, 14, 10) };
        body.Controls.Add(_list);
        body.Controls.Add(buttons);
        body.Controls.Add(bottom);
        body.Controls.Add(hint);
        Controls.Add(body);
        CancelButton = _close;

        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => EditSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { EditSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
        };
        _add.Click += (_, _) => AddNew();
        _edit.Click += (_, _) => EditSelected();
        _delete.Click += (_, _) => DeleteSelected();
        _export.Click += async (_, _) => await ExportAsync();
        _import.Click += async (_, _) => await ImportAsync();

        Reload(selectAccountId ?? _accounts.FirstOrDefault()?.Id);
        ResumeLayout(true);
    }

    private static string AuthText(AuthMethod m) => m switch
    {
        AuthMethod.OAuthGoogle => "OAuth (Google)",
        AuthMethod.OAuthMicrosoft => "OAuth (Microsoft)",
        _ => "비밀번호"
    };

    private static string ProtocolText(IncomingProtocol p) => p == IncomingProtocol.Imap ? "IMAP" : "POP3";

    private void Reload(string? selectId)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var a in _accounts)
        {
            var item = new ListViewItem(
            [
                a.ToString(), a.EmailAddress, ProtocolText(a.Protocol),
                $"{a.IncomingHost}:{a.IncomingPort}", AuthText(a.AuthMethod), a.Enabled ? "사용" : "사용 안 함"
            ]) { Tag = a };
            if (!a.Enabled) item.ForeColor = SystemColors.GrayText;
            _list.Items.Add(item);
            if (a.Id == selectId) item.Selected = item.Focused = true;
        }
        _list.EndUpdate();
        if (_list.SelectedItems.Count > 0) _list.SelectedItems[0].EnsureVisible();
        _summary.Text = $"등록된 계정 {_accounts.Count}개";
        UpdateButtons();
    }

    private Button[] AllButtons => [_add, _edit, _delete, _export, _import, _close];

    private void UpdateButtons()
    {
        _edit.Enabled = _delete.Enabled = _list.SelectedItems.Count == 1;
        _export.Enabled = true;   // 계정이 없어도 설정만 내보낼 수 있음
    }

    private void AddNew()
    {
        using var form = new AccountForm(null);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        _onCreated(form.Account);
        Reload(form.Account.Id);
    }

    private void EditSelected()
    {
        if (_list.SelectedItems.Count != 1 || _list.SelectedItems[0].Tag is not MailAccount account) return;
        using var form = new AccountForm(account);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        _onEdited(form.Account);
        Reload(form.Account.Id);
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItems.Count != 1 || _list.SelectedItems[0].Tag is not MailAccount account) return;
        if (!MainForm.ConfirmDeleteAccount(this, account)) return;
        // 삭제 후 다음 계정(없으면 이전 계정)을 선택
        int index = _list.SelectedIndices[0];
        _onDeleted(account);
        var next = _accounts.Count == 0 ? null : _accounts[Math.Min(index, _accounts.Count - 1)];
        Reload(next?.Id);
        _list.Focus();
    }

    private void SetBusy(bool busy, string? text = null)
    {
        UseWaitCursor = busy;
        foreach (var b in AllButtons) b.Enabled = !busy;
        if (busy) _summary.Text = text ?? "";
        else
        {
            _summary.Text = $"등록된 계정 {_accounts.Count}개";
            UpdateButtons();
        }
    }

    // =====================================================================
    // 내보내기: 선택 → (계정 포함 시) Windows 본인 확인 → 암호 → 저장
    // =====================================================================

    private async Task ExportAsync()
    {
        // 1) 내보낼 계정/설정 선택 (목록에서 선택된 계정이 있으면 그 계정만, 아니면 전체를 기본 선택)
        var rows = _accounts.Select(a => new TransferRow(a.ToString(), a.EmailAddress, ProtocolText(a.Protocol))).ToList();
        List<int> picked;
        bool includeSettings;
        using (var select = new TransferSelectionForm("내보내기 - 항목 선택",
                   "파일로 내보낼 계정을 선택하세요. 계정의 비밀번호와 로그인 토큰은 다음 단계에서 정하는 암호로 암호화됩니다.",
                   rows, checkAll: true, settingsAvailable: true, settingsChecked: true,
                   settingsNote: "설정에는 비밀 값이 없어 암호화하지 않고 저장합니다.", okText: "다음"))
        {
            if (select.ShowDialog(this) != DialogResult.OK) return;
            picked = select.CheckedIndices;
            includeSettings = select.IncludeSettings;
        }
        var selected = picked.Select(i => _accounts[i]).ToList();

        // 2) 계정(비밀 값)이 포함되면 현재 로그인한 Windows 사용자 본인 확인
        string? password = null;
        if (selected.Count > 0)
        {
            Log.Write($"Account export requested ({selected.Count} accounts, settings={includeSettings}) - starting Windows user verification");
            SetBusy(true, "Windows 사용자 확인 중...");
            UserVerificationResult verification;
            try
            {
                verification = await WindowsUserVerifier.VerifyAsync(this, "MailClient - 계정 목록 내보내기",
                    $"메일 계정 {selected.Count}개의 비밀번호가 포함된 목록을 내보내려고 합니다. 본인 확인을 위해 Windows 로그인 정보를 입력하세요.");
            }
            finally
            {
                if (!IsDisposed) SetBusy(false);
            }
            if (IsDisposed) return;
            Activate();

            Log.Write($"Account export verification: {verification.Outcome} via {verification.Method}");
            if (!verification.IsVerified)
            {
                if (verification.Outcome == UserVerificationOutcome.Failed)
                    MessageBox.Show(this, "본인 확인에 실패해 내보내기를 취소했습니다.\n\n" + verification.Error,
                        "계정 목록 내보내기", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 3) 비밀 값 암호화용 암호
            using var pw = new ExportPasswordForm(selected.Count);
            if (pw.ShowDialog(this) != DialogResult.OK) return;
            password = pw.Password;
        }
        else
        {
            Log.Write("Settings-only export requested (no accounts, no secrets)");
        }

        // 4) 저장
        using var dlg = new SaveFileDialog
        {
            Title = "계정 목록 내보내기",
            Filter = "JSON 파일 (*.json)|*.json",
            FileName = selected.Count > 0
                ? $"MailClient-{(includeSettings ? "backup" : "accounts")}-{DateTime.Now:yyyyMMdd}.json"
                : $"MailClient-settings-{DateTime.Now:yyyyMMdd}.json",
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var settings = includeSettings ? _getSettings() : null;
        var path = dlg.FileName;

        SetBusy(true, selected.Count > 0 ? "내보내는 중... (암호화 키 생성)" : "내보내는 중...");
        try
        {
            // 키 유도(PBKDF2 60만 회)에 시간이 걸리므로 UI 스레드 밖에서 실행
            await Task.Run(() => AccountExport.Export(selected, settings, password, path));
            var what = new List<string>();
            if (selected.Count > 0) what.Add($"계정 {selected.Count}개");
            if (settings != null) what.Add("설정");
            MessageBox.Show(this,
                $"{string.Join(", ", what)}을(를) 내보냈습니다.\n{path}" +
                (selected.Count > 0 ? "\n\n비밀번호 등 비밀 값은 입력한 암호로 암호화되었습니다.\n이 암호를 잃어버리면 비밀 값을 복구할 수 없습니다." : ""),
                "내보내기 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error("export accounts", ex);
            MessageBox.Show(this, "내보내기에 실패했습니다.\n\n" + ex.Message, "내보내기", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    // =====================================================================
    // 가져오기: 파일 → 선택 → (비밀 값 포함 시) 암호 → 중복 처리 → 반영
    // =====================================================================

    /// <summary>
    /// 내보낸 JSON 에서 선택한 계정과 설정을 가져옵니다.
    /// 같은 계정(ID 또는 이메일 주소가 같음)이 이미 있으면 덮어쓸지/건너뛸지 묻습니다.
    /// 덮어쓸 때는 기존 계정 ID 를 유지해 로컬에 저장된 메일이 그대로 연결되게 합니다.
    /// </summary>
    private async Task ImportAsync()
    {
        using var open = new OpenFileDialog
        {
            Title = "계정 목록 가져오기",
            Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*"
        };
        if (open.ShowDialog(this) != DialogResult.OK) return;
        var path = open.FileName;

        AccountExport.ExportFile header;
        try
        {
            header = AccountExport.Peek(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "계정 목록 파일을 읽을 수 없습니다.\n\n" + ex.Message, "가져오기", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (header.Accounts.Count == 0 && header.Settings == null)
        {
            MessageBox.Show(this, "파일에 가져올 계정이나 설정이 없습니다.", "가져오기", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 1) 가져올 계정/설정 선택 (각 계정이 새 계정인지, 이미 있는지 표시)
        var rows = header.Accounts.Select(e =>
        {
            var existing = _accounts.FirstOrDefault(x => x.Id == e.Id)
                           ?? _accounts.FirstOrDefault(x => x.EmailAddress.Equals(e.EmailAddress, StringComparison.OrdinalIgnoreCase));
            var name = string.IsNullOrWhiteSpace(e.DisplayName) ? e.EmailAddress : e.DisplayName;
            return existing == null
                ? new TransferRow(name, e.EmailAddress, ProtocolText(e.Protocol), "새 계정")
                : new TransferRow(name, e.EmailAddress, ProtocolText(e.Protocol), $"이미 있음 ({existing})", StatusIsWarning: true);
        }).ToList();

        List<int> picked;
        bool includeSettings;
        using (var select = new TransferSelectionForm("가져오기 - 항목 선택",
                   $"'{Path.GetFileName(path)}' (내보낸 날짜: {header.ExportedAt.LocalDateTime:yyyy-MM-dd HH:mm})\n가져올 계정을 선택하세요.",
                   rows, checkAll: true,
                   settingsAvailable: header.Settings != null, settingsChecked: false,
                   settingsNote: header.Settings != null
                       ? "선택하면 현재 \"도구 > 설정\" 내용이 파일의 설정으로 바뀝니다."
                       : "이 파일에는 설정이 포함되어 있지 않습니다.",
                   okText: "가져오기"))
        {
            if (select.ShowDialog(this) != DialogResult.OK) return;
            picked = select.CheckedIndices;
            includeSettings = select.IncludeSettings;
        }

        // 2) 선택한 계정에 비밀 값이 있으면 암호 입력 → 복호화 (틀리면 다시 입력)
        AccountExport.ImportResult? result = null;
        bool needPassword = AccountExport.NeedsPassword(header, picked);
        string? error = null;
        while (result == null)
        {
            string? password = null;
            if (needPassword)
            {
                using var prompt = new PasswordPromptForm("가져오기 암호",
                    $"선택한 계정 {picked.Count}개의 비밀번호와 로그인 토큰을 복원합니다.\n\n내보낼 때 입력한 암호를 입력하세요.", error);
                if (prompt.ShowDialog(this) != DialogResult.OK) return;
                password = prompt.Password;
            }

            SetBusy(true, needPassword ? "암호 확인 중..." : "가져오는 중...");
            try
            {
                result = await Task.Run(() => AccountExport.Import(path, password, picked, includeSettings));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                error = "암호가 올바르지 않습니다. 다시 입력하세요.";
            }
            catch (Exception ex)
            {
                Log.Error("import accounts", ex);
                MessageBox.Show(this, "가져오기에 실패했습니다.\n\n" + ex.Message, "가져오기", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally
            {
                if (!IsDisposed) SetBusy(false);
            }
        }

        // 3) 기존 계정과 비교
        var plan = AccountExport.PlanImport(_accounts, result.Accounts);
        var conflicts = plan.Conflicts;
        var newOnes = plan.NewAccounts;

        bool overwrite = false;
        if (conflicts.Count > 0)
        {
            var names = string.Join("\n", conflicts.Take(10).Select(c => $"  • {c.Existing} <{c.Existing.EmailAddress}>"))
                        + (conflicts.Count > 10 ? $"\n  … 외 {conflicts.Count - 10}개" : "");
            var answer = MessageBox.Show(this,
                $"다음 {conflicts.Count}개 계정은 이미 등록되어 있습니다.\n{names}\n\n" +
                "[예]  가져온 설정으로 덮어쓰기 (받은 메일은 유지)\n[아니요]  기존 계정 유지 (건너뛰기)\n[취소]  가져오기 취소",
                "가져오기 - 중복 계정", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            overwrite = answer == DialogResult.Yes;
        }

        // 4) 반영
        if (overwrite)
        {
            foreach (var (imp, existing) in conflicts)
            {
                imp.Id = existing.Id;   // 로컬 메일 데이터와의 연결 유지
                _onEdited(imp);
            }
        }
        if (newOnes.Count > 0) _onAdded(newOnes);
        if (result.Settings != null) _onSettingsImported(result.Settings);

        Reload(newOnes.FirstOrDefault()?.Id ?? conflicts.FirstOrDefault().Existing?.Id);

        var skipped = overwrite ? 0 : conflicts.Count;
        MessageBox.Show(this,
            "가져오기를 마쳤습니다.\n\n" +
            $"새로 추가: {newOnes.Count}개\n덮어쓰기: {(overwrite ? conflicts.Count : 0)}개\n건너뜀: {skipped}개\n" +
            $"설정: {(result.Settings != null ? "가져옴" : "가져오지 않음")}",
            "가져오기 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

/// <summary>내보내기 파일의 비밀 값을 암호화할 암호 입력 (확인 입력 포함)</summary>
public class ExportPasswordForm : DpiForm
{
    private readonly TextBox _pw1 = new() { UseSystemPasswordChar = true };
    private readonly TextBox _pw2 = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _show = new() { Text = "암호 표시", AutoSize = true };
    private readonly Label _hint = new() { AutoSize = true };
    private readonly Button _ok = new() { Text = "확인", AutoSize = true };
    private readonly Button _cancel = new() { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };

    public string Password => _pw1.Text;

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding S(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    public ExportPasswordForm(int accountCount)
    {
        SuspendLayout();
        Text = "내보내기 암호";
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
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2,
            Location = Point.Empty, Padding = S(18, 16, 18, 14)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(280)));

        var intro = new Label
        {
            AutoSize = true, MaximumSize = new Size(S(400), 0), Margin = S(0, 0, 0, 12),
            Text = $"계정 {accountCount}개의 비밀번호, OAuth 클라이언트 비밀, 로그인 토큰을 이 암호로 암호화해 저장합니다.\n" +
                   $"파일에 담긴 비밀 값을 복원하려면 이 암호가 필요하며, 잃어버리면 복구할 수 없습니다."
        };
        root.Controls.Add(intro, 0, 0);
        root.SetColumnSpan(intro, 2);

        void Row(int r, string label, Control c)
        {
            root.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = S(0, 4, 14, 4) }, 0, r);
            c.Dock = DockStyle.Fill;
            c.Margin = S(0, 3, 0, 3);
            root.Controls.Add(c, 1, r);
        }
        Row(1, "암호", _pw1);
        Row(2, "암호 확인", _pw2);
        _show.Margin = S(0, 4, 0, 0);
        root.Controls.Add(_show, 1, 3);
        _hint.Margin = S(0, 6, 0, 0);
        root.Controls.Add(_hint, 1, 4);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = S(0, 16, 0, 0)
        };
        foreach (var b in new[] { _ok, _cancel }) b.MinimumSize = new Size(S(88), S(30));
        _cancel.Margin = Padding.Empty;
        _ok.Margin = S(0, 0, 8, 0);
        buttons.Controls.AddRange([_cancel, _ok]);
        root.Controls.Add(buttons, 0, 5);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
        AcceptButton = _ok;
        CancelButton = _cancel;

        _pw1.TextChanged += (_, _) => UpdateHint();
        _pw2.TextChanged += (_, _) => UpdateHint();
        _show.CheckedChanged += (_, _) => _pw1.UseSystemPasswordChar = _pw2.UseSystemPasswordChar = !_show.Checked;
        _ok.Click += (_, _) =>
        {
            if (!IsValid()) return;
            DialogResult = DialogResult.OK;
            Close();
        };

        UpdateHint();
        ResumeLayout(true);
    }

    private bool IsValid() => _pw1.Text.Length >= AccountExport.MinPasswordLength && _pw1.Text == _pw2.Text;

    private void UpdateHint()
    {
        if (_pw1.Text.Length < AccountExport.MinPasswordLength)
        {
            _hint.Text = $"{AccountExport.MinPasswordLength}자 이상 입력하세요. (현재 {_pw1.Text.Length}자)";
            _hint.ForeColor = SystemColors.GrayText;
        }
        else if (_pw1.Text != _pw2.Text)
        {
            _hint.Text = _pw2.Text.Length == 0 ? "확인을 위해 한 번 더 입력하세요." : "두 암호가 일치하지 않습니다.";
            _hint.ForeColor = _pw2.Text.Length == 0 ? SystemColors.GrayText : Color.Firebrick;
        }
        else
        {
            _hint.Text = "✔ 사용할 수 있는 암호입니다.";
            _hint.ForeColor = Color.Green;
        }
        _ok.Enabled = IsValid();
    }
}

/// <summary>암호 한 번 입력 (가져오기용). 이전 시도의 오류 메시지를 함께 표시할 수 있습니다.</summary>
public class PasswordPromptForm : DpiForm
{
    private readonly TextBox _pw = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _show = new() { Text = "암호 표시", AutoSize = true };
    private readonly Button _ok = new() { Text = "확인", AutoSize = true };
    private readonly Button _cancel = new() { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };

    public string Password => _pw.Text;

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding S(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    public PasswordPromptForm(string title, string message, string? error = null)
    {
        SuspendLayout();
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
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2,
            Location = Point.Empty, Padding = S(18, 16, 18, 14)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(280)));

        var intro = new Label { Text = message, AutoSize = true, MaximumSize = new Size(S(400), 0), Margin = S(0, 0, 0, 12) };
        root.Controls.Add(intro, 0, 0);
        root.SetColumnSpan(intro, 2);

        root.Controls.Add(new Label { Text = "암호", AutoSize = true, Anchor = AnchorStyles.Left, Margin = S(0, 4, 14, 4) }, 0, 1);
        _pw.Dock = DockStyle.Fill;
        _pw.Margin = S(0, 3, 0, 3);
        root.Controls.Add(_pw, 1, 1);
        _show.Margin = S(0, 4, 0, 0);
        root.Controls.Add(_show, 1, 2);

        if (error != null)
        {
            var err = new Label { Text = error, AutoSize = true, ForeColor = Color.Firebrick, Margin = S(0, 8, 0, 0) };
            root.Controls.Add(err, 1, 3);
        }

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = S(0, 16, 0, 0)
        };
        foreach (var b in new[] { _ok, _cancel }) b.MinimumSize = new Size(S(88), S(30));
        _cancel.Margin = Padding.Empty;
        _ok.Margin = S(0, 0, 8, 0);
        buttons.Controls.AddRange([_cancel, _ok]);
        root.Controls.Add(buttons, 0, 4);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
        AcceptButton = _ok;
        CancelButton = _cancel;

        _pw.TextChanged += (_, _) => _ok.Enabled = _pw.Text.Length > 0;
        _show.CheckedChanged += (_, _) => _pw.UseSystemPasswordChar = !_show.Checked;
        _ok.Click += (_, _) =>
        {
            if (_pw.Text.Length == 0) return;
            DialogResult = DialogResult.OK;
            Close();
        };
        _ok.Enabled = false;
        ResumeLayout(true);
    }
}

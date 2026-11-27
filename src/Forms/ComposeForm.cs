using MailClient.Models;
using MailClient.Services;
using MimeKit;

namespace MailClient.Forms;

/// <summary>
/// 메일 쓰기 / 회신 / 전달 창.
/// <para>
/// 제목이나 본문에 한 글자라도 입력하면 그때부터 작성 중인 메일을 Draft 에 보관하고, 이후 바뀔 때마다 잠시 뒤 갱신합니다.
/// 보내면 Draft 에서 지우고, 보내지 않고 닫으면 Draft 에서도 지울지 묻습니다.
/// </para>
/// </summary>
public class ComposeForm : DpiForm
{
    private readonly MailService _mail;
    private readonly MailStore _store;
    private readonly Action _saveSettings;
    private readonly ComposeDraft _draft;

    private readonly ComboBox _from = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _to = new() { Dock = DockStyle.Fill };
    private readonly TextBox _cc = new() { Dock = DockStyle.Fill };
    private readonly TextBox _bcc = new() { Dock = DockStyle.Fill };
    private readonly TextBox _subject = new() { Dock = DockStyle.Fill };
    private readonly ListBox _attachments = new() { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
    private readonly TextBox _body = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true, AcceptsTab = true,
        WordWrap = true, Font = new Font("Malgun Gothic", 10f), BorderStyle = BorderStyle.None
    };
    private readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly TableLayoutPanel _headerTable;
    private readonly Label _attachLabel;
    private readonly System.Windows.Forms.Timer _draftTimer = new() { Interval = 1500 };

    private bool _sending;
    private bool _dirty;

    /// <summary>Draft 에 보관한 메일의 ID (아직 보관하지 않았으면 null)</summary>
    public string? DraftId { get; private set; }

    private bool _draftPending;   // 마지막 보관 이후 바뀐 내용이 있음

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding S(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    private sealed class AttachmentEntry
    {
        public string? FilePath { get; init; }
        public MimeEntity? Entity { get; init; }
        public override string ToString()
        {
            if (FilePath != null) return $"{Path.GetFileName(FilePath)}  ({new FileInfo(FilePath).Length / 1024.0:0.#} KB)";
            return Entity switch
            {
                MimePart p => p.FileName ?? p.ContentType.Name ?? "attachment",
                MessagePart m => (m.Message?.Subject ?? "message") + ".eml",
                _ => "attachment"
            };
        }
    }

    /// <param name="existingDraft">Draft 에 보관된 메일을 이어서 쓸 때 그 메일</param>
    public ComposeForm(List<MailAccount> accounts, MailAccount account, ComposeDraft draft, MailService mail, MailStore store, Action saveSettings,
        MailItem? existingDraft = null)
    {
        _mail = mail;
        _store = store;
        _saveSettings = saveSettings;
        _draft = draft;
        DraftId = existingDraft?.Id;

        Text = string.IsNullOrEmpty(draft.Subject) ? "새 메일" : draft.Subject;
        Font = new Font("Malgun Gothic", 9f);
        // 화면 배율에 맞춘 크기 (작업 영역보다 크지 않게)
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(S(820), work.Width * 9 / 10), Math.Min(S(640), work.Height * 9 / 10));
        MinimumSize = new Size(S(500), S(400));
        StartPosition = FormStartPosition.CenterParent;
        Icon = AppIcon.Window;
        KeyPreview = true;

        // 툴바 (ToolStrip 은 배율을 스스로 처리)
        var send = new ToolStripButton("➤ 보내기") { DisplayStyle = ToolStripItemDisplayStyle.Text, Font = new Font(Font, FontStyle.Bold), ToolTipText = "보내기 (Ctrl+Enter)" };
        send.Click += async (_, _) => await SendAsync();
        var attach = new ToolStripButton("첨부 파일 추가") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        attach.Click += (_, _) => AddAttachments();
        var removeAttach = new ToolStripButton("첨부 제거") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        removeAttach.Click += (_, _) => RemoveAttachments();
        _toolbar.Items.AddRange([send, new ToolStripSeparator(), attach, removeAttach]);
        _toolbar.Padding = S(4, 2, 4, 2);

        // 머리글: [이름 열(글자 폭에 맞춤) | 입력 칸]. 행 높이는 입력 칸 높이(글꼴 기준)에 맞춰 자동
        _headerTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = S(10, 8, 10, 6)
        };
        _headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow("보낸 사람:", _from);
        AddRow("받는 사람:", _to);
        AddRow("참조:", _cc);
        AddRow("숨은 참조:", _bcc);
        AddRow("제목:", _subject);
        _attachLabel = AddRow("첨부:", _attachments);
        _attachLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        _attachLabel.Margin = S(0, 6, 10, 3);
        _attachments.Height = S(56);

        var bodyPanel = new Panel { Dock = DockStyle.Fill, Padding = S(8, 6, 8, 6), BackColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle };
        bodyPanel.Controls.Add(_body);
        // 머리글과 본문 사이 여백
        var bodyHost = new Panel { Dock = DockStyle.Fill, Padding = S(10, 0, 10, 10) };
        bodyHost.Controls.Add(bodyPanel);

        _statusStrip.Items.Add(_status);

        Controls.Add(bodyHost);
        Controls.Add(_headerTable);
        Controls.Add(_statusStrip);
        Controls.Add(_toolbar);

        // 값 채우기
        foreach (var a in accounts)
            _from.Items.Add(new AccountChoice(a));
        _from.SelectedIndex = Math.Max(0, accounts.FindIndex(a => a.Id == account.Id));

        _to.Text = draft.To;
        _cc.Text = draft.Cc;
        _bcc.Text = draft.Bcc;
        _subject.Text = draft.Subject;
        _body.Text = draft.Body;
        foreach (var e in draft.ForwardedAttachments)
            _attachments.Items.Add(new AttachmentEntry { Entity = e });
        UpdateAttachmentVisibility();

        // 값을 채운 뒤에 연결하므로 이후의 변경은 모두 사용자가 입력한 것
        _subject.TextChanged += (_, _) => { Text = string.IsNullOrEmpty(_subject.Text) ? "새 메일" : _subject.Text; ContentChanged(startsDraft: true); };
        _body.TextChanged += (_, _) => ContentChanged(startsDraft: true);
        foreach (var tb in new[] { _to, _cc, _bcc })
        {
            tb.TextChanged += (_, _) => ContentChanged(startsDraft: false);
            // 받은 편지함에서 읽은 메일의 보낸 사람 중 입력한 글자가 들어 있는 주소를 추천
            var suggest = new AddressSuggest(tb, (text, limit) => _store.SearchKnownSenders(text, limit));
            FormClosed += (_, _) => suggest.Dispose();
        }
        _from.SelectedIndexChanged += (_, _) => ContentChanged(startsDraft: false);
        _draftTimer.Tick += (_, _) => SaveDraftNow();

        // 파일 드래그로 첨부
        AllowDrop = true;
        _body.AllowDrop = true;
        _attachments.AllowDrop = true;
        foreach (Control c in new Control[] { this, _body, _attachments })
        {
            c.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
            };
        }

        KeyDown += async (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SendAsync();
            }
        };

        Shown += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_to.Text)) _to.Focus();
            else
            {
                _body.Focus();
                _body.SelectionStart = 0;
                _body.SelectionLength = 0;
                _body.ScrollToCaret();
            }
            _dirty = false;
        };
        FormClosing += ComposeForm_FormClosing;
        FormClosed += (_, _) => _draftTimer.Dispose();
    }

    private sealed class AccountChoice(MailAccount account)
    {
        public MailAccount Account { get; } = account;
        public override string ToString() =>
            string.IsNullOrWhiteSpace(Account.SenderName) ? Account.EmailAddress : $"{Account.SenderName} <{Account.EmailAddress}>";
    }

    private Label AddRow(string label, Control control)
    {
        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = S(0, 3, 10, 3) };
        int row = _headerTable.RowCount++;
        _headerTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = S(0, 3, 0, 3);
        _headerTable.Controls.Add(lbl, 0, row);
        _headerTable.Controls.Add(control, 1, row);
        return lbl;
    }

    private void UpdateAttachmentVisibility()
    {
        bool visible = _attachments.Items.Count > 0;
        _attachments.Visible = visible;
        _attachLabel.Visible = visible;
    }

    private void AddAttachments()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = "첨부 파일 추가" };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
    }

    private void AddFiles(IEnumerable<string> files)
    {
        foreach (var f in files.Where(File.Exists))
            _attachments.Items.Add(new AttachmentEntry { FilePath = f });
        UpdateAttachmentVisibility();
        ContentChanged(startsDraft: false);
    }

    private void RemoveAttachments()
    {
        foreach (var item in _attachments.SelectedItems.Cast<object>().ToList())
            _attachments.Items.Remove(item);
        UpdateAttachmentVisibility();
        ContentChanged(startsDraft: false);
    }

    // ---------- Draft 보관 ----------

    /// <summary>
    /// 내용이 바뀜. 제목·본문을 처음 입력한 순간 바로 Draft 에 보관하고, 이미 보관 중이면 입력이 잠시 멈춘 뒤 갱신합니다.
    /// (받는 사람·첨부만 바꾼 것으로는 새로 보관하지 않음)
    /// </summary>
    private void ContentChanged(bool startsDraft)
    {
        _dirty = true;
        if (_sending) return;
        if (DraftId == null)
        {
            if (startsDraft) SaveDraftNow();
            return;
        }
        _draftPending = true;
        _draftTimer.Stop();
        _draftTimer.Start();
    }

    private ComposeDraft CurrentDraft() => new()
    {
        To = _to.Text,
        Cc = _cc.Text,
        Bcc = _bcc.Text,
        Subject = _subject.Text,
        Body = _body.Text,
        InReplyTo = _draft.InReplyTo,
        References = _draft.References,
        ForwardedAttachments = _attachments.Items.Cast<AttachmentEntry>().Where(e => e.Entity != null).Select(e => e.Entity!).ToList()
    };

    private IEnumerable<string> AttachmentFiles() =>
        _attachments.Items.Cast<AttachmentEntry>().Where(e => e.FilePath != null).Select(e => e.FilePath!);

    /// <summary>지금 내용으로 Draft 에 보관 (처음이면 새로 만들고, 아니면 바꿈)</summary>
    private void SaveDraftNow()
    {
        _draftTimer.Stop();
        if (_from.SelectedItem is not AccountChoice choice) return;
        try
        {
            var message = MessageBuilder.BuildDraft(choice.Account, CurrentDraft(), AttachmentFiles());
            DraftId = _store.SaveDraft(DraftId, choice.Account.Id, message).Id;
            _draftPending = false;
            _status.Text = $"Draft 에 보관함 ({DateTime.Now:HH:mm})";
        }
        catch (Exception ex)
        {
            Log.Error("save draft", ex);
            _status.Text = "Draft 에 보관하지 못했습니다: " + ex.Message;
        }
    }

    private void DeleteDraft()
    {
        _draftTimer.Stop();
        if (DraftId == null) return;
        try
        {
            if (_store.FindById(DraftId) is { } item) _store.Delete([item]);
        }
        catch (Exception ex) { Log.Error("delete draft", ex); }
        DraftId = null;
        _draftPending = false;
    }

    // ---------- 보내기 ----------

    private async Task SendAsync()
    {
        if (_sending) return;
        if (_from.SelectedItem is not AccountChoice choice) return;
        var account = choice.Account;

        if (string.IsNullOrWhiteSpace(_to.Text) && string.IsNullOrWhiteSpace(_cc.Text) && string.IsNullOrWhiteSpace(_bcc.Text))
        {
            MessageBox.Show(this, "받는 사람을 입력하세요.", "보내기", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _to.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_subject.Text) &&
            MessageBox.Show(this, "제목 없이 보내시겠습니까?", "보내기", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        MimeMessage message;
        try
        {
            message = MessageBuilder.Build(account, CurrentDraft(), AttachmentFiles());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "보내기", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _sending = true;
        _draftTimer.Stop();
        UseWaitCursor = true;
        _toolbar.Enabled = false;
        _status.Text = "보내는 중...";
        bool sent = false;
        try
        {
            await _mail.SendAsync(account, message, CancellationToken.None);
            _saveSettings(); // OAuth 토큰 갱신 반영
            _store.Add(account.Id, message, MailFolder.SentItems, "", isRead: true);
            DeleteDraft();   // 보냈으므로 Draft 에서 지움
            _dirty = false;
            sent = true;
        }
        catch (Exception ex)
        {
            Log.Error("send", ex);
            _status.Text = "보내기 실패";
            MessageBox.Show(this, "메일을 보내지 못했습니다.\n\n" + ex.Message, "보내기 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _sending = false;
            UseWaitCursor = false;
            _toolbar.Enabled = true;
            if (!IsDisposed && DraftId != null && _draftPending) SaveDraftNow();   // 보내지 못했으면 그사이 바뀐 내용 보관
        }
        // 보내는 중에는 닫기를 막으므로(FormClosing) 보내기 상태를 푼 뒤에 닫음
        if (sent) Close();
    }

    /// <summary>닫을 때 Draft 에서도 지울지 묻는 창 (테스트에서 바꿔 끼울 수 있음)</summary>
    internal Func<IWin32Window, DialogResult> AskDeleteDraft = owner => MessageBox.Show(owner,
        "작성 중인 메일이 Draft 에 보관되어 있습니다.\n\nDraft 에서도 삭제하시겠습니까?\n\n" +
        "[예] Draft 에서 삭제하고 닫기\n[아니요] Draft 에 남겨 두고 닫기 (나중에 Draft 에서 열어 이어서 쓸 수 있음)\n[취소] 계속 쓰기",
        "메일 쓰기", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

    private void ComposeForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_sending)
        {
            e.Cancel = true;
            return;
        }
        if (DraftId != null)
        {
            // 프로그램 종료·Windows 종료 때는 묻지 않고 최신 내용을 Draft 에 남김
            if (e.CloseReason is CloseReason.ApplicationExitCall or CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
            {
                if (_draftPending) SaveDraftNow();
                return;
            }
            switch (AskDeleteDraft(this))
            {
                case DialogResult.Yes:
                    DeleteDraft();
                    break;
                case DialogResult.No:
                    if (_draftPending) SaveDraftNow();
                    break;
                default:
                    e.Cancel = true;
                    break;
            }
            return;
        }
        // 제목·본문은 입력하지 않았지만 받는 사람·첨부 등을 바꾼 경우 (Draft 에 보관하지 않은 상태)
        if (_dirty && MessageBox.Show(this, "작성 중인 메일을 저장하지 않고 닫으시겠습니까?", "메일 쓰기",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            e.Cancel = true;
    }
}

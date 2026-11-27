using System.Text.Json;
using MailClient.Models;
using MailClient.Services;
using MimeKit;

namespace MailClient.Forms;

public class MainForm : DpiForm
{
    private const string DragFormat = "MailClient.MailItemIds";

    private readonly AppSettings _settings;
    // 설정에서 저장소 파일을 바꾸면 교체됨 (SwitchDatabaseAsync)
    private MailStore _store;
    private MailService _mail;
    private string? _startupDbError;

    // UI
    private readonly MenuStrip _menu = new();
    private readonly RibbonBar _ribbon = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _countLabel = new();
    private readonly ToolStripStatusLabel _nextCheckLabel = new();
    private readonly SplitContainer _mainSplit = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
    private readonly SplitContainer _rightSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly TreeView _tree = new()
    {
        Dock = DockStyle.Fill, HideSelection = false, AllowDrop = true, FullRowSelect = true,
        ShowLines = false, BorderStyle = BorderStyle.None   // ItemHeight 는 글꼴 기준으로 MainForm_Shown 에서 설정
    };
    private readonly MailListView _list = new()
    {
        EmptyText = NoMailText,
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
        MultiSelect = true, AllowColumnReorder = true, BorderStyle = BorderStyle.None,
        ShowItemToolTips = true   // 보낸 사람 열은 이름만 보이므로, 마우스를 올리면 주소까지 표시
    };
    private readonly MessageView _preview = new() { Dock = DockStyle.Fill };
    private readonly TextBox _searchBox = new();
    private readonly NotifyIcon _notify = new() { Icon = AppIcon.Window, Text = "MailClient" };

    // 상태
    private readonly System.Windows.Forms.Timer _checkTimer = new();
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 30_000 };
    private DateTime _nextCheck;
    private bool _checking;
    private CancellationTokenSource? _checkCts;
    private FolderRef? _currentFolder;
    private string? _previewItemId;
    private int _sortColumn = 2;
    private bool _sortDescending = true;
    private TreeNode? _dropHighlight;
    private Font? _boldFont;

    // 명령 (메뉴/툴바/컨텍스트 메뉴 상태 갱신용)
    // 메뉴 항목(ToolStripItem)과 리본 버튼(Control)을 함께 담음
    private readonly List<System.ComponentModel.Component> _needsSelection = [];
    private readonly List<System.ComponentModel.Component> _needsSingle = [];
    private readonly List<System.ComponentModel.Component> _needsAccount = [];
    private ToolStripMenuItem _moveMenu = null!;
    private ToolStripMenuItem _ctxMove = null!;
    private readonly List<System.ComponentModel.Component> _checkCommands = [];
    private readonly List<System.ComponentModel.Component> _stopCommands = [];
    private RibbonButton _checkButton = null!;
    private RibbonButton _previewButton = null!;
    private ToolStripMenuItem _previewMenu = null!;
    private const string CheckButtonText = "보내기/받기";

    private readonly System.Windows.Forms.Timer _serverSyncTimer = new() { Interval = 1500 };
    private readonly HashSet<string> _serverSyncPending = [];

    public MainForm()
    {
        _settings = SettingsService.Load();
        _store = OpenInitialStore();
        _mail = new MailService(_store, () => _settings.Junk);

        Text = "MailClient";
        Font = new Font("Malgun Gothic", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        // 저장된 논리 크기를 현재 화면 배율로 변환하고, 작업 영역을 넘지 않게 제한
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Size = new Size(
            Math.Min(LogicalToDeviceUnits(Math.Max(800, _settings.WindowLogicalWidth)), work.Width),
            Math.Min(LogicalToDeviceUnits(Math.Max(500, _settings.WindowLogicalHeight)), work.Height));
        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
        Icon = AppIcon.Window;
        KeyPreview = true;

        BuildMenu();
        BuildRibbon();
        BuildLayout();
        BuildListContextMenu();
        BuildTreeContextMenu();

        _statusStrip.Items.AddRange([_status, _countLabel, _nextCheckLabel]);

        Controls.Add(_mainSplit);
        Controls.Add(_statusStrip);
        Controls.Add(_ribbon);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        AttachStore(_store);
        _serverSyncTimer.Tick += async (_, _) =>
        {
            _serverSyncTimer.Stop();
            _serverSyncRun = SyncServerChangesAsync();
            await _serverSyncRun;
        };
        _checkTimer.Tick += async (_, _) => await CheckAllAsync();
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); ShowPreview(); };
        _clockTimer.Tick += (_, _) => UpdateNextCheckLabel();

        _notify.Visible = true;
        _notify.DoubleClick += (_, _) => RestoreFromTray();
        _notify.BalloonTipClicked += (_, _) => RestoreFromTray();

        Shown += MainForm_Shown;
        FormClosing += MainForm_FormClosing;

        // 화면 잠금을 풀고 다시 로그인하면 자동으로 보내기/받기 (정적 이벤트라 창이 닫힐 때 반드시 해제)
        Microsoft.Win32.SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
        FormClosed += (_, _) => Microsoft.Win32.SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        ToastNotifier.Activated += ToastNotifier_Activated;
        FormClosed += (_, _) => ToastNotifier.Activated -= ToastNotifier_Activated;

        _preview.LeftAlignHtml = _settings.HtmlLeftAlign;
        BuildTree();
        UpdateCommandStates();
    }

    // =====================================================================
    // 레이아웃
    // =====================================================================

    private void BuildLayout()
    {
        _mainSplit.Panel1.Controls.Add(_tree);
        _mainSplit.Panel2.Controls.Add(_rightSplit);
        _rightSplit.Panel1.Controls.Add(_list);
        _rightSplit.Panel1.Controls.Add(BuildSearchBar());   // Dock=Top: 목록 위
        _rightSplit.Panel2.Controls.Add(_preview);

        _list.Columns.Add("보낸 사람", LogicalToDeviceUnits(150));
        _list.Columns.Add("제목", LogicalToDeviceUnits(300));
        _list.Resize += (_, _) => FitFlexibleColumns();
        _list.Columns.Add("받은 날짜");
        _list.Columns.Add("크기", 0, HorizontalAlignment.Right);
        _list.Columns.Add("첨부", 0, HorizontalAlignment.Center);
        // 날짜/크기/첨부 열 너비는 폼에 붙어 실제 글꼴이 정해진 뒤(Shown) FitFixedColumns 에서 측정

        _tree.AfterSelect += Tree_AfterSelect;
        _tree.AfterExpand += Tree_AccountExpansionChanged;
        _tree.AfterCollapse += Tree_AccountExpansionChanged;
        _tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node; };
        _tree.DragEnter += Tree_DragOver;
        _tree.DragOver += Tree_DragOver;
        _tree.DragLeave += (_, _) => SetDropHighlight(null);
        _tree.DragDrop += Tree_DragDrop;

        _list.SelectedIndexChanged += (_, _) => { UpdateCommandStates(); _previewTimer.Stop(); _previewTimer.Start(); };
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.ItemDrag += List_ItemDrag;
        _list.ColumnClick += List_ColumnClick;
        _list.KeyDown += List_KeyDown;
    }

    /// <summary>메일 목록 바로 위의 검색 줄 (제목 / 보낸 사람 / 받는 사람)</summary>
    private Control BuildSearchBar()
    {
        int S(int v) => LogicalToDeviceUnits(v);
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(S(6), S(5), S(6), S(5)),
            BackColor = Color.FromArgb(250, 250, 250)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var icon = new Label
        {
            Text = RibbonGlyph.Search, Font = RibbonGlyph.GetFont(S(16)), ForeColor = Color.FromArgb(96, 96, 96),
            AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, S(6), 0)
        };
        _searchBox.Dock = DockStyle.Fill;
        _searchBox.Margin = Padding.Empty;
        _searchBox.PlaceholderText = "현재 폴더에서 검색 (제목 / 보낸 사람 / 받는 사람)";
        var clear = new LinkLabel { Text = "지우기", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(S(8), 0, 0, 0), Visible = false };
        clear.LinkClicked += (_, _) => { _searchBox.Clear(); _searchBox.Focus(); };
        _searchBox.TextChanged += (_, _) => { clear.Visible = _searchBox.TextLength > 0; RefreshList(true); };
        _searchBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { _searchBox.Clear(); e.SuppressKeyPress = true; } };
        bar.Controls.Add(icon, 0, 0);
        bar.Controls.Add(_searchBox, 1, 0);
        bar.Controls.Add(clear, 2, 0);

        var line = new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(218, 218, 218) };
        bar.Controls.Add(line, 0, 1);
        bar.SetColumnSpan(line, 3);
        return bar;
    }

    private void MainForm_Shown(object? sender, EventArgs e)
    {
        _notify.Icon = AppIcon.Tray();   // 화면 배율에 맞는 작은 아이콘 크기 (흐려지지 않게)

        ApplyFontMetrics();
        ApplyAccountExpansion();   // 저장된 계정 펼침/접힘 상태 (핸들 생성 후 다시 적용)
        try
        {
            _mainSplit.SplitterDistance = Math.Clamp(LogicalToDeviceUnits(_settings.TreeLogicalWidth), LogicalToDeviceUnits(120), Math.Max(LogicalToDeviceUnits(150), Width - LogicalToDeviceUnits(300)));
            _rightSplit.SplitterDistance = Math.Max(120, _rightSplit.Height * 2 / 5);
        }
        catch { /* 창 크기가 너무 작을 때 무시 */ }

        SetPreviewVisible(_settings.ShowPreviewPane);   // 저장된 미리 보기 창 표시 상태
        ApplyCheckInterval();

        if (_startupDbError != null)
        {
            var error = _startupDbError;
            BeginInvoke(() => MessageBox.Show(this, error, "메일 저장소", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }

        if (_settings.Accounts.Count == 0)
        {
            SetStatus("등록된 메일 계정이 없습니다. [파일] > [계정 편집] > [추가]로 계정을 등록하세요.");
            BeginInvoke(() =>
            {
                if (MessageBox.Show(this, "등록된 메일 계정이 없습니다. 지금 계정을 추가하시겠습니까?\n\n" +
                        "내보낸 계정 파일(.json)이 있으면 [아니요]를 누른 뒤 [파일] > [계정 편집] > [가져오기]로 불러올 수 있습니다.", "MailClient",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    AddAccount();
            });
        }
        else if (_settings.CheckOnStartup)
        {
            BeginInvoke(async () => await CheckAllAsync());
        }
    }

    /// <summary>
    /// 실제 글꼴(현재 화면 배율)로 측정하는 크기: 트리 줄 높이, 메일 목록 줄 높이, 날짜/크기/첨부 열 너비, 리본 높이.
    /// 창을 표시할 때와 화면 배율이 바뀐 뒤 호출합니다.
    /// </summary>
    private void ApplyFontMetrics()
    {
        // 트리 줄 높이: 글자 높이 + 위아래 여백 (고정 22px 은 화면 배율이 크면 글자보다 작아 빽빽함)
        _tree.ItemHeight = DpiForm.EvenItemHeight(TextRenderer.MeasureText("가Ag", _tree.Font).Height + LogicalToDeviceUnits(10));

        // 메일 목록 줄 간격: ListView 는 줄 높이 속성이 없어, 폭 1px·원하는 높이의 빈 이미지 목록으로 줄 높이를 정함
        _boldFont ??= new Font(_list.Font, FontStyle.Bold);
        int rowHeight = TextRenderer.MeasureText("가Ag", _boldFont).Height + LogicalToDeviceUnits(10);
        var oldImages = _list.SmallImageList;
        _list.SmallImageList = new ImageList { ImageSize = new Size(1, Math.Min(256, rowHeight)) };
        oldImages?.Dispose();

        FitFixedColumns();
        _ribbon.UpdateBodyHeight();
    }

    /// <summary>창이 배율이 다른 모니터로 옮겨졌거나 배율 설정이 바뀐 뒤: 글꼴로 측정한 크기와 굵은 글꼴을 새로 만듦</summary>
    protected override void OnDpiRescaled(int oldDpi, int newDpi)
    {
        var oldBold = _boldFont;
        _boldFont = new Font(_list.Font, FontStyle.Bold);
        ApplyFontMetrics();
        if (_currentFolder != null) RefreshList(true);   // 읽지 않은 메일의 굵은 글꼴을 새 글꼴로 교체
        oldBold?.Dispose();
        BoldTreeFont();   // 트리의 굵은 글꼴(읽지 않은 메일이 있는 폴더)도 새 크기로 교체
    }

    /// <summary>잠금 해제 후 메일 확인까지 기다리는 시간 (Wi-Fi·VPN 등 네트워크가 다시 연결될 시간)</summary>
    internal static TimeSpan UnlockCheckDelay { get; set; } = TimeSpan.FromSeconds(5);

    private void SystemEvents_SessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason != Microsoft.Win32.SessionSwitchReason.SessionUnlock) return;
        Log.Write("Session unlocked - scheduling mail check");
        // 시스템 이벤트 스레드에서 호출되므로 UI 스레드로 넘김
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(async () =>
        {
            await Task.Delay(UnlockCheckDelay);
            if (IsDisposed) return;
            SetStatus("화면 잠금 해제 - 메일을 확인합니다...");
            await CheckAllAsync();   // 이미 확인 중이면 무시됨 (중복 실행 방지)
        });
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _checkCts?.Cancel();
        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        if (WindowState == FormWindowState.Normal)
        {
            double scale = DeviceDpi / 96.0;
            _settings.WindowLogicalWidth = (int)Math.Round(Width / scale);
            _settings.WindowLogicalHeight = (int)Math.Round(Height / scale);
        }
        _settings.TreeLogicalWidth = (int)Math.Round(_mainSplit.SplitterDistance * 96.0 / DeviceDpi);
        SaveSettings();
        _notify.Visible = false;
        _notify.Dispose();
        _store.Dispose();
    }

    private void RestoreFromTray()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    // =====================================================================
    // 메뉴 / 툴바
    // =====================================================================

    private static ToolStripMenuItem Item(string text, Action onClick, Keys keys = Keys.None, string? keyDisplay = null)
    {
        var mi = new ToolStripMenuItem(text) { ShortcutKeys = keys };
        if (keyDisplay != null) mi.ShortcutKeyDisplayString = keyDisplay;
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private void BuildMenu()
    {
        // 파일
        var file = new ToolStripMenuItem("파일(&F)");
        var newMail = Item("새 메일(&N)", NewMail, Keys.Control | Keys.N);
        // 계정 추가/삭제는 계정 편집(메일 계정 목록 창)의 버튼으로
        var editAccount = Item("계정 편집(&E)...", () => ShowAccountList(SelectedTreeAccount()?.Id));
        var saveAs = Item("다른 이름으로 저장(.eml)(&S)...", SaveSelectedAs);
        var exit = Item("끝내기(&X)", Close);
        file.DropDownItems.AddRange([newMail, new ToolStripSeparator(), editAccount,
            new ToolStripSeparator(), saveAs, new ToolStripSeparator(), exit]);
        // 계정 편집(목록 창)은 계정이 없어도 추가·가져오기를 할 수 있도록 항상 사용 가능
        _needsAccount.Add(newMail);
        _needsSingle.Add(saveAs);

        // 메시지
        var msg = new ToolStripMenuItem("메시지(&M)");
        var open = Item("열기(&O)", OpenSelected, Keys.None, "Enter");
        var reply = Item("회신(&R)", () => Reply(false), Keys.Control | Keys.R);
        var replyAll = Item("전체 회신(&L)", () => Reply(true), Keys.Control | Keys.Shift | Keys.R);
        var forward = Item("전달(&W)", Forward, Keys.Control | Keys.F);
        var markRead = Item("읽음으로 표시(&Q)", () => MarkRead(true), Keys.Control | Keys.Q);
        var markUnread = Item("읽지 않음으로 표시(&U)", () => MarkRead(false), Keys.Control | Keys.U);
        _moveMenu = new ToolStripMenuItem("다른 폴더로 이동(&V)");
        _moveMenu.DropDownItems.Add("(없음)");
        _moveMenu.DropDownOpening += (_, _) => FillMoveMenu(_moveMenu);
        var delete = Item("삭제(&D)", DeleteSelected, Keys.None, "Del");
        var restore = Item("원래 폴더로 복원(&B)", RestoreSelected);
        var junk = Item("정크 메일로 표시(&J)", MarkAsJunk, Keys.Control | Keys.J);
        var notJunk = Item("정크 메일 아님(&K)", MarkAsNotJunk);
        msg.DropDownItems.AddRange([open, new ToolStripSeparator(), reply, replyAll, forward, new ToolStripSeparator(),
            markRead, markUnread, new ToolStripSeparator(), _moveMenu, delete, restore, new ToolStripSeparator(), junk, notJunk]);
        _needsSingle.AddRange([open, reply, replyAll, forward]);
        _needsSelection.AddRange([markRead, markUnread, _moveMenu, delete, restore, junk, notJunk]);

        // 도구
        var tools = new ToolStripMenuItem("도구(&T)");
        var check = Item("지금 메일 확인(&C)", async () => await CheckAllAsync(), Keys.F9);
        var stop = Item("메일 확인 중지", () => _checkCts?.Cancel());
        _stopCommands.Add(stop);
        _checkCommands.Add(check);
        var reapply = Item("Inbox에 정크 필터 다시 적용(&F)", ReapplyJunkFilter);
        var emptyDeleted = Item("Deleted Items 비우기", () => EmptyFolder(MailFolder.DeletedItems));
        var emptyJunk = Item("Junk Email 비우기", () => EmptyFolder(MailFolder.JunkEmail));
        var settings = Item("설정(&O)...", ShowSettings);
        tools.DropDownItems.AddRange([check, stop, new ToolStripSeparator(), reapply, emptyDeleted, emptyJunk,
            new ToolStripSeparator(), settings]);
        _needsAccount.Add(reapply);

        // 보기
        var view = new ToolStripMenuItem("보기(&V)");
        _previewMenu = new ToolStripMenuItem("미리 보기 창(&P)") { ShortcutKeys = Keys.Alt | Keys.P };
        _previewMenu.Click += (_, _) => SetPreviewVisible(!_settings.ShowPreviewPane);
        var refresh = Item("새로 고침", () => { BuildTree(); RefreshList(true); }, Keys.Control | Keys.F5);
        view.DropDownItems.AddRange([_previewMenu, refresh]);

        // 도움말
        var help = new ToolStripMenuItem("도움말(&H)");
        help.DropDownItems.Add(Item("데이터 폴더 열기", () =>
        {
            Directory.CreateDirectory(AppPaths.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.Root) { UseShellExecute = true });
        }));
        help.DropDownItems.Add(Item("정보(&A)", () => MessageBox.Show(this,
            "MailClient 1.0\n\nPOP3 / IMAP / SMTP 메일 클라이언트\n(.NET 9 Windows Forms, MailKit)\n\n데이터 위치: " + AppPaths.Root,
            "정보", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        _menu.Items.AddRange([file, msg, view, tools, help]);
    }

    private void BuildRibbon()
    {
        // ---- 홈 ----
        var home = _ribbon.AddTab("홈");

        var gNew = home.AddGroup("새로 만들기");
        var newMail = gNew.AddLarge(RibbonGlyph.Mail, "새 메일", "새 메일 쓰기 (Ctrl+N)", NewMail);

        var gSync = home.AddGroup("보내기/받기");
        _checkButton = gSync.AddLarge(RibbonGlyph.Sync, CheckButtonText, "모든 계정의 새 메일 확인 (F9)", async () => await CheckAllAsync());
        _checkCommands.Add(_checkButton);
        var stop = gSync.AddSmallStack((RibbonGlyph.Stop, "중지", "진행 중인 메일 확인 중지", () => _checkCts?.Cancel()));
        _stopCommands.Add(stop[0]);

        var gRespond = home.AddGroup("응답");
        var reply = gRespond.AddLarge(RibbonGlyph.Reply, "회신", "회신 (Ctrl+R)", () => Reply(false));
        var more = gRespond.AddSmallStack(
            (RibbonGlyph.ReplyAll, "전체 회신", "전체 회신 (Ctrl+Shift+R)", () => Reply(true)),
            (RibbonGlyph.Forward, "전달", "전달 (Ctrl+F)", Forward));

        var gDelete = home.AddGroup("삭제");
        var delete = gDelete.AddLarge(RibbonGlyph.Delete, "삭제", "삭제 (Del) - Deleted Items 에서는 영구 삭제", DeleteSelected);
        var restore = gDelete.AddSmallStack((RibbonGlyph.Restore, "복원", "Deleted Items 의 메일을 원래 폴더로 복원", RestoreSelected));

        var gMove = home.AddGroup("이동 / 정크");
        var move = gMove.AddLarge(RibbonGlyph.MoveToFolder, "이동", "다른 폴더로 이동", null);
        var moveMenu = new ContextMenuStrip();
        moveMenu.Opening += (_, e) =>
        {
            if (_currentFolder == null) { e.Cancel = true; return; }
            FillMoveItems(moveMenu.Items);
        };
        move.DropDown = moveMenu;
        var junkStack = gMove.AddSmallStack(
            (RibbonGlyph.Junk, "정크 메일", "정크 메일로 표시 (Ctrl+J)", MarkAsJunk),
            (RibbonGlyph.NotJunk, "정크 아님", "정크 메일이 아님 - Inbox 로 이동", MarkAsNotJunk));

        var gTags = home.AddGroup("태그");
        var tags = gTags.AddSmallStack(
            (RibbonGlyph.Read, "읽음으로 표시", "읽음으로 표시 (Ctrl+Q)", () => MarkRead(true)),
            (RibbonGlyph.Mail, "읽지 않음으로 표시", "읽지 않음으로 표시 (Ctrl+U)", () => MarkRead(false)));


        // 보기 (홈 탭에 포함)
        var gView = home.AddGroup("보기");
        _previewButton = gView.AddLarge(RibbonGlyph.PreviewPane, "미리 보기", "미리 보기 창 표시/숨기기 (Alt+P)",
            () => SetPreviewVisible(!_settings.ShowPreviewPane));
        gView.AddLarge(RibbonGlyph.Refresh, "새로 고침", "폴더 트리와 메일 목록 새로 고침 (Ctrl+F5)", () => { BuildTree(); RefreshList(true); });

        // ---- 도구 ----
        var tools = _ribbon.AddTab("도구");
        var gAccounts = tools.AddGroup("계정");
        gAccounts.AddLarge(RibbonGlyph.AddAccount, "계정 추가", "새 메일 계정 추가", AddAccount);
        var editAccounts = gAccounts.AddLarge(RibbonGlyph.Account, "계정 목록", "계정 편집 / 내보내기 / 가져오기", () => ShowAccountList(SelectedTreeAccount()?.Id));
        var gClean = tools.AddGroup("정리");
        // 작은 버튼은 한 열에 2개까지 (3개를 쌓으면 모든 그룹의 높이가 그만큼 커짐)
        var cleanup = gClean.AddSmallStack(
            (RibbonGlyph.Filter, "정크 필터 다시 적용", "Inbox 의 메일에 정크 필터를 다시 적용", ReapplyJunkFilter));
        gClean.AddSmallStack(
            (RibbonGlyph.Delete, "Deleted Items 비우기", "Deleted Items 의 메일을 영구 삭제", () => EmptyFolder(MailFolder.DeletedItems)),
            (RibbonGlyph.Junk, "Junk Email 비우기", "Junk Email 의 메일을 영구 삭제", () => EmptyFolder(MailFolder.JunkEmail)));
        var gSettings = tools.AddGroup("설정");
        gSettings.AddLarge(RibbonGlyph.Settings, "설정", "메일 확인 주기, 정크 메일 필터 등", ShowSettings);

        _needsAccount.AddRange([newMail, cleanup[0]]);   // 계정 목록은 가져오기를 위해 항상 사용 가능
        _needsSingle.AddRange([reply, more[0], more[1]]);
        _needsSelection.AddRange([delete, restore[0], move, junkStack[0], junkStack[1], tags[0], tags[1]]);
    }

    /// <summary>메뉴 항목과 리본 버튼을 같은 방식으로 켜고 끄기</summary>
    private static void SetEnabled(System.ComponentModel.Component c, bool enabled)
    {
        if (c is ToolStripItem t) t.Enabled = enabled;
        else if (c is Control k) k.Enabled = enabled;
    }

    private void BuildListContextMenu()
    {
        var ctx = new ContextMenuStrip();
        var open = Item("열기", OpenSelected);
        open.Font = new Font(open.Font, FontStyle.Bold);
        var reply = Item("회신", () => Reply(false));
        var replyAll = Item("전체 회신", () => Reply(true));
        var forward = Item("전달", Forward);
        var read = Item("읽음으로 표시", () => MarkRead(true));
        var unread = Item("읽지 않음으로 표시", () => MarkRead(false));
        _ctxMove = new ToolStripMenuItem("다른 폴더로 이동");
        _ctxMove.DropDownItems.Add("(없음)");
        _ctxMove.DropDownOpening += (_, _) => FillMoveMenu(_ctxMove);
        var delete = Item("삭제", DeleteSelected);
        var restore = Item("원래 폴더로 복원", RestoreSelected);
        var junk = Item("정크 메일로 표시", MarkAsJunk);
        var notJunk = Item("정크 메일 아님", MarkAsNotJunk);
        var safe = Item("보낸 사람을 허용 목록에 추가", () => AddSenderToList(safe: true, domain: false));
        var safeDomain = Item("보낸 사람 도메인을 허용 목록에 추가", () => AddSenderToList(safe: true, domain: true));
        var block = Item("보낸 사람 차단", () => AddSenderToList(safe: false, domain: false));
        var blockDomain = Item("보낸 사람 도메인 차단", () => AddSenderToList(safe: false, domain: true));
        var saveAs = Item("다른 이름으로 저장(.eml)...", SaveSelectedAs);

        ctx.Items.AddRange([open, reply, replyAll, forward, new ToolStripSeparator(), read, unread,
            new ToolStripSeparator(), _ctxMove, delete, restore, new ToolStripSeparator(), junk, notJunk,
            new ToolStripSeparator(), safe, safeDomain, block, blockDomain, new ToolStripSeparator(), saveAs]);

        ctx.Opening += (_, e) =>
        {
            var sel = SelectedItems();
            if (sel.Count == 0) { e.Cancel = true; return; }
            bool single = sel.Count == 1;
            foreach (var x in new ToolStripItem[] { open, reply, replyAll, forward, saveAs }) x.Enabled = single;
            var folder = _currentFolder?.Folder;
            bool drafts = folder == MailFolder.Drafts;
            open.Text = drafts ? "이어서 쓰기" : "열기";
            reply.Visible = replyAll.Visible = forward.Visible = _ctxMove.Visible = !drafts;
            restore.Visible = folder == MailFolder.DeletedItems;
            junk.Visible = folder != MailFolder.JunkEmail && !(folder?.ShowsRecipients() ?? false);
            notJunk.Visible = folder == MailFolder.JunkEmail;
            bool incoming = !(folder?.ShowsRecipients() ?? false);
            safe.Visible = safeDomain.Visible = block.Visible = blockDomain.Visible = incoming;
            delete.Text = folder == MailFolder.DeletedItems ? "영구 삭제" : "삭제";
        };
        _list.ContextMenuStrip = ctx;
    }

    private void BuildTreeContextMenu()
    {
        var ctx = new ContextMenuStrip();
        var check = Item("이 계정 메일 확인", async () =>
        {
            if (SelectedTreeAccount() is { } a) await CheckAllAsync([a]);
        });
        var newMail = Item("새 메일", NewMail);
        var edit = Item("계정 편집...", () => ShowAccountList(SelectedTreeAccount()?.Id));
        var remove = Item("계정 삭제", RemoveAccount);
        var empty = Item("폴더 비우기", () =>
        {
            if (_tree.SelectedNode?.Tag is FolderRef fr) EmptyFolder(fr.Folder);
        });
        var add = Item("계정 추가...", AddAccount);
        // 보관함 노드용
        var newArchive = Item("새 하위 폴더...", AddArchiveFolder);
        var renameArchive = Item("이름 바꾸기...", RenameArchiveFolder);
        var deleteArchive = Item("폴더 삭제", DeleteArchiveFolder);
        var accountItems = new ToolStripItem[] { check, newMail, empty, edit, remove, add };
        var sep1 = new ToolStripSeparator(); var sep2 = new ToolStripSeparator(); var sep3 = new ToolStripSeparator();
        ctx.Items.AddRange([check, newMail, sep1, empty, sep2, edit, remove, sep3, add, newArchive, renameArchive, deleteArchive]);

        ctx.Opening += (_, e) =>
        {
            if (_tree.SelectedNode?.Tag is FolderRef { IsDrafts: true }) { e.Cancel = true; return; }
            bool archive = _tree.SelectedNode?.Tag is FolderRef { IsArchive: true };
            foreach (var x in accountItems) x.Visible = !archive;
            sep1.Visible = sep2.Visible = sep3.Visible = !archive;
            newArchive.Visible = archive;
            // 최상위 Archive 는 이름 바꾸기·삭제 불가
            renameArchive.Visible = deleteArchive.Visible = archive && (_tree.SelectedNode?.Tag as FolderRef)?.ArchiveId is { Length: > 0 };
            if (archive) return;

            bool hasAccount = SelectedTreeAccount() != null;
            newMail.Enabled = edit.Enabled = remove.Enabled = hasAccount;
            check.Enabled = hasAccount && !_checking;
            check.Text = _checking ? "메일 확인 중..." : "이 계정 메일 확인";
            var fr = _tree.SelectedNode?.Tag as FolderRef;
            empty.Visible = fr is { Folder: MailFolder.DeletedItems or MailFolder.JunkEmail };
            if (fr != null) empty.Text = $"'{fr.Folder.DisplayName()}' 비우기";
        };
        _tree.ContextMenuStrip = ctx;
    }

    private void FillMoveMenu(ToolStripMenuItem menu) => FillMoveItems(menu.DropDownItems);

    /// <summary>"다른 폴더로 이동" 목록: 메일 계정의 폴더 + 보관함 폴더(들여쓰기로 계층 표시)</summary>
    private void FillMoveItems(ToolStripItemCollection items)
    {
        items.Clear();
        if (_currentFolder == null || _currentFolder.IsDrafts) return;
        foreach (var f in MailFolderExtensions.All)
        {
            if (!_currentFolder.IsArchive && f == _currentFolder.Folder) continue;
            var target = f;
            items.Add(Item(f.DisplayName(), () => MoveSelected(target)));
        }
        items.Add(new ToolStripSeparator());
        foreach (var (id, name, depth) in ArchiveTreeOrder())
        {
            var item = Item(new string(' ', depth * 4) + name, () => ArchiveItems(SelectedItems(), id));
            item.Enabled = !(_currentFolder.IsArchive && _currentFolder.ArchiveId == id);
            items.Add(item);
        }
    }

    /// <summary>보관함 폴더를 트리 순서(최상위 → 하위, 이름순)로: (ID, 이름, 깊이). 최상위 Archive 는 ID ""</summary>
    private List<(string Id, string Name, int Depth)> ArchiveTreeOrder()
    {
        var all = _store.GetArchiveFolders();
        var result = new List<(string, string, int)> { ("", MailFolder.Archive.DisplayName(), 0) };
        void Walk(string parent, int depth)
        {
            foreach (var f in all.Where(f => f.ParentId == parent))
            {
                result.Add((f.Id, f.Name, depth));
                Walk(f.Id, depth + 1);
            }
        }
        Walk("", 1);
        return result;
    }

    /// <summary>보관함 폴더로 옮기기 (로컬 전용, 서버의 사본은 그대로)</summary>
    private void ArchiveItems(List<MailItem> items, string archiveId)
    {
        if (items.Count == 0) return;
        _store.MoveToArchive(items, archiveId);
        var name = archiveId == "" ? MailFolder.Archive.DisplayName()
            : _store.GetArchiveFolders().FirstOrDefault(f => f.Id == archiveId)?.Name ?? MailFolder.Archive.DisplayName();
        SetStatus($"{items.Count}개 메시지를 보관함 '{name}'(으)로 옮겼습니다.");
    }

    private void UpdateCommandStates()
    {
        int sel = _list.SelectedItems.Count;
        bool hasAccount = _settings.Accounts.Count > 0;
        foreach (var i in _needsSelection) SetEnabled(i, sel > 0);
        foreach (var i in _needsSingle) SetEnabled(i, sel == 1);
        foreach (var i in _needsAccount) SetEnabled(i, hasAccount);

        // 메일 확인 중에는 "지금 메일 확인"을 막아 중복 실행 방지 (F9 단축키도 함께 비활성)
        foreach (var i in _checkCommands) SetEnabled(i, hasAccount && !_checking);
        foreach (var i in _stopCommands) SetEnabled(i, _checking);
        _checkButton.Text = _checking ? "확인 중..." : CheckButtonText;
    }

    // =====================================================================
    // 트리
    // =====================================================================

    private void BuildTree()
    {
        var previous = _currentFolder;
        var previousAccountNode = (_tree.SelectedNode?.Tag as MailAccount)?.Id;   // 계정 노드가 선택돼 있었으면 그대로 유지
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        foreach (var account in _settings.Accounts)
        {
            var root = new TreeNode(AccountNodeText(account)) { Tag = account, Name = account.Id, ToolTipText = account.EmailAddress };
            foreach (var folder in MailFolderExtensions.All)
                root.Nodes.Add(new TreeNode(folder.DisplayName()) { Tag = new FolderRef(account.Id, folder) });
            _tree.Nodes.Add(root);
        }
        // 보관함: 계정과 별개인 최상위 노드 + 사용자가 만든 하위 폴더 (이름순)
        var archiveFolders = _store.GetArchiveFolders();
        var archiveRoot = new TreeNode(MailFolder.Archive.DisplayName())
        {
            Tag = FolderRef.ArchiveOf(""), ToolTipText = "계정과 별개로 이 PC 에 보관하는 메일 (메일을 끌어다 놓으세요. 오른쪽 클릭으로 하위 폴더 추가)"
        };
        void AddArchiveChildren(TreeNode parent, string parentId)
        {
            foreach (var f in archiveFolders.Where(f => f.ParentId == parentId))
            {
                var node = new TreeNode(f.Name) { Tag = FolderRef.ArchiveOf(f.Id) };
                parent.Nodes.Add(node);
                AddArchiveChildren(node, f.Id);
            }
        }
        AddArchiveChildren(archiveRoot, "");
        _tree.Nodes.Add(archiveRoot);
        // 작성 중인 메일: 계정과 별개인 최상위 노드 (모든 계정의 메일)
        var draftsNode = new TreeNode(MailFolder.Drafts.DisplayName())
        {
            Tag = FolderRef.Drafts, ToolTipText = "작성 중인 메일 (제목이나 본문을 입력하면 자동으로 보관됩니다. 두 번 클릭하면 이어서 쓸 수 있습니다)"
        };
        _tree.Nodes.Add(draftsNode);
        _tree.EndUpdate();
        ApplyAccountExpansion();
        UpdateFolderCounts();

        TreeNode? toSelect = null;
        if (previousAccountNode != null && _tree.Nodes.ContainsKey(previousAccountNode)) toSelect = _tree.Nodes[previousAccountNode];
        else if (previous != null) toSelect = FindFolderNode(previous) ?? (previous.IsArchive ? archiveRoot : previous.IsDrafts ? draftsNode : null);
        // 기본: 첫 계정의 Inbox (계정이 없으면 보관함)
        toSelect ??= _tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is MailAccount && n.Nodes.Count > 0)?.Nodes[0] ?? archiveRoot;
        // 접어 둔 노드 안의 폴더를 선택하면 TreeView 가 펼쳐 버리므로, 가장 바깥의 접힌 상위 노드를 대신 선택
        // (계정 노드면 오른쪽에는 그 계정의 Inbox 표시)
        for (var p = toSelect?.Parent; p != null; p = p.Parent)
            if (_settings.CollapsedAccounts.Contains(ExpansionKey(p) ?? "")) toSelect = p;
        if (toSelect != null)
        {
            _applyingExpansion = true;
            try { _tree.SelectedNode = toSelect; }
            finally { _applyingExpansion = false; }
        }
        else
        {
            _currentFolder = null;
            _list.Items.Clear();
            _preview.Clear();
        }
        UpdateCommandStates();
    }

    private bool _applyingExpansion;

    /// <summary>저장된 계정 노드 펼침/접힘 상태 적용.
    /// 트리 핸들이 만들어지기 전의 Expand() 는 유지되지 않으므로 창 표시 후(Shown)에도 다시 호출함.</summary>
    private void ApplyAccountExpansion()
    {
        _applyingExpansion = true;
        try
        {
            foreach (var node in AllNodes(_tree.Nodes))
            {
                if (node.Nodes.Count == 0 || ExpansionKey(node) is not { } key) continue;
                if (_settings.CollapsedAccounts.Contains(key)) node.Collapse(ignoreChildren: true);
                else node.Expand();
            }
        }
        finally { _applyingExpansion = false; }
        UpdateAccountNodeFonts();
    }

    /// <summary>펼침 상태를 기억하는 노드의 키: 계정 ID, 보관함은 "archive:" + 폴더 ID. 그 밖의 노드는 null</summary>
    private static string? ExpansionKey(TreeNode node) => node.Tag switch
    {
        MailAccount a => a.Id,
        FolderRef { IsArchive: true } fr => "archive:" + fr.ArchiveId,
        _ => null
    };

    private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes)
        {
            yield return n;
            foreach (var c in AllNodes(n.Nodes)) yield return c;
        }
    }

    /// <summary>사용자가 계정·보관함 노드를 펼치거나 접으면 기억 (종료 시 설정에 저장)</summary>
    private void Tree_AccountExpansionChanged(object? sender, TreeViewEventArgs e)
    {
        if (e.Node == null) return;
        if (e.Node.Tag is MailAccount) UpdateAccountNodeFonts();   // 접히면 읽지 않은 메일 표시(굵게)
        if (_applyingExpansion || ExpansionKey(e.Node) is not { } key) return;
        var existing = AllNodes(_tree.Nodes).Select(ExpansionKey).OfType<string>().ToHashSet();
        var collapsed = _settings.CollapsedAccounts;
        collapsed.RemoveAll(k => k == key || !existing.Contains(k));   // 중복·없어진 노드 정리
        if (!e.Node.IsExpanded) collapsed.Add(key);
    }

    private static string AccountNodeText(MailAccount a) =>
        a.Enabled ? a.ToString() : a + " (사용 안 함)";

    private TreeNode? FindFolderNode(FolderRef fr) =>
        AllNodes(_tree.Nodes).FirstOrDefault(n => n.Tag is FolderRef r && r == fr);

    private void UpdateFolderCounts()
    {
        var archiveNames = _store.GetArchiveFolders().ToDictionary(f => f.Id, f => f.Name);
        foreach (var n in AllNodes(_tree.Nodes))
        {
            if (n.Tag is not FolderRef fr) continue;
            string name;
            int count;
            if (fr.IsDrafts)
            {
                int drafts = _store.DraftCount();
                var draftText = drafts > 0 ? $"{MailFolder.Drafts.DisplayName()} [{drafts}]" : MailFolder.Drafts.DisplayName();
                if (n.Text != draftText) n.Text = draftText;
                continue;
            }
            if (fr.IsArchive)
            {
                name = fr.ArchiveId is { Length: > 0 } id ? archiveNames.GetValueOrDefault(id, n.Text) : MailFolder.Archive.DisplayName();
                count = _store.ArchiveUnreadCount(fr.ArchiveId ?? "");
            }
            else
            {
                name = fr.Folder.DisplayName();
                count = fr.Folder switch
                {
                    MailFolder.SentItems => 0,
                    MailFolder.DeletedItems => 0,
                    _ => _store.UnreadCount(fr.AccountId, fr.Folder)
                };
            }
            var text = count > 0 ? $"{name} ({count})" : name;
            SetNodeBold(n, count > 0);   // 읽지 않은 메일이 있으면 굵게
            if (n.Text != text) n.Text = text;
            n.ForeColor = count > 0 ? Color.FromArgb(0, 90, 170) : _tree.ForeColor;
        }
        UpdateAccountNodeFonts();
    }

    /// <summary>계정 노드가 접혀 있고 그 계정의 Inbox 에 읽지 않은 메일이 있으면 계정 이름을 굵게 (새 메일이 있음을 알 수 있게)</summary>
    private void UpdateAccountNodeFonts()
    {
        foreach (TreeNode root in _tree.Nodes)
        {
            if (root.Tag is not MailAccount a) continue;
            bool bold = !root.IsExpanded && _store.UnreadCount(a.Id, MailFolder.Inbox) > 0;
            SetNodeBold(root, bold);
            root.ForeColor = bold ? Color.FromArgb(0, 90, 170) : _tree.ForeColor;
        }
    }

    private void SetNodeBold(TreeNode n, bool bold)
    {
        var font = bold ? BoldTreeFont() : null;
        if (n.NodeFont == font) return;
        n.NodeFont = font;
        var t = n.Text;
        n.Text = "";   // TreeView 는 글자를 다시 설정해야 굵은 글자 폭으로 다시 계산함 (안 하면 끝이 잘림)
        n.Text = t;
    }

    private Font? _boldTreeFont;

    /// <summary>트리 글꼴의 굵은 버전 (트리 글꼴이 바뀌면 새로 만듦)</summary>
    private Font BoldTreeFont()
    {
        var f = _tree.Font;
        if (_boldTreeFont == null || _boldTreeFont.FontFamily.Name != f.FontFamily.Name || _boldTreeFont.Size != f.Size)
        {
            var old = _boldTreeFont;
            _boldTreeFont = new Font(f, FontStyle.Bold);
            if (old == null) Disposed += (_, _) => _boldTreeFont?.Dispose();
            // 이전 굵은 글꼴을 쓰던 노드는 새 글꼴로 교체한 뒤 해제
            if (old != null)
            {
                foreach (var n in AllNodes(_tree.Nodes))
                    if (n.NodeFont == old) { n.NodeFont = _boldTreeFont; var t = n.Text; n.Text = ""; n.Text = t; }
                old.Dispose();
            }
        }
        return _boldTreeFont;
    }

    private MailAccount? SelectedTreeAccount() => _tree.SelectedNode?.Tag switch
    {
        MailAccount a => a,
        FolderRef fr => _settings.Accounts.FirstOrDefault(a => a.Id == fr.AccountId),
        _ => null
    };

    private MailAccount? CurrentAccount() =>
        (_currentFolder != null ? _settings.Accounts.FirstOrDefault(a => a.Id == _currentFolder.AccountId) : null)
        ?? SelectedTreeAccount()
        ?? _settings.Accounts.FirstOrDefault();

    private void Tree_AfterSelect(object? sender, TreeViewEventArgs e)
    {
        // 계정 노드: 트리 선택은 계정 노드에 그대로 두고, 오른쪽 목록에만 그 계정의 Inbox 를 표시
        var fr = e.Node?.Tag switch
        {
            MailAccount acc => new FolderRef(acc.Id, MailFolder.Inbox),
            FolderRef folder => folder,
            _ => null
        };
        if (fr == null || fr == _currentFolder) return;

        _currentFolder = fr;
        if (fr.IsDrafts)
        {
            Text = $"{MailFolder.Drafts.DisplayName()} - MailClient";
        }
        else if (fr.IsArchive)
        {
            var path = e.Node!.FullPath.Replace(_tree.PathSeparator, " / ");
            Text = $"{System.Text.RegularExpressions.Regex.Replace(path, @" \(\d+\)", "")} - MailClient";   // 읽지 않은 수 표시 제외
        }
        else
        {
            var account = _settings.Accounts.FirstOrDefault(a => a.Id == fr.AccountId);
            Text = $"{fr.Folder.DisplayName()} - {account} - MailClient";
        }
        UpdateSortGlyph();   // 열 이름 (받는 사람/보낸 사람, 날짜)
        RefreshList(false);
    }

    // ---- 보관함 폴더 관리 ----

    private FolderRef? SelectedArchiveNode() => _tree.SelectedNode?.Tag is FolderRef { IsArchive: true } fr ? fr : null;

    private string? ValidateArchiveName(string parentId, string name, string? exceptId = null)
    {
        if (name.Length == 0) return "폴더 이름을 입력하세요.";
        if (name.Length > 100) return "폴더 이름이 너무 깁니다.";
        bool dup = _store.GetArchiveFolders().Any(f => f.ParentId == parentId && f.Id != exceptId &&
                                                       string.Equals(f.Name, name, StringComparison.CurrentCultureIgnoreCase));
        return dup ? "같은 위치에 이름이 같은 폴더가 있습니다." : null;
    }

    private void AddArchiveFolder()
    {
        var parent = SelectedArchiveNode()?.ArchiveId ?? "";
        using var dlg = new TextInputForm("새 보관 폴더", "보관함에 만들 하위 폴더 이름:", "", n => ValidateArchiveName(parent, n));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var created = _store.AddArchiveFolder(parent, dlg.Value);   // ArchiveChanged → 트리 다시 그림
        // 상위 폴더를 펼치고 새 폴더 선택
        _settings.CollapsedAccounts.Remove("archive:" + parent);
        ApplyAccountExpansion();
        if (FindFolderNode(FolderRef.ArchiveOf(created.Id)) is { } node) _tree.SelectedNode = node;
    }

    private void RenameArchiveFolder()
    {
        if (SelectedArchiveNode() is not { ArchiveId: { Length: > 0 } id }) return;
        var folder = _store.GetArchiveFolders().FirstOrDefault(f => f.Id == id);
        if (folder == null) return;
        using var dlg = new TextInputForm("보관 폴더 이름 바꾸기", "새 이름:", folder.Name, n => ValidateArchiveName(folder.ParentId, n, folder.Id));
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Value == folder.Name) return;
        _store.RenameArchiveFolder(id, dlg.Value);
    }

    private void DeleteArchiveFolder()
    {
        if (SelectedArchiveNode() is not { ArchiveId: { Length: > 0 } id }) return;
        var folders = _store.GetArchiveFolders();
        var folder = folders.FirstOrDefault(f => f.Id == id);
        if (folder == null) return;
        var parentName = folder.ParentId == "" ? MailFolder.Archive.DisplayName() : folders.FirstOrDefault(f => f.Id == folder.ParentId)?.Name;
        if (MessageBox.Show(this,
                $"보관 폴더 '{folder.Name}'을(를) 삭제하시겠습니까?\n\n이 폴더와 그 아래 폴더의 메일은 삭제되지 않고 '{parentName}'(으)로 옮겨집니다.",
                "보관 폴더 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        int moved = _store.DeleteArchiveFolder(id);
        if (FindFolderNode(FolderRef.ArchiveOf(folder.ParentId)) is { } parentNode) _tree.SelectedNode = parentNode;
        SetStatus($"보관 폴더 '{folder.Name}'을(를) 삭제했습니다." + (moved > 0 ? $" 메일 {moved}개를 '{parentName}'(으)로 옮겼습니다." : ""));
    }

    // ---- 드래그 앤 드롭 (목록 → 트리 폴더) ----

    private void List_ItemDrag(object? sender, ItemDragEventArgs e)
    {
        var ids = SelectedItems().Select(i => i.Id).ToArray();
        if (ids.Length == 0) return;
        var data = new DataObject();
        data.SetData(DragFormat, ids);
        _list.DoDragDrop(data, DragDropEffects.Move);
        SetDropHighlight(null);
    }

    /// <summary>
    /// 놓을 수 있는 폴더:
    /// 보관함 폴더(어느 계정의 메일이든, 지금 보고 있는 보관함 폴더 제외) /
    /// 같은 계정의 다른 폴더 / 보관함에서 끌어온 메일은 아무 계정의 폴더.
    /// </summary>
    private FolderRef? DropTarget(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DragFormat) != true || _currentFolder == null) return null;
        var node = _tree.GetNodeAt(_tree.PointToClient(new Point(e.X, e.Y)));
        if (node?.Tag is not FolderRef fr || fr == _currentFolder) return null;
        if (fr.IsDrafts || _currentFolder.IsDrafts) return null;   // 작성 중인 메일은 메일 쓰기 창에서만 만들고 지움
        if (fr.IsArchive) return fr;
        if (_currentFolder.IsArchive) return fr;
        return fr.AccountId == _currentFolder.AccountId ? fr : null;
    }

    private void Tree_DragOver(object? sender, DragEventArgs e)
    {
        var target = DropTarget(e);
        e.Effect = target != null ? DragDropEffects.Move : DragDropEffects.None;
        SetDropHighlight(target != null ? FindFolderNode(target) : null);
    }

    private void Tree_DragDrop(object? sender, DragEventArgs e)
    {
        var target = DropTarget(e);
        SetDropHighlight(null);
        if (target == null || e.Data?.GetData(DragFormat) is not string[] ids) return;

        var items = ids.Select(_store.FindById).OfType<MailItem>().ToList();
        if (items.Count == 0) return;
        if (target.IsArchive)
            ArchiveItems(items, target.ArchiveId ?? "");
        else if (_currentFolder?.IsArchive == true)
        {
            // 보관함 → 계정 폴더 (다른 계정이면 그 계정의 메일이 됨)
            _store.MoveToAccountFolder(items, target.AccountId, target.Folder);
            var account = _settings.Accounts.FirstOrDefault(a => a.Id == target.AccountId);
            SetStatus($"{items.Count}개 메시지를 [{account}] '{target.Folder.DisplayName()}'(으)로 옮겼습니다.");
        }
        else
            MoveItems(items, target.Folder);
    }

    private void SetDropHighlight(TreeNode? node)
    {
        if (_dropHighlight == node) return;
        if (_dropHighlight != null)
        {
            _dropHighlight.BackColor = _tree.BackColor;
        }
        _dropHighlight = node;
        if (node != null)
        {
            node.BackColor = Color.FromArgb(204, 228, 247);
        }
    }

    // =====================================================================
    // 목록
    // =====================================================================

    private List<MailItem> SelectedItems() =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => (MailItem)i.Tag!).ToList();

    private MailItem? SingleSelected() => _list.SelectedItems.Count == 1 ? (MailItem)_list.SelectedItems[0].Tag! : null;

    private void Store_Changed(string accountId)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Store_Changed(accountId));
            return;
        }
        UpdateFolderCounts();
        // 보관함은 여러 계정의 메일을 담으므로 어느 계정이 바뀌어도 다시 그림
        if (_currentFolder?.AccountId == accountId || _currentFolder?.IsArchive == true || _currentFolder?.IsDrafts == true) RefreshList(true);
    }

    private IEnumerable<MailItem> FilteredSorted(IEnumerable<MailItem> items)
    {
        var q = _searchBox.Text.Trim();
        if (q.Length > 0)
            items = items.Where(i => i.Subject.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || i.From.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || i.To.Contains(q, StringComparison.OrdinalIgnoreCase));

        Func<MailItem, IComparable> key = _sortColumn switch
        {
            0 => i => DisplayNames(_currentFolder?.Folder.ShowsRecipients() == true ? i.To : i.From).ToLowerInvariant(),
            1 => i => i.Subject.ToLowerInvariant(),
            3 => i => i.Size,
            4 => i => i.HasAttachments,
            _ => i => i.Date
        };
        return _sortDescending ? items.OrderByDescending(key).ThenByDescending(i => i.Date)
                               : items.OrderBy(key).ThenByDescending(i => i.Date);
    }

    /// <summary>현재 폴더의 목록을 갱신. keepSelection=true 면 선택 항목 유지</summary>
    // ---------- Inbox 날짜 구획 ----------

    private static readonly string[] DateGroupNames =
        ["Today", "Yesterday", "This Week", "One Week Ago", "Two Weeks Ago", "Three Weeks Ago", "Last Month", "Older"];
    private ListViewGroup[]? _dateGroups;
    private bool _dateGroupsAscending;

    /// <summary>
    /// 받은 날짜 → 구획 번호 (0 = Today … 7 = Older). 주의 시작 요일은 시스템 지역 설정을 따름.
    /// Today: 오늘(미래 날짜 포함) / Yesterday: 어제 / This Week: 이번 주의 그 전 날 /
    /// One·Two·Three Weeks Ago: 1·2·3주 전 주 / Last Month: 그 전부터 지난달 1일까지 / Older: 그보다 전
    /// </summary>
    internal static int DateGroupIndex(DateTime date, DateTime today, DayOfWeek firstDayOfWeek)
    {
        var d = date.Date;
        today = today.Date;
        if (d >= today) return 0;
        if (d == today.AddDays(-1)) return 1;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
        if (d >= weekStart) return 2;
        if (d >= weekStart.AddDays(-7)) return 3;
        if (d >= weekStart.AddDays(-14)) return 4;
        if (d >= weekStart.AddDays(-21)) return 5;
        if (d >= new DateTime(today.Year, today.Month, 1).AddMonths(-1)) return 6;
        return 7;
    }

    private int DateGroupIndex(MailItem it) =>
        DateGroupIndex(it.Date.LocalDateTime, DateTime.Today, System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);

    /// <summary>구획 머리글을 표시 순서대로 준비 (날짜 오름차순 정렬이면 Older 부터). 접힘 상태 유지를 위해 순서가 바뀔 때만 다시 만듦</summary>
    private void EnsureDateGroups(bool ascending)
    {
        if (_dateGroups != null && _dateGroupsAscending == ascending) return;
        _dateGroups ??= DateGroupNames.Select(n => new ListViewGroup(n, HorizontalAlignment.Left)
        {
            CollapsedState = ListViewGroupCollapsedState.Expanded   // 머리글의 화살표로 접고 펼 수 있음
        }).ToArray();
        _dateGroupsAscending = ascending;
        _list.Groups.Clear();
        _list.Groups.AddRange(ascending ? _dateGroups.Reverse().ToArray() : _dateGroups);
    }

    private const string NoMailText = "(메일이 없습니다.)";
    private const string NoSearchResultText = "(검색 결과가 없습니다.)";

    private void RefreshList(bool keepSelection)
    {
        // 목록이 비었을 때 문구: 검색 중이면 검색 결과 없음
        _list.EmptyText = _searchBox.TextLength > 0 ? NoSearchResultText : NoMailText;
        if (_currentFolder == null)
        {
            _list.Items.Clear();
            _list.ShowGroups = false;
            return;
        }

        var items = FilteredSorted(_currentFolder.IsDrafts ? _store.GetDraftItems()
            : _currentFolder.IsArchive ? _store.GetArchiveItems(_currentFolder.ArchiveId ?? "")
            : _store.GetItems(_currentFolder.AccountId, _currentFolder.Folder)).ToList();

        // Inbox 만 받은 날짜로 구획. 목록 순서도 구획 순서로 맞춰 화면 순서 = 항목 순서 (삭제 후 다음 항목 선택 등)
        bool grouped = _currentFolder.Folder == MailFolder.Inbox;
        if (grouped)
        {
            bool ascending = _sortColumn == 2 && !_sortDescending;
            EnsureDateGroups(ascending);
            items = items.OrderBy(i => ascending ? -DateGroupIndex(i) : DateGroupIndex(i)).ToList();   // 안정 정렬: 구획 안은 기존 정렬 유지
        }
        if (_list.ShowGroups != grouped) _list.ShowGroups = grouped;

        // 같은 항목 집합/순서라면 텍스트·서식만 갱신 (깜빡임 및 선택 해제 방지)
        if (keepSelection && _list.Items.Count == items.Count &&
            _list.Items.Cast<ListViewItem>().Select(i => ((MailItem)i.Tag!).Id).SequenceEqual(items.Select(i => i.Id)))
        {
            for (int i = 0; i < items.Count; i++) ApplyItem(_list.Items[i], items[i]);
            UpdateCountLabel(items);
            return;
        }

        var selectedIds = keepSelection ? SelectedItems().Select(i => i.Id).ToHashSet() : [];
        _list.BeginUpdate();
        _list.Items.Clear();
        var lvItems = items.Select(it => { var lvi = new ListViewItem(["", "", "", "", ""]); ApplyItem(lvi, it); return lvi; }).ToArray();
        _list.Items.AddRange(lvItems);
        foreach (var lvi in lvItems)
            if (selectedIds.Contains(((MailItem)lvi.Tag!).Id)) lvi.Selected = true;
        _list.EndUpdate();

        if (_list.SelectedItems.Count == 0)
        {
            _previewItemId = null;
            _preview.Clear();
        }
        UpdateSortGlyph();
        UpdateCountLabel(items);
        UpdateCommandStates();
    }

    /// <summary>
    /// "이현철 &lt;chalse@example.com&gt;, b@example.com" → "이현철, b@example.com".
    /// 이름이 있으면 이름만, 없으면 주소. 주소 목록으로 읽을 수 없는 값은 그대로.
    /// </summary>
    internal static string DisplayNames(string addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses)) return addresses;
        if (!MimeKit.InternetAddressList.TryParse(addresses, out var list) || list.Count == 0) return addresses;
        var names = list.Mailboxes.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : m.Name.Trim()).ToList();
        return names.Count > 0 ? string.Join(", ", names) : addresses;
    }

    private void ApplyItem(ListViewItem lvi, MailItem it)
    {
        lvi.Tag = it;
        var group = _list.ShowGroups && _dateGroups != null ? _dateGroups[DateGroupIndex(it)] : null;
        if (!ReferenceEquals(lvi.Group, group)) lvi.Group = group;
        var people = it.Folder.ShowsRecipients() ? it.To : it.From;
        // 값이 바뀐 것만 설정: 같은 값이라도 설정하면 그 행을 다시 그리므로, 읽음 처리 한 번에 목록 전체가 다시 그려짐(깜빡임)
        SetText(0, DisplayNames(people));   // 이름만 (주소는 도움말·미리 보기 머리글에서 확인)
        SetText(1, string.IsNullOrEmpty(it.Subject) ? "(제목 없음)" : it.Subject);
        SetText(2, it.Date.LocalDateTime.ToString(it.Date.LocalDateTime.Date == DateTime.Today ? "HH:mm" : "yyyy-MM-dd HH:mm"));
        SetText(3, FormatSize(it.Size));
        SetText(4, it.HasAttachments ? "●" : "");
        _boldFont ??= new Font(_list.Font, FontStyle.Bold);
        var font = it.IsRead ? _list.Font : _boldFont;
        if (!ReferenceEquals(lvi.Font, font)) lvi.Font = font;
        var fore = it.Folder == MailFolder.JunkEmail ? Color.DimGray : _list.ForeColor;
        if (lvi.ForeColor != fore) lvi.ForeColor = fore;
        var tip = string.IsNullOrEmpty(it.Subject) ? people : people + "\n" + it.Subject;   // 마우스를 올리면 주소 + 제목
        if (lvi.ToolTipText != tip) lvi.ToolTipText = tip;

        void SetText(int i, string text)
        {
            if (lvi.SubItems[i].Text != text) lvi.SubItems[i].Text = text;
        }
    }

    private void UpdateCountLabel(List<MailItem> items)
    {
        int unread = items.Count(i => !i.IsRead);
        _countLabel.Text = $"항목 {items.Count}개" + (unread > 0 ? $", 읽지 않음 {unread}개" : "");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB"
    };

    private void List_ColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (_sortColumn == e.Column) _sortDescending = !_sortDescending;
        else
        {
            _sortColumn = e.Column;
            _sortDescending = e.Column is 2 or 3;
        }
        RefreshList(true);
        UpdateSortGlyph();
    }

    /// <summary>
    /// 날짜/크기/첨부 열 너비를 실제 글꼴로 측정해 정합니다 (DPI, 읽지 않은 메일의 굵은 글꼴, 정렬 표시 ▼ 포함).
    /// 고정 픽셀 값은 화면 배율에 따라 "2026-11-25 1…" 처럼 잘리기 때문.
    /// </summary>
    private void FitFixedColumns()
    {
        _boldFont ??= new Font(_list.Font, FontStyle.Bold);
        int Measure(IEnumerable<string> samples) =>
            samples.Max(s => Math.Max(TextRenderer.MeasureText(s, _list.Font).Width, TextRenderer.MeasureText(s, _boldFont).Width));

        int pad = LogicalToDeviceUnits(18);   // 셀 좌우 여백 + 열 구분선
        const string glyph = " ▼";
        _list.Columns[2].Width = Measure(["2026-12-28 22:58", "받은 날짜" + glyph, "보낸 날짜" + glyph]) + pad;
        _list.Columns[3].Width = Measure(["1024 KB", "999.9 MB", "1023 B", "크기" + glyph]) + pad;
        _list.Columns[4].Width = Measure(["●", "첨부" + glyph]) + pad;
        FitFlexibleColumns();
    }

    /// <summary>
    /// 날짜/크기/첨부 열을 뺀 나머지 폭을 "보낸 사람"(약 22%, 이름만 표시하므로 좁게)과 "제목"이 나눠 가집니다.
    /// 고정 열이 가로 스크롤 밖으로 밀려나지 않도록 목록 크기가 바뀔 때마다 다시 계산합니다.
    /// </summary>
    private void FitFlexibleColumns()
    {
        if (_list.Columns.Count < 5 || _list.ClientSize.Width <= 0) return;
        int fixedWidth = _list.Columns[2].Width + _list.Columns[3].Width + _list.Columns[4].Width;
        int available = _list.ClientSize.Width - fixedWidth - SystemInformation.VerticalScrollBarWidth;
        int sender = Math.Clamp(available * 22 / 100, LogicalToDeviceUnits(90), LogicalToDeviceUnits(160));
        _list.Columns[0].Width = sender;
        _list.Columns[1].Width = Math.Max(LogicalToDeviceUnits(120), available - sender);
    }

    private void UpdateSortGlyph()
    {
        var folder = _currentFolder?.Folder;
        string[] baseNames =
        [
            folder?.ShowsRecipients() == true ? "받는 사람" : "보낸 사람", "제목",
            folder switch { MailFolder.SentItems => "보낸 날짜", MailFolder.Drafts => "저장한 날짜", _ => "받은 날짜" }, "크기", "첨부"
        ];
        for (int i = 0; i < _list.Columns.Count; i++)
            _list.Columns[i].Text = baseNames[i] + (i == _sortColumn ? (_sortDescending ? " ▼" : " ▲") : "");
    }

    private void List_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Delete:
                DeleteSelected();
                e.Handled = true;
                break;
            case Keys.Enter:
                OpenSelected();
                e.Handled = true;
                break;
            case Keys.A when e.Control:
                _list.BeginUpdate();
                foreach (ListViewItem i in _list.Items) i.Selected = true;
                _list.EndUpdate();
                e.Handled = true;
                break;
        }
    }

    /// <summary>미리 보기 창 표시/숨기기 (툴바 버튼과 보기 메뉴 공통, 상태는 설정에 저장)</summary>
    private void SetPreviewVisible(bool visible)
    {
        _settings.ShowPreviewPane = visible;
        _rightSplit.Panel2Collapsed = !visible;
        _previewButton.Checked = visible;
        _previewMenu.Checked = visible;
        if (visible)
        {
            _previewItemId = null;   // 숨긴 동안 바뀐 선택을 다시 표시
            ShowPreview();
        }
    }

    private void ShowPreview()
    {
        // 미리 보기 창이 숨겨져 있으면 내용을 불러오지 않고 읽음 처리도 하지 않음
        if (!_settings.ShowPreviewPane) return;
        var item = SingleSelected();
        if (item == null)
        {
            if (_list.SelectedItems.Count == 0)
            {
                _previewItemId = null;
                _preview.Clear();
            }
            return;
        }
        if (_previewItemId == item.Id) return;

        try
        {
            var msg = _store.LoadMessage(item);
            _preview.ShowMessage(msg);
            _previewItemId = item.Id;
            SetNewMailIndicator(false);   // 메일을 열어 봄
            if (!item.IsRead) _store.SetRead([item], true);
        }
        catch (Exception ex)
        {
            _previewItemId = null;
            _preview.Clear();
            SetStatus("메시지를 열 수 없습니다: " + ex.Message);
        }
    }

    // =====================================================================
    // 메시지 명령
    // =====================================================================

    private MimeMessage? LoadSelectedMessage(out MailItem? item)
    {
        item = SingleSelected();
        if (item == null) return null;
        try
        {
            return _store.LoadMessage(item);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "메시지를 열 수 없습니다.\n" + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    public void NewMail() => OpenCompose(CurrentAccount(), new ComposeDraft());

    internal void OpenCompose(MailAccount? account, ComposeDraft draft, MailItem? existingDraft = null)
    {
        if (_settings.Accounts.Count == 0)
        {
            MessageBox.Show(this, "먼저 메일 계정을 추가하세요.", "새 메일", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var form = new ComposeForm(_settings.Accounts, account ?? _settings.Accounts[0], draft, _mail, _store, SaveSettings, existingDraft);
        form.Show();
    }

    /// <summary>Draft 의 메일을 메일 쓰기 창에서 이어서 씀 (이미 열려 있으면 그 창을 앞으로)</summary>
    private void OpenDraft(MailItem item)
    {
        var open = Application.OpenForms.OfType<ComposeForm>().FirstOrDefault(f => f.DraftId == item.Id);
        if (open != null)
        {
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
            return;
        }
        MimeMessage message;
        try { message = _store.LoadMessage(item); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "메시지를 열 수 없습니다.\n" + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var account = _settings.Accounts.FirstOrDefault(a => a.Id == item.AccountId);
        OpenCompose(account, MessageBuilder.ReadDraft(message), item);
    }

    internal void Reply(MailItem item, MimeMessage message, bool replyAll)
    {
        var account = _settings.Accounts.FirstOrDefault(a => a.Id == item.AccountId) ?? CurrentAccount();
        if (account == null) return;
        OpenCompose(account, MessageBuilder.CreateReply(message, account, replyAll));
    }

    internal void Forward(MailItem item, MimeMessage message)
    {
        var account = _settings.Accounts.FirstOrDefault(a => a.Id == item.AccountId) ?? CurrentAccount();
        OpenCompose(account, MessageBuilder.CreateForward(message));
    }

    private void Reply(bool replyAll)
    {
        var msg = LoadSelectedMessage(out var item);
        if (msg != null && item != null) Reply(item, msg, replyAll);
    }

    private void Forward()
    {
        var msg = LoadSelectedMessage(out var item);
        if (msg != null && item != null) Forward(item, msg);
    }

    private void OpenSelected()
    {
        if (SingleSelected() is { Folder: MailFolder.Drafts } draftItem)
        {
            OpenDraft(draftItem);
            return;
        }
        var msg = LoadSelectedMessage(out var item);
        if (msg == null || item == null) return;
        if (!item.IsRead) _store.SetRead([item], true);
        SetNewMailIndicator(false);
        new MessageForm(this, item, msg).Show();
    }

    private void SaveSelectedAs()
    {
        var item = SingleSelected();
        if (item == null) return;
        var name = string.IsNullOrWhiteSpace(item.Subject) ? "message" : item.Subject;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        using var dlg = new SaveFileDialog { FileName = name + ".eml", Filter = "전자 메일 (*.eml)|*.eml" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _store.SaveMessageTo(item, dlg.FileName);
    }

    private void MarkRead(bool read)
    {
        _store.SetRead(SelectedItems(), read);
        SetNewMailIndicator(false);
    }

    private void MoveSelected(MailFolder target) => MoveItems(SelectedItems(), target);

    private void MoveItems(List<MailItem> items, MailFolder target)
    {
        if (items.Count == 0) return;
        _store.Move(items, target);
        SetNewMailIndicator(false);
        SetStatus($"{items.Count}개 메시지를 '{target.DisplayName()}'(으)로 이동했습니다.");
    }

    internal void DeleteItems(List<MailItem> items)
    {
        if (items.Count == 0) return;

        // 지운 항목 중 목록에서 가장 위에 있던 위치. 삭제 후 목록을 다시 그리면 그 자리에 "다음 항목"이 오고,
        // 다음 항목이 없으면(맨 끝을 지움) 마지막 항목 = "이전 항목"을 선택
        var ids = items.Select(i => i.Id).ToHashSet();
        int anchor = _list.Items.Cast<ListViewItem>().Where(lvi => ids.Contains(((MailItem)lvi.Tag!).Id))
                          .Select(lvi => lvi.Index).DefaultIfEmpty(-1).Min();

        if (items.All(i => i.Folder == MailFolder.DeletedItems))
        {
            if (_settings.ConfirmPermanentDelete &&
                MessageBox.Show(this, $"선택한 {items.Count}개 메시지를 영구적으로 삭제하시겠습니까?", "영구 삭제",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            _store.Delete(items);   // Changed 이벤트로 목록이 즉시 다시 그려짐
            SetStatus($"{items.Count}개 메시지를 영구 삭제했습니다.");
            SetNewMailIndicator(false);
        }
        else
        {
            MoveItems(items.Where(i => i.Folder != MailFolder.DeletedItems).ToList(), MailFolder.DeletedItems);
        }

        if (anchor >= 0) SelectListIndex(Math.Min(anchor, _list.Items.Count - 1));
    }

    /// <summary>목록의 한 항목만 선택하고 보이게 스크롤 (index 가 음수면 아무것도 선택하지 않음)</summary>
    private void SelectListIndex(int index)
    {
        if (index < 0 || index >= _list.Items.Count) return;
        _list.BeginUpdate();
        _list.SelectedItems.Clear();
        var item = _list.Items[index];
        item.Selected = true;
        item.Focused = true;
        _list.EndUpdate();
        item.EnsureVisible();
        if (ContainsFocus && !_list.Focused && ActiveControl is not TextBoxBase) _list.Focus();   // 계속 Delete 키로 지울 수 있도록
    }

    private void DeleteSelected() => DeleteItems(SelectedItems());

    private void RestoreSelected()
    {
        var items = SelectedItems().Where(i => i.Folder == MailFolder.DeletedItems).ToList();
        foreach (var g in items.GroupBy(i => i.OriginalFolder ?? MailFolder.Inbox))
            _store.Move(g.ToList(), g.Key);
    }

    private void EmptyFolder(MailFolder folder)
    {
        var account = SelectedTreeAccount() ?? CurrentAccount();
        if (account == null) return;
        var items = _store.GetItems(account.Id, folder);
        if (items.Count == 0) return;
        if (MessageBox.Show(this, $"[{account}]의 '{folder.DisplayName()}' 폴더에 있는 {items.Count}개 메시지를 영구 삭제하시겠습니까?",
                "폴더 비우기", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _store.Delete(items);
    }

    private void MarkAsJunk()
    {
        var items = SelectedItems();
        if (items.Count == 0) return;
        var senders = items.Select(i => i.FromAddress).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        MoveItems(items, MailFolder.JunkEmail);

        if (senders.Count > 0 && MessageBox.Show(this,
                "다음 보낸 사람을 차단 목록에 추가할까요? 이후 이 주소에서 오는 메일은 항상 Junk Email 로 이동합니다.\n\n" + string.Join("\n", senders),
                "정크 메일", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            foreach (var s in senders)
            {
                _settings.Junk.SafeSenders.RemoveAll(x => x.Equals(s, StringComparison.OrdinalIgnoreCase));
                if (!_settings.Junk.BlockedSenders.Contains(s, StringComparer.OrdinalIgnoreCase)) _settings.Junk.BlockedSenders.Add(s);
            }
            SaveSettings();
        }
    }

    private void MarkAsNotJunk()
    {
        var items = SelectedItems();
        if (items.Count == 0) return;
        var senders = items.Select(i => i.FromAddress).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var i in items) _store.SetJunkFlag(i, false);
        MoveItems(items, MailFolder.Inbox);

        if (senders.Count > 0 && MessageBox.Show(this,
                "다음 보낸 사람을 허용(예외) 목록에 추가할까요? 이후 이 주소에서 오는 메일은 정크 메일로 분류되지 않습니다.\n\n" + string.Join("\n", senders),
                "정크 메일 아님", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            foreach (var s in senders)
            {
                _settings.Junk.BlockedSenders.RemoveAll(x => x.Equals(s, StringComparison.OrdinalIgnoreCase));
                if (!_settings.Junk.SafeSenders.Contains(s, StringComparer.OrdinalIgnoreCase)) _settings.Junk.SafeSenders.Add(s);
            }
            SaveSettings();
        }
    }

    private void AddSenderToList(bool safe, bool domain)
    {
        var values = SelectedItems()
            .Select(i => domain ? JunkFilter.DomainOf(i.FromAddress) : i.FromAddress.ToLowerInvariant())
            .Where(v => v.Length > 0)
            .Distinct()
            .ToList();
        if (values.Count == 0) return;

        var junk = _settings.Junk;
        var (add, remove) = (safe, domain) switch
        {
            (true, false) => (junk.SafeSenders, junk.BlockedSenders),
            (true, true) => (junk.SafeDomains, junk.BlockedDomains),
            (false, false) => (junk.BlockedSenders, junk.SafeSenders),
            _ => (junk.BlockedDomains, junk.SafeDomains)
        };
        foreach (var v in values)
        {
            remove.RemoveAll(x => x.Equals(v, StringComparison.OrdinalIgnoreCase));
            if (!add.Contains(v, StringComparer.OrdinalIgnoreCase)) add.Add(v);
        }
        SaveSettings();
        SetStatus($"{(safe ? "허용" : "차단")} 목록에 추가: {string.Join(", ", values)}");
    }

    private void ReapplyJunkFilter()
    {
        int moved = 0;
        Cursor = Cursors.WaitCursor;
        try
        {
            foreach (var account in _settings.Accounts)
            {
                var toJunk = new List<MailItem>();
                foreach (var item in _store.GetItems(account.Id, MailFolder.Inbox))
                {
                    try
                    {
                        var verdict = JunkFilter.Evaluate(_store.LoadMessage(item), _settings.Junk, account);
                        if (verdict.IsJunk) toJunk.Add(item);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("reapply junk", ex);
                    }
                }
                foreach (var i in toJunk) _store.SetJunkFlag(i, true);
                _store.Move(toJunk, MailFolder.JunkEmail);
                moved += toJunk.Count;
            }
        }
        finally
        {
            Cursor = Cursors.Default;
        }
        MessageBox.Show(this, $"{moved}개 메시지를 Junk Email 로 이동했습니다.", "정크 필터", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // =====================================================================
    // 계정 / 설정
    // =====================================================================

    private void AddAccount()
    {
        using var form = new AccountForm(null);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        AddNewAccount(form.Account);
    }

    /// <summary>새 계정 등록 (계정 추가 창 또는 계정 목록 창의 [추가]) 후 Inbox 선택, 메일 확인</summary>
    private void AddNewAccount(MailAccount account)
    {
        _settings.Accounts.Add(account);
        SaveSettings();
        BuildTree();
        var node = FindFolderNode(new FolderRef(account.Id, MailFolder.Inbox));
        if (node != null) _tree.SelectedNode = node;
        _ = CheckAllAsync([account]);
    }

    /// <summary>등록된 계정 목록 창 (추가 / 편집 / 삭제 / 내보내기 / 가져오기)</summary>
    private void ShowAccountList(string? selectAccountId = null)
    {
        using var form = new AccountListForm(_settings.Accounts, selectAccountId, ApplyEditedAccount, AddImportedAccounts,
            () => AccountExport.ExportedSettings.From(_settings), ApplyToolSettings, AddNewAccount, DeleteAccount);
        form.ShowDialog(this);
    }

    /// <summary>가져오기로 추가된 계정 반영 후 새 메일 확인</summary>
    private void AddImportedAccounts(List<MailAccount> accounts)
    {
        _settings.Accounts.AddRange(accounts);
        SaveSettings();
        BuildTree();
        _ = CheckAllAsync(accounts.Where(a => a.Enabled).ToList());
    }

    /// <summary>계정 편집 결과 반영 (계정 목록 창에서 호출)</summary>
    private void ApplyEditedAccount(MailAccount edited)
    {
        var idx = _settings.Accounts.FindIndex(a => a.Id == edited.Id);
        if (idx < 0) return;
        _settings.Accounts[idx] = edited;
        OAuthService.ClearCache(edited.Id);
        SaveSettings();
        BuildTree();
    }

    private void RemoveAccount()
    {
        var account = SelectedTreeAccount();
        if (account == null || !ConfirmDeleteAccount(this, account)) return;
        DeleteAccount(account);
    }

    /// <summary>계정 삭제 확인 (트리 우클릭 메뉴, 계정 목록 창 공통)</summary>
    internal static bool ConfirmDeleteAccount(IWin32Window owner, MailAccount account) =>
        MessageBox.Show(owner, $"계정 '{account}'을(를) 삭제하시겠습니까?\n이 계정의 로컬에 저장된 모든 메일도 삭제됩니다. (서버의 메일은 삭제되지 않습니다.)",
            "계정 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    /// <summary>계정과 그 계정의 로컬 메일 삭제 (확인은 호출자 담당)</summary>
    private void DeleteAccount(MailAccount account)
    {
        _settings.Accounts.RemoveAll(a => a.Id == account.Id);
        SaveSettings();
        _store.RemoveAccount(account.Id);
        OAuthService.ClearCache(account.Id);
        if (_currentFolder?.AccountId == account.Id) _currentFolder = null;
        BuildTree();
    }

    private async void ShowSettings()
    {
        // 설정 창은 복사본을 편집
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, SettingsService.JsonOptions), SettingsService.JsonOptions)!;
        using (var form = new SettingsForm(copy))
        {
            if (form.ShowDialog(this) != DialogResult.OK) return;
        }

        ApplyToolSettings(AccountExport.ExportedSettings.From(copy));

        // 메일 저장소 위치가 바뀌었으면 그 파일로 전환 (실패하면 설정도 그대로)
        if (!AppPaths.SamePath(AppPaths.ResolveDatabase(copy.DatabasePath), _store.DatabasePath))
            await SwitchDatabaseAsync(copy.DatabasePath);
        else if (copy.DatabasePath != _settings.DatabasePath)
        {
            _settings.DatabasePath = copy.DatabasePath;   // 같은 파일을 다른 표기로 지정한 경우
            SaveSettings();
        }
    }

    // =====================================================================
    // 메일 저장소 (SQLite) 열기 / 전환
    // =====================================================================

    private Task? _serverSyncRun;

    /// <summary>시작 시 설정의 저장소를 열고, 실패하면 이번 실행은 기본 위치를 사용 (설정 값은 유지해 다음에 다시 시도)</summary>
    private MailStore OpenInitialStore()
    {
        var path = AppPaths.ResolveDatabase(_settings.DatabasePath);
        bool isDefault = AppPaths.SamePath(path, AppPaths.DatabaseFile);
        try
        {
            return new MailStore(path, migrateLegacy: isDefault);
        }
        catch (Exception ex) when (!isDefault)
        {
            Log.Error($"open mail store {path}", ex);
            _startupDbError = $"설정한 메일 저장소 파일을 열 수 없습니다.\n\n{path}\n\n{ex.Message}\n\n" +
                              $"이번 실행에서는 기본 위치를 사용합니다:\n{AppPaths.DatabaseFile}\n\n" +
                              "[도구] > [설정] > [일반]에서 저장소 위치를 확인하세요.";
            return new MailStore();
        }
    }

    private void AttachStore(MailStore store)
    {
        store.Changed += Store_Changed;
        // 이동·삭제·읽음 표시를 IMAP 서버에도 반영 (계정 옵션). 잠시 모아서 한 번에 보냄
        store.ServerSyncEnabled = id => _settings.Accounts.FirstOrDefault(a => a.Id == id) is { Protocol: IncomingProtocol.Imap, SyncChangesToServer: true };
        store.ServerOpsQueued += Store_ServerOpsQueued;
        store.ArchiveChanged += Store_ArchiveChanged;
    }

    private void DetachStore(MailStore store)
    {
        store.Changed -= Store_Changed;
        store.ServerOpsQueued -= Store_ServerOpsQueued;
        store.ArchiveChanged -= Store_ArchiveChanged;
        store.ServerSyncEnabled = null;
    }

    /// <summary>보관함 폴더가 추가·이름 변경·삭제되면 트리를 다시 그림</summary>
    private void Store_ArchiveChanged()
    {
        if (InvokeRequired) { BeginInvoke(Store_ArchiveChanged); return; }
        BuildTree();
    }

    private void Store_ServerOpsQueued(string accountId)
    {
        void Queue() { _serverSyncPending.Add(accountId); _serverSyncTimer.Stop(); _serverSyncTimer.Start(); }
        if (InvokeRequired) BeginInvoke(Queue); else Queue();
    }

    /// <summary>
    /// 메일 저장소를 다른 파일로 바꾸고 트리·목록을 그 파일의 내용으로 다시 그립니다.
    /// 파일이 없으면 현재 메일을 복사할지 묻습니다. 실패하면 기존 저장소와 설정을 그대로 둡니다.
    /// </summary>
    internal async Task<bool> SwitchDatabaseAsync(string configured)
    {
        var path = AppPaths.ResolveDatabase(configured);
        if (AppPaths.SamePath(path, _store.DatabasePath))
        {
            _settings.DatabasePath = configured;
            SaveSettings();
            return true;
        }
        if (Application.OpenForms.OfType<ComposeForm>().Any())
        {
            MessageBox.Show(this, "작성 중인 메일 창이 있어 메일 저장소를 바꿀 수 없습니다.\n메일 창을 닫은 뒤 다시 지정하세요.",
                "메일 저장소", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        bool copy = false;
        if (!File.Exists(path))
        {
            var answer = MessageBox.Show(this,
                $"지정한 위치에 메일 저장소 파일이 없습니다.\n{path}\n\n현재 메일을 이 위치로 복사할까요?\n\n" +
                "[예]  현재 메일을 복사해서 사용\n[아니요]  빈 저장소로 시작 (메일 확인 때 서버에서 최근 메일을 다시 받음)\n[취소]  바꾸지 않음",
                "메일 저장소 위치", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            copy = answer == DialogResult.Yes;
        }

        SetStatus("메일 저장소를 바꾸는 중...");
        UseWaitCursor = true;
        try
        {
            await StopBackgroundWorkAsync();
            if (copy) _store.CopyTo(path);
            var newStore = new MailStore(path, migrateLegacy: AppPaths.SamePath(path, AppPaths.DatabaseFile));

            // 이전 저장소의 메일을 보여 주던 읽기 창은 닫음
            foreach (var f in Application.OpenForms.OfType<MessageForm>().ToList()) f.Close();

            var old = _store;
            DetachStore(old);
            _store = newStore;
            AttachStore(newStore);
            _mail = new MailService(_store, () => _settings.Junk);
            old.Dispose();

            _settings.DatabasePath = configured;
            SaveSettings();
            Log.Write($"Mail store switched: {old.DatabasePath} -> {path}{(copy ? " (copied)" : "")}");

            _previewItemId = null;
            _preview.Clear();
            BuildTree();
            RefreshList(false);
            SetStatus($"메일 저장소를 바꿨습니다: {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"switch mail store {path}", ex);
            SetStatus("메일 저장소를 바꾸지 못했습니다.");
            MessageBox.Show(this, $"메일 저장소 파일을 열 수 없어 기존 저장소를 계속 사용합니다.\n\n{path}\n\n{ex.Message}",
                "메일 저장소", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            UseWaitCursor = false;
            ApplyCheckInterval();
        }
    }

    /// <summary>저장소를 바꾸기 전에 메일 확인·서버 반영을 멈추고 끝날 때까지 기다림</summary>
    private async Task StopBackgroundWorkAsync()
    {
        _checkTimer.Stop();
        _serverSyncTimer.Stop();
        _serverSyncPending.Clear();   // 대기열은 저장소 파일에 남아 있어 나중에 처리됨
        _checkCts?.Cancel();
        for (int i = 0; _checking && i < 300; i++) await Task.Delay(100);
        if (_checking) throw new TimeoutException("진행 중인 메일 확인이 끝나지 않았습니다. 잠시 후 다시 시도하세요.");
        if (_serverSyncRun is { IsCompleted: false } run)
            await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>"도구 > 설정" 값 반영 (설정 창 확인, 또는 가져오기)</summary>
    private void ApplyToolSettings(AccountExport.ExportedSettings s)
    {
        s.ApplyTo(_settings);
        SaveSettings();
        ApplyCheckInterval();
        if (_preview.LeftAlignHtml != _settings.HtmlLeftAlign)
        {
            _preview.LeftAlignHtml = _settings.HtmlLeftAlign;
            _preview.Rerender();
        }
    }

    /// <summary>HTML 메일 왼쪽 정렬 설정 (읽기 창에서 사용)</summary>
    internal bool HtmlLeftAlign => _settings.HtmlLeftAlign;

    internal void SaveSettings()
    {
        try
        {
            SettingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Error("settings save", ex);
            SetStatus("설정 저장 실패: " + ex.Message);
        }
    }

    // =====================================================================
    // 주기적 메일 확인
    // =====================================================================

    private void ApplyCheckInterval()
    {
        _checkTimer.Stop();
        var minutes = Math.Clamp(_settings.CheckIntervalMinutes, 1, 1440);
        _checkTimer.Interval = minutes * 60_000;
        _checkTimer.Start();
        _clockTimer.Start();
        _nextCheck = DateTime.Now.AddMinutes(minutes);
        UpdateNextCheckLabel();
    }

    private void UpdateNextCheckLabel()
    {
        _nextCheckLabel.Text = _checking ? "확인 중..." : $"다음 확인: {_nextCheck:HH:mm} (주기 {_settings.CheckIntervalMinutes}분)";
    }

    private Task CheckAllAsync() => CheckAllAsync(_settings.Accounts.Where(a => a.Enabled).ToList());

    private async Task CheckAllAsync(List<MailAccount> accounts)
    {
        // 이미 확인 중이면 무시 (수동 요청, 주기 타이머, 계정 추가 후 확인 등 모든 경로 공통)
        if (_checking || accounts.Count == 0) return;
        _checking = true;
        _checkCts = new CancellationTokenSource();
        UpdateNextCheckLabel();
        UpdateCommandStates();

        int totalNew = 0, totalJunk = 0, totalRemoved = 0;
        var newItems = new List<MailItem>();
        var errors = new List<string>();
        try
        {
            foreach (var account in accounts)
            {
                SetStatus($"[{account}] 메일 확인 중...");
                try
                {
                    var r = await _mail.CheckAccountAsync(account, _checkCts.Token);
                    totalNew += r.NewCount;
                    if (r.NewItems != null) newItems.AddRange(r.NewItems);
                    totalJunk += r.JunkCount;
                    if (r.SyncError != null) errors.Add($"[{account}] 서버 반영: {r.SyncError}");
                    totalRemoved += r.RemovedCount;
                }
                catch (OperationCanceledException)
                {
                    errors.Add($"[{account}] 취소됨");
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"check {account.EmailAddress}", ex);
                    errors.Add($"[{account}] {ex.Message}");
                }
            }
        }
        finally
        {
            _checking = false;
            _checkCts.Dispose();
            _checkCts = null;
            SaveSettings(); // OAuth refresh token 갱신 반영
            if (!IsDisposed) UpdateCommandStates();
        }

        // 타이머 재시작 (수동 확인 후에도 주기 초기화)
        ApplyCheckInterval();

        var msg = $"메일 확인 완료 ({DateTime.Now:HH:mm}) - 새 메일 {totalNew}개" + (totalJunk > 0 ? $", 정크 {totalJunk}개" : "")
                  + (totalRemoved > 0 ? $", 서버에서 삭제된 메일 {totalRemoved}개를 Deleted Items로 이동" : "");
        if (errors.Count > 0) msg += "  |  오류: " + string.Join(" / ", errors);
        SetStatus(msg);

        if (totalNew > 0) SetNewMailIndicator(true);
        if (totalNew > 0 && _settings.ShowNotification) NotifyNewMail(newItems, totalNew);
    }

    /// <summary>새 메일 토스트 알림 (알림 센터). 토스트를 쓸 수 없으면 알림 영역 풍선 알림.</summary>
    private void NotifyNewMail(List<MailItem> items, int totalNew)
    {
        if (!ToastNotifier.ShowNewMail(items, totalNew))
            _notify.ShowBalloonTip(5000, "새 메일", $"새 메일 {totalNew}개가 도착했습니다.", ToolTipIcon.Info);
    }

    /// <summary>토스트 알림을 누름: 창을 앞으로 가져오고 해당 계정의 받은 편지함에서 가장 최근 새 메일을 선택</summary>
    private void ToastNotifier_Activated(string args)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (!Visible) Show();
            RestoreFromTray();
            var query = args.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            if (!query.TryGetValue("account", out var accountId)) return;
            if (FindFolderNode(new FolderRef(accountId, MailFolder.Inbox)) is { } node) _tree.SelectedNode = node;
            if (query.TryGetValue("id", out var id))
            {
                var lvi = _list.Items.Cast<ListViewItem>().FirstOrDefault(x => ((MailItem)x.Tag!).Id == id);
                if (lvi != null) SelectListIndex(lvi.Index);
            }
        });
    }

    private bool _newMailIndicator;

    /// <summary>
    /// 창·작업 표시줄·알림 영역 아이콘의 새 메일 표시(주황색 점). 메일 확인에서 새 메일을 받으면 켜고,
    /// 사용자가 메일을 열거나(미리 보기 포함) 지우기·이동·읽음 표시를 하면 끕니다.
    /// </summary>
    internal void SetNewMailIndicator(bool on)
    {
        if (_newMailIndicator == on) return;
        _newMailIndicator = on;
        Icon = AppIcon.ForWindow(on);
        var oldTray = _notify.Icon;
        _notify.Icon = AppIcon.Tray(on);
        if (!ReferenceEquals(oldTray, AppIcon.Window)) oldTray?.Dispose();
        _notify.Text = on ? "MailClient - 새 메일 있음" : "MailClient";
        if (!on) ToastNotifier.ClearNewMail();   // 메일을 확인했으므로 알림 센터의 새 메일 알림도 정리
    }

    internal bool NewMailIndicator => _newMailIndicator;

    /// <summary>대기 중인 변경을 IMAP 서버에 반영 (실패한 것은 다음 메일 확인 때 다시 시도)</summary>
    private async Task SyncServerChangesAsync()
    {
        var ids = _serverSyncPending.ToList();
        _serverSyncPending.Clear();
        foreach (var id in ids)
        {
            var account = _settings.Accounts.FirstOrDefault(a => a.Id == id);
            if (account == null) continue;
            try
            {
                var r = await _mail.SyncChangesAsync(account, CancellationToken.None);
                if (r.Failed > 0 && !IsDisposed)
                    SetStatus($"[{account}] 서버 반영 실패 {r.Failed}건 (다음 메일 확인 때 다시 시도): {r.Error}");
            }
            catch (Exception ex)
            {
                Log.Error($"server sync {account.EmailAddress}", ex);
                if (!IsDisposed) SetStatus($"[{account}] 서버에 변경을 반영하지 못했습니다 (다음 메일 확인 때 다시 시도): {ex.Message}");
            }
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.ToolTipText = text;
    }
}

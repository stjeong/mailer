using System.Diagnostics;
using MailClient.Services;

namespace MailClient.Forms;

/// <summary>도움말 > 정보: 버전과 데이터 위치 (위치는 누르면 탐색기로 열리는 링크)</summary>
public class AboutForm : DpiForm
{
    private const int IconLogical = 48;
    private readonly PictureBox _icon = new() { SizeMode = PictureBoxSizeMode.Zoom };
    private readonly LinkLabel _dataLink = new() { AutoSize = true };
    private readonly Label _title = new() { AutoSize = true };

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding S(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    public AboutForm()
    {
        SuspendLayout();
        Text = "정보";
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
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = S(18, 16, 18, 12)
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _icon.Size = new Size(S(IconLogical), S(IconLogical));
        _icon.Margin = S(0, 0, 14, 0);
        _icon.Image = AppIcon.Render(S(IconLogical), newMail: false);
        t.Controls.Add(_icon, 0, 0);
        t.SetRowSpan(_icon, 3);

        _title.Margin = S(0, 0, 0, 6);
        _title.Text = $"MailClient {UpdateService.CurrentVersionText}{(AppPaths.IsPackaged ? " (msix)" : "")}";
        _title.Font = TitleFont();
        t.Controls.Add(_title, 1, 0);

        t.Controls.Add(new Label
        {
            AutoSize = true, Margin = S(0, 0, 0, 12),
            Text = "POP3 / IMAP / SMTP 메일 클라이언트\n(.NET 9 Windows Forms, MailKit)"
        }, 1, 1);

        // "데이터 위치: <경로>" - 경로 부분만 링크
        var prefix = "데이터 위치: ";
        _dataLink.Text = prefix + AppPaths.Root;
        _dataLink.LinkArea = new LinkArea(prefix.Length, AppPaths.Root.Length);
        _dataLink.Margin = S(0, 0, 0, 16);
        _dataLink.LinkClicked += (_, _) => OpenDataFolder();
        new ToolTip().SetToolTip(_dataLink, "탐색기로 열기");
        t.Controls.Add(_dataLink, 1, 2);

        var ok = new Button { Text = "확인", AutoSize = true, DialogResult = DialogResult.OK, MinimumSize = new Size(S(84), S(28)), Margin = Padding.Empty };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false
        };
        buttons.Controls.Add(ok);
        t.Controls.Add(buttons, 0, 3);
        t.SetColumnSpan(buttons, 2);

        Controls.Add(t);
        AcceptButton = ok;
        CancelButton = ok;
        ResumeLayout(true);
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { AppPaths.Root }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("open data folder", ex);
            MessageBox.Show(this, "폴더를 열 수 없습니다.\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>제목: 본문 글꼴의 11/9 배 굵게. 창 글꼴에서 만들어야 배율이 바뀔 때 같이 커짐</summary>
    private Font TitleFont() => new(Font.FontFamily, Font.Size * 11f / 9f, FontStyle.Bold, Font.Unit);

    /// <summary>직접 지정한 제목 글꼴과 아이콘은 새 배율에 맞게 다시 만듦</summary>
    protected override void OnDpiRescaled(int oldDpi, int newDpi)
    {
        base.OnDpiRescaled(oldDpi, newDpi);
        var oldFont = _title.Font;
        _title.Font = TitleFont();
        oldFont.Dispose();
        var old = _icon.Image;
        _icon.Image = AppIcon.Render(S(IconLogical), newMail: false);
        old?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon.Image?.Dispose();
        base.Dispose(disposing);
    }
}

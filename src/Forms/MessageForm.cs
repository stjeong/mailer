using MailClient.Models;
using MimeKit;

namespace MailClient.Forms;

/// <summary>메일 읽기 창</summary>
public class MessageForm : DpiForm
{
    /// <summary>삭제 후 창 닫기 (리본 [삭제], Ctrl+D)</summary>
    private readonly Action _delete;

    public MessageForm(MainForm owner, MailItem item, MimeMessage message)
    {
        _delete = () => { owner.DeleteItems([item]); Close(); };
        Text = string.IsNullOrEmpty(message.Subject) ? "(제목 없음)" : message.Subject;
        Font = new Font("Malgun Gothic", 9f);
        // 화면 배율에 맞춘 크기 (작업 영역보다 크지 않게)
        var work = Screen.FromControl(owner).WorkingArea;
        Size = new Size(Math.Min(LogicalToDeviceUnits(900), work.Width * 9 / 10),
                        Math.Min(LogicalToDeviceUnits(700), work.Height * 9 / 10));
        StartPosition = FormStartPosition.CenterParent;
        Icon = AppIcon.Window;
        KeyPreview = true;

        var view = new MessageView { Dock = DockStyle.Fill, LeftAlignHtml = owner.HtmlLeftAlign };

        // 리본: [메시지] 탭 - 응답 | 삭제 | 동작
        var ribbon = new RibbonBar();
        var tab = ribbon.AddTab("메시지");
        var gRespond = tab.AddGroup("응답");
        gRespond.AddLarge(RibbonGlyph.Reply, "회신", "회신 (Ctrl+R)", () => owner.Reply(item, message, false));
        gRespond.AddLarge(RibbonGlyph.ReplyAll, "전체 회신", "전체 회신 (Ctrl+Shift+R)", () => owner.Reply(item, message, true));
        gRespond.AddLarge(RibbonGlyph.Forward, "전달", "전달 (Ctrl+F)", () => owner.Forward(item, message));
        var gDelete = tab.AddGroup("삭제");
        gDelete.AddLarge(RibbonGlyph.Delete, "삭제", "삭제 후 창 닫기 (Ctrl+D)", _delete);
        var gActions = tab.AddGroup("동작");
        gActions.AddLarge(RibbonGlyph.Print, "인쇄", "인쇄 (Ctrl+P)", view.Print);
        gActions.AddLarge(RibbonGlyph.Close, "닫기", "창 닫기 (Esc)", Close);

        Controls.Add(view);
        Controls.Add(ribbon);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.Control && e.KeyCode == Keys.R) { owner.Reply(item, message, e.Shift); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F) { owner.Forward(item, message); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.P) { view.Print(); e.Handled = true; }
        };

        Load += (_, _) => view.ShowMessage(message);
    }

    /// <summary>
    /// Ctrl+D: 삭제. 본문(WebBrowser)에 포커스가 있어도 동작하도록 KeyDown 이 아니라 여기서 처리
    /// (WebBrowser 의 Ctrl+D "즐겨찾기 추가"보다 먼저)
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.D))
        {
            _delete();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

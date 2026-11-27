namespace MailClient.Models;

/// <summary>로컬 저장소(SQLite messages 테이블)에 보관되는 메일 1건의 메타데이터. 원본은 raw 열에 저장</summary>
public class MailItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = "";
    public MailFolder Folder { get; set; } = MailFolder.Inbox;

    public string MessageId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string From { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string To { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public long Size { get; set; }
    public bool IsRead { get; set; }
    public bool HasAttachments { get; set; }
    public bool IsJunkByFilter { get; set; }

    /// <summary>서버 측 식별자 (IMAP UID 또는 POP3 UIDL)</summary>
    public string ServerId { get; set; } = "";

    /// <summary>Deleted Items 로 이동되기 전 폴더 (복원용)</summary>
    public MailFolder? OriginalFolder { get; set; }

}

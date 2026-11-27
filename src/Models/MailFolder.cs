namespace MailClient.Models;

public enum MailFolder
{
    Inbox,
    SentItems,
    JunkEmail,
    DeletedItems,
    /// <summary>계정과 별개인 로컬 보관함 (하위 폴더는 ArchiveFolder). 서버와 동기화하지 않음</summary>
    Archive,
    /// <summary>작성 중인 메일 (임시 보관). 메일의 계정 = 보낸 사람 계정. 로컬 전용, 서버와 동기화하지 않음</summary>
    Drafts
}

public static class MailFolderExtensions
{
    /// <summary>계정마다 있는 폴더 (Archive 는 계정과 별개라 제외)</summary>
    public static readonly MailFolder[] All =
        [MailFolder.Inbox, MailFolder.SentItems, MailFolder.JunkEmail, MailFolder.DeletedItems];

    public static string DisplayName(this MailFolder folder) => folder switch
    {
        MailFolder.Inbox => "Inbox",
        MailFolder.SentItems => "Sent Items",
        MailFolder.JunkEmail => "Junk Email",
        MailFolder.DeletedItems => "Deleted Items",
        MailFolder.Archive => "Archive",
        MailFolder.Drafts => "Draft",
        _ => folder.ToString()
    };

    /// <summary>목록에 보낸 사람 대신 받는 사람을 보여 주는 폴더 (보낸 편지함, 작성 중인 메일)</summary>
    public static bool ShowsRecipients(this MailFolder folder) => folder is MailFolder.SentItems or MailFolder.Drafts;
}

/// <summary>
/// 트리 노드 Tag 로 사용하는 폴더 참조.
/// 계정 폴더: (계정 ID, 폴더). 보관함: (AccountId = "", Folder = Archive, ArchiveId = 하위 폴더 ID, 최상위는 "").
/// </summary>
public sealed record FolderRef(string AccountId, MailFolder Folder, string? ArchiveId = null)
{
    public bool IsArchive => Folder == MailFolder.Archive;

    /// <summary>Draft 노드 (모든 계정의 작성 중인 메일)</summary>
    public bool IsDrafts => Folder == MailFolder.Drafts;

    public static FolderRef ArchiveOf(string archiveId) => new("", MailFolder.Archive, archiveId);

    public static readonly FolderRef Drafts = new("", MailFolder.Drafts);
}

/// <summary>보관함 하위 폴더 (ParentId "" = 최상위 Archive 바로 아래)</summary>
public sealed record ArchiveFolder(string Id, string ParentId, string Name);

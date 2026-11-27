using System.Globalization;
using System.Text.Json;
using MailClient.Models;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace MailClient.Services;

/// <summary>계정별 서버 동기화 상태</summary>
public class SyncState
{
    public uint ImapUidValidity { get; set; }
    public uint ImapLastUid { get; set; }
    public bool Pop3Initialized { get; set; }
    public HashSet<string> Pop3SeenUids { get; set; } = [];
}

/// <summary>IMAP 서버에 반영할 변경 1건 (server_ops 테이블)</summary>
/// <param name="MessageRef">로컬 메일 ID (영구 삭제 후에는 로컬에 없을 수 있음)</param>
/// <param name="ServerFolder">서버 폴더 전체 이름 ("" = INBOX)</param>
public sealed record ServerOp(long Seq, string AccountId, string MessageRef, ServerOpKind Kind, MailFolder Target,
    string ServerFolder, uint Uid, uint UidValidity, string MessageId, int Attempts);

public enum ServerOpKind { MarkRead, MarkUnread, Move, Delete }

/// <summary>받은 편지함에서 읽은 메일의 보낸 사람 (받는 사람 추천용)</summary>
public sealed record KnownSender(string Address, string Name, int ReadCount, DateTimeOffset LastRead);

/// <summary>
/// SQLite 기반 로컬 메일 저장소 (%AppData%\MailClient\mail.db).
/// <list type="bullet">
/// <item><c>messages</c>: 메일 메타데이터 + 원본 MIME(raw BLOB) + 검색용 본문 텍스트. 폴더는 folder 열로 관리.</item>
/// <item><c>sync_state</c>: 계정별 IMAP UID / POP3 UIDL 동기화 상태.</item>
/// <item><c>server_ops</c>: IMAP 서버에 아직 반영하지 못한 변경(이동·삭제·읽음). 오프라인이거나 재시작해도 유지.</item>
/// </list>
/// 하나의 연결을 lock 으로 직렬화해 사용합니다. 이전 버전의 파일 저장소(Data\{계정}\index.json + *.eml)는
/// 처음 열 때 자동으로 DB 로 옮기고, 원래 폴더는 Data.migrated-* 로 이름을 바꿔 남겨 둡니다.
/// </summary>
public sealed class MailStore : IDisposable
{
    private const int SchemaVersion = 4;

    private readonly object _gate = new();
    private readonly SqliteConnection _db;
    private readonly Dictionary<string, SyncState> _sync = new();
    private bool _disposed;

    /// <summary>폴더 내용이 바뀌었을 때 (계정 ID)</summary>
    public event Action<string>? Changed;

    /// <summary>서버에 반영할 변경이 대기열에 추가됐을 때 (계정 ID)</summary>
    public event Action<string>? ServerOpsQueued;

    /// <summary>계정의 변경을 IMAP 서버에도 반영할지 (계정 ID → 사용 여부). null 이면 반영 안 함</summary>
    public Func<string, bool>? ServerSyncEnabled { get; set; }

    public static string DefaultDatabasePath => AppPaths.DatabaseFile;

    public string DatabasePath { get; }

    public MailStore() : this(DefaultDatabasePath, migrateLegacy: true) { }

    public MailStore(string databasePath, bool migrateLegacy = false)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        bool isNew = !File.Exists(databasePath);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
            _db.Open();
            // SQLite 가 아니면 여기서 "file is not a database" 예외
            if (!isNew)
            {
                long tables = Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'");
                long mail = Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'messages'");
                if (tables > 0 && mail == 0)
                    throw new InvalidDataException("이 프로그램의 메일 저장소 파일이 아닙니다. (다른 프로그램의 SQLite 데이터베이스)");
            }

            if (isNew) Exec("PRAGMA auto_vacuum = INCREMENTAL;");   // 빈 DB 에서만 적용됨
            Exec("PRAGMA journal_mode = WAL;");
            Exec("PRAGMA synchronous = NORMAL;");
            CreateSchema();
        }
        catch
        {
            _db.Dispose();
            throw;
        }

        if (migrateLegacy) MigrateLegacyFiles();
    }

    /// <summary>현재 저장소 전체를 새 파일로 복사 (일관된 스냅숏, 대상 파일은 없어야 함)</summary>
    public void CopyTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        lock (_gate) Exec("VACUUM INTO $p", ("$p", Path.GetFullPath(path)));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _db.Dispose();
        }
    }

    // =====================================================================
    // 스키마
    // =====================================================================

    private void CreateSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS messages (
                id              TEXT    PRIMARY KEY,
                account_id      TEXT    NOT NULL,
                folder          INTEGER NOT NULL,          -- MailFolder (0=Inbox, 1=Sent, 2=Junk, 3=Deleted)
                original_folder INTEGER,                   -- Deleted Items 로 옮기기 전 폴더 (복원용)
                message_id      TEXT    NOT NULL DEFAULT '',
                subject         TEXT    NOT NULL DEFAULT '',
                sender          TEXT    NOT NULL DEFAULT '',
                sender_address  TEXT    NOT NULL DEFAULT '',
                recipients      TEXT    NOT NULL DEFAULT '',
                date            TEXT    NOT NULL,          -- ISO 8601 (시간대 오프셋 포함)
                size            INTEGER NOT NULL,
                is_read         INTEGER NOT NULL DEFAULT 0,
                has_attachments INTEGER NOT NULL DEFAULT 0,
                is_junk         INTEGER NOT NULL DEFAULT 0,
                server_id       TEXT    NOT NULL DEFAULT '',
                body_text       TEXT    NOT NULL DEFAULT '',   -- 검색용 일반 텍스트 본문
                raw             BLOB    NOT NULL               -- 원본 메시지 (RFC 5322 / MIME)
            );
            CREATE INDEX IF NOT EXISTS ix_messages_account_folder ON messages (account_id, folder);
            CREATE INDEX IF NOT EXISTS ix_messages_message_id     ON messages (account_id, message_id);

            CREATE TABLE IF NOT EXISTS sync_state (
                account_id        TEXT    PRIMARY KEY,
                imap_uid_validity INTEGER NOT NULL DEFAULT 0,
                imap_last_uid     INTEGER NOT NULL DEFAULT 0,
                pop3_initialized  INTEGER NOT NULL DEFAULT 0,
                pop3_seen_uids    TEXT    NOT NULL DEFAULT '[]'   -- JSON 배열
            );

            CREATE TABLE IF NOT EXISTS server_ops (
                seq           INTEGER PRIMARY KEY AUTOINCREMENT,   -- 처리 순서
                account_id    TEXT    NOT NULL,
                message_ref   TEXT    NOT NULL,                    -- 로컬 messages.id
                kind          INTEGER NOT NULL,                    -- ServerOpKind
                target        INTEGER NOT NULL DEFAULT 0,          -- Move 대상 로컬 폴더 (MailFolder)
                server_folder TEXT    NOT NULL DEFAULT '',         -- 현재 서버 위치 ("" = INBOX)
                server_uid    INTEGER NOT NULL DEFAULT 0,
                uid_validity  INTEGER NOT NULL DEFAULT 0,
                message_id    TEXT    NOT NULL DEFAULT '',         -- UID 로 못 찾을 때 Message-ID 로 검색
                attempts      INTEGER NOT NULL DEFAULT 0,
                last_error    TEXT    NOT NULL DEFAULT '',
                created       TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_server_ops_account ON server_ops (account_id, seq);
            """);
        // v2: 메일의 현재 서버 위치 (IMAP). 이전 버전 메일은 "" (= INBOX), UIDVALIDITY 0 (= 계정 동기화 상태 값)
        EnsureColumn("messages", "server_folder", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("messages", "server_uid_validity", "INTEGER NOT NULL DEFAULT 0");
        // v3: 보관함 (계정과 별개). folder = Archive 인 메일의 하위 폴더 ID ("" = 최상위)
        EnsureColumn("messages", "archive_folder", "TEXT NOT NULL DEFAULT ''");
        Exec("""
            CREATE TABLE IF NOT EXISTS archive_folders (
                id        TEXT PRIMARY KEY,
                parent_id TEXT NOT NULL DEFAULT '',   -- '' = 최상위 Archive 바로 아래
                name      TEXT NOT NULL,
                created   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_messages_archive ON messages (folder, archive_folder);
            """);
        // v4: 받은 편지함에서 읽은 메일의 보낸 사람 (메일 쓰기의 받는 사람 추천용)
        Exec("""
            CREATE TABLE IF NOT EXISTS known_senders (
                address    TEXT    PRIMARY KEY COLLATE NOCASE,
                name       TEXT    NOT NULL DEFAULT '',
                read_count INTEGER NOT NULL DEFAULT 0,
                last_read  TEXT    NOT NULL
            );
            """);

        if (Scalar<long>("SELECT COUNT(*) FROM schema_info") == 0)
            Exec("INSERT INTO schema_info (version) VALUES ($v)", ("$v", SchemaVersion));
        else
            Exec("UPDATE schema_info SET version = $v WHERE version < $v", ("$v", SchemaVersion));
    }

    private void EnsureColumn(string table, string column, string definition)
    {
        using var cmd = Cmd($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c", ("$c", column));
        if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            Exec($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    // =====================================================================
    // SQL 도우미 (호출자가 lock 을 잡은 상태에서 사용)
    // =====================================================================

    private SqliteCommand Cmd(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private int Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(sql, args);
        return cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(sql, args);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? default! : (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture);
    }

    private void InTransaction(Action action)
    {
        using var tx = _db.BeginTransaction();
        action();
        tx.Commit();
    }

    private const string ItemColumns =
        "id, account_id, folder, original_folder, message_id, subject, sender, sender_address, recipients, " +
        "date, size, is_read, has_attachments, is_junk, server_id";

    private static MailItem ReadItem(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        AccountId = r.GetString(1),
        Folder = (MailFolder)r.GetInt32(2),
        OriginalFolder = r.IsDBNull(3) ? null : (MailFolder)r.GetInt32(3),
        MessageId = r.GetString(4),
        Subject = r.GetString(5),
        From = r.GetString(6),
        FromAddress = r.GetString(7),
        To = r.GetString(8),
        Date = DateTimeOffset.Parse(r.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Size = r.GetInt64(10),
        IsRead = r.GetInt64(11) != 0,
        HasAttachments = r.GetInt64(12) != 0,
        IsJunkByFilter = r.GetInt64(13) != 0,
        ServerId = r.GetString(14)
    };

    private List<MailItem> QueryItems(string where, params (string, object?)[] args)
    {
        using var cmd = Cmd($"SELECT {ItemColumns} FROM messages WHERE {where}", args);
        using var r = cmd.ExecuteReader();
        var list = new List<MailItem>();
        while (r.Read()) list.Add(ReadItem(r));
        return list;
    }

    // =====================================================================
    // 조회
    // =====================================================================

    public List<MailItem> GetItems(string accountId, MailFolder folder)
    {
        lock (_gate)
            return QueryItems("account_id = $a AND folder = $f", ("$a", accountId), ("$f", (int)folder));
    }

    public MailItem? Find(string accountId, string id)
    {
        lock (_gate)
            return QueryItems("account_id = $a AND id = $id", ("$a", accountId), ("$id", id)).FirstOrDefault();
    }

    public int UnreadCount(string accountId, MailFolder folder)
    {
        lock (_gate)
            return (int)Scalar<long>("SELECT COUNT(*) FROM messages WHERE account_id = $a AND folder = $f AND is_read = 0",
                ("$a", accountId), ("$f", (int)folder));
    }

    public int Count(string accountId, MailFolder folder)
    {
        lock (_gate)
            return (int)Scalar<long>("SELECT COUNT(*) FROM messages WHERE account_id = $a AND folder = $f",
                ("$a", accountId), ("$f", (int)folder));
    }

    /// <summary>같은 Message-Id 의 받은 메일이 이미 있는지 (보낸 편지함 제외: 자신에게 보낸 메일은 받아야 함)</summary>
    public bool ContainsMessageId(string accountId, string messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return false;
        lock (_gate)
            return Scalar<long>("SELECT COUNT(*) FROM messages WHERE account_id = $a AND message_id = $m AND folder <> $sent",
                ("$a", accountId), ("$m", messageId), ("$sent", (int)MailFolder.SentItems)) > 0;
    }

    /// <summary>원본 메시지 바이트 (RFC 5322)</summary>
    public byte[] GetRawMessage(MailItem item)
    {
        lock (_gate)
        {
            var raw = Scalar<byte[]>("SELECT raw FROM messages WHERE id = $id", ("$id", item.Id));
            return raw ?? throw new FileNotFoundException("메시지를 찾을 수 없습니다.", item.Id);
        }
    }

    public MimeMessage LoadMessage(MailItem item)
    {
        using var ms = new MemoryStream(GetRawMessage(item));
        return MimeMessage.Load(ms);
    }

    /// <summary>원본 메시지를 .eml 파일로 저장</summary>
    public void SaveMessageTo(MailItem item, string path) => File.WriteAllBytes(path, GetRawMessage(item));

    // =====================================================================
    // 변경
    // =====================================================================

    /// <summary>메시지를 저장소에 추가합니다.</summary>
    /// <param name="uidValidity">IMAP: 받은 서버 폴더(INBOX)의 UIDVALIDITY (서버 변경 반영 시 UID 확인용)</param>
    public MailItem Add(string accountId, MimeMessage message, MailFolder folder, string serverId, bool isRead, bool notify = true,
        uint uidValidity = 0)
    {
        byte[] raw;
        using (var ms = new MemoryStream())
        {
            message.WriteTo(ms);
            raw = ms.ToArray();
        }

        var item = new MailItem
        {
            AccountId = accountId,
            Folder = folder,
            MessageId = message.MessageId ?? "",
            Subject = message.Subject ?? "",
            From = FormatAddresses(message.From),
            FromAddress = message.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            To = FormatAddresses(message.To),
            Date = message.Date == DateTimeOffset.MinValue ? DateTimeOffset.Now : message.Date,
            Size = raw.Length,
            IsRead = isRead,
            HasAttachments = message.Attachments.Any(),
            ServerId = serverId
        };

        lock (_gate)
        {
            Insert(item, raw, SafePlainText(message), orIgnore: false);
            if (uidValidity != 0)
                Exec("UPDATE messages SET server_uid_validity = $v WHERE id = $id", ("$v", (long)uidValidity), ("$id", item.Id));
        }
        if (notify) Changed?.Invoke(accountId);
        return item;
    }

    private void Insert(MailItem it, byte[] raw, string bodyText, bool orIgnore)
    {
        Exec($"""
            INSERT {(orIgnore ? "OR IGNORE " : "")}INTO messages
                ({ItemColumns}, body_text, raw)
            VALUES ($id, $a, $f, $of, $mid, $subj, $from, $fromAddr, $to, $date, $size, $read, $att, $junk, $srv, $body, $raw)
            """,
            ("$id", it.Id), ("$a", it.AccountId), ("$f", (int)it.Folder), ("$of", (int?)it.OriginalFolder),
            ("$mid", it.MessageId), ("$subj", it.Subject), ("$from", it.From), ("$fromAddr", it.FromAddress),
            ("$to", it.To), ("$date", it.Date.ToString("o", CultureInfo.InvariantCulture)), ("$size", it.Size),
            ("$read", it.IsRead ? 1 : 0), ("$att", it.HasAttachments ? 1 : 0), ("$junk", it.IsJunkByFilter ? 1 : 0),
            ("$srv", it.ServerId), ("$body", bodyText), ("$raw", raw));
    }

    private static string SafePlainText(MimeMessage message)
    {
        try { return JunkFilter.GetPlainText(message); }
        catch { return ""; }
    }

    /// <summary>
    /// 여러 항목에 같은 SQL 을 실행하고, 실제로 바뀐 계정들에 Changed 를 알립니다.
    /// <paramref name="serverOp"/> 가 있으면 서버 반영을 켠 계정의 IMAP 메일에 대해 같은 트랜잭션에서 대기열에 넣습니다
    /// (삭제는 행이 사라지기 전에 서버 위치를 기록해야 하므로 SQL 실행 전에 넣음).
    /// </summary>
    private void UpdateEach(IEnumerable<MailItem> items, string sql, (ServerOpKind Kind, MailFolder Target)? serverOp,
        params (string, object?)[] args)
    {
        var accounts = new HashSet<string>();
        var queued = new HashSet<string>();
        var syncEnabled = ServerSyncEnabled;
        lock (_gate)
        {
            InTransaction(() =>
            {
                foreach (var it in items)
                {
                    bool sync = serverOp != null && syncEnabled?.Invoke(it.AccountId) == true;
                    bool isDelete = serverOp?.Kind == ServerOpKind.Delete;
                    // 삭제: 행이 있을 때만 들어감 (INSERT ... SELECT FROM messages)
                    if (sync && isDelete && EnqueueServerOp(it.Id, serverOp!.Value.Kind, serverOp.Value.Target)) queued.Add(it.AccountId);

                    if (Exec(sql, [.. args, ("$id", it.Id)]) > 0)
                    {
                        accounts.Add(it.AccountId);
                        if (sync && !isDelete && EnqueueServerOp(it.Id, serverOp!.Value.Kind, serverOp.Value.Target)) queued.Add(it.AccountId);
                    }
                }
            });
        }
        foreach (var a in accounts) Changed?.Invoke(a);
        foreach (var a in queued) ServerOpsQueued?.Invoke(a);
    }

    /// <summary>메일의 현재 서버 위치를 기록해 대기열에 추가 (IMAP 으로 받은 메일만). 호출자가 lock 을 잡은 상태</summary>
    private bool EnqueueServerOp(string messageRef, ServerOpKind kind, MailFolder target)
    {
        if (kind is ServerOpKind.MarkRead or ServerOpKind.MarkUnread)
        {
            // 아직 보내지 않은 읽음/안 읽음 요청은 마지막 것만 의미 있음
            Exec("DELETE FROM server_ops WHERE message_ref = $id AND kind IN ($r, $u) AND attempts = 0",
                ("$id", messageRef), ("$r", (int)ServerOpKind.MarkRead), ("$u", (int)ServerOpKind.MarkUnread));
        }
        return Exec("""
            INSERT INTO server_ops (account_id, message_ref, kind, target, server_folder, server_uid, uid_validity, message_id, created)
            SELECT account_id, id, $k, $t, server_folder, CAST(substr(server_id, 6) AS INTEGER), server_uid_validity, message_id, $now
              FROM messages
             WHERE id = $id AND server_id LIKE 'imap:%'
            """,
            ("$k", (int)kind), ("$t", (int)target), ("$now", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)),
            ("$id", messageRef)) > 0;
    }

    /// <param name="queueServerOp">false: 서버에서 일어난 변경을 로컬에 맞추는 경우 (서버에 다시 보내지 않음)</param>
    public void Move(IEnumerable<MailItem> items, MailFolder target, bool queueServerOp = true) =>
        UpdateEach(items, """
            UPDATE messages
               SET original_folder = CASE WHEN $t = $deleted THEN folder ELSE NULL END,
                   folder = $t
             WHERE id = $id AND folder <> $t
            """,
            // 보관함은 로컬 전용: 보관함으로 옮기는 것은 서버에 반영하지 않음
            queueServerOp && target != MailFolder.Archive ? (ServerOpKind.Move, target) : null,
            ("$t", (int)target), ("$deleted", (int)MailFolder.DeletedItems));

    // =====================================================================
    // 보관함 (계정과 별개, 로컬 전용)
    // =====================================================================

    /// <summary>ID 로 메일 찾기 (보관함은 여러 계정의 메일을 담으므로 계정 없이)</summary>
    public MailItem? FindById(string id)
    {
        lock (_gate)
            return QueryItems("id = $id", ("$id", id)).FirstOrDefault();
    }

    public List<ArchiveFolder> GetArchiveFolders()
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT id, parent_id, name FROM archive_folders ORDER BY name COLLATE NOCASE");
            using var r = cmd.ExecuteReader();
            var list = new List<ArchiveFolder>();
            while (r.Read()) list.Add(new ArchiveFolder(r.GetString(0), r.GetString(1), r.GetString(2)));
            return list;
        }
    }

    public ArchiveFolder AddArchiveFolder(string parentId, string name)
    {
        var f = new ArchiveFolder(Guid.NewGuid().ToString("N"), parentId, name.Trim());
        lock (_gate)
            Exec("INSERT INTO archive_folders (id, parent_id, name, created) VALUES ($id, $p, $n, $c)",
                ("$id", f.Id), ("$p", f.ParentId), ("$n", f.Name), ("$c", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)));
        ArchiveChanged?.Invoke();
        return f;
    }

    public void RenameArchiveFolder(string id, string name)
    {
        lock (_gate) Exec("UPDATE archive_folders SET name = $n WHERE id = $id", ("$n", name.Trim()), ("$id", id));
        ArchiveChanged?.Invoke();
    }

    /// <summary>
    /// 하위 폴더 삭제. 그 폴더와 아래 폴더들의 메일은 삭제한 폴더의 상위 폴더로 옮깁니다
    /// (Deleted Items 로 옮겨 둔 보관함 메일도 복원할 곳이 남도록 함께 옮김). 옮긴 메일 수를 반환.
    /// </summary>
    public int DeleteArchiveFolder(string id)
    {
        int moved = 0;
        lock (_gate)
        {
            var all = GetArchiveFoldersUnlocked();
            var target = all.FirstOrDefault(f => f.Id == id);
            if (target == null) return 0;
            var remove = new HashSet<string> { id };
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var f in all)
                    if (remove.Contains(f.ParentId) && remove.Add(f.Id)) grew = true;
            }
            InTransaction(() =>
            {
                foreach (var fid in remove)
                {
                    moved += Exec("UPDATE messages SET archive_folder = $p WHERE archive_folder = $id AND folder = $a",
                        ("$p", target.ParentId), ("$id", fid), ("$a", (int)MailFolder.Archive));
                    Exec("UPDATE messages SET archive_folder = $p WHERE archive_folder = $id", ("$p", target.ParentId), ("$id", fid));
                    Exec("DELETE FROM archive_folders WHERE id = $id", ("$id", fid));
                }
            });
        }
        ArchiveChanged?.Invoke();
        return moved;
    }

    private List<ArchiveFolder> GetArchiveFoldersUnlocked()
    {
        using var cmd = Cmd("SELECT id, parent_id, name FROM archive_folders");
        using var r = cmd.ExecuteReader();
        var list = new List<ArchiveFolder>();
        while (r.Read()) list.Add(new ArchiveFolder(r.GetString(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    /// <summary>보관함 폴더 하나의 메일 (하위 폴더 제외, 모든 계정)</summary>
    public List<MailItem> GetArchiveItems(string archiveId)
    {
        lock (_gate)
            return QueryItems("folder = $a AND archive_folder = $id", ("$a", (int)MailFolder.Archive), ("$id", archiveId));
    }

    public int ArchiveUnreadCount(string archiveId)
    {
        lock (_gate)
            return (int)Scalar<long>("SELECT COUNT(*) FROM messages WHERE folder = $a AND archive_folder = $id AND is_read = 0",
                ("$a", (int)MailFolder.Archive), ("$id", archiveId));
    }

    /// <summary>보관함 폴더로 옮기기 (어느 계정의 어느 폴더에서든). 서버에는 반영하지 않음 (서버 사본은 그대로)</summary>
    public void MoveToArchive(IEnumerable<MailItem> items, string archiveId) =>
        UpdateEach(items, """
            UPDATE messages SET folder = $a, archive_folder = $arch, original_folder = NULL
             WHERE id = $id AND NOT (folder = $a AND archive_folder = $arch)
            """, null, ("$a", (int)MailFolder.Archive), ("$arch", archiveId));

    /// <summary>
    /// 보관함 메일을 계정 폴더로 옮기기. 원래 계정이면 일반 이동(서버 반영 옵션을 따름),
    /// 다른 계정이면 그 계정의 로컬 메일로 바꿉니다 (원래 서버 위치는 의미가 없으므로 지우고 서버에는 반영하지 않음).
    /// </summary>
    public void MoveToAccountFolder(IEnumerable<MailItem> items, string accountId, MailFolder target)
    {
        var list = items.ToList();
        var same = list.Where(i => i.AccountId == accountId).ToList();
        var other = list.Where(i => i.AccountId != accountId).ToList();
        if (same.Count > 0) Move(same, target);
        if (other.Count == 0) return;
        lock (_gate)
        {
            InTransaction(() =>
            {
                foreach (var it in other)
                    Exec("""
                        UPDATE messages SET account_id = $acc, folder = $t, original_folder = NULL,
                               server_id = '', server_folder = '', server_uid_validity = 0
                         WHERE id = $id
                        """, ("$acc", accountId), ("$t", (int)target), ("$id", it.Id));
            });
        }
        Changed?.Invoke(accountId);
        foreach (var a in other.Select(i => i.AccountId).Distinct()) Changed?.Invoke(a);
    }

    /// <summary>보관함 폴더 목록이 바뀌었을 때</summary>
    public event Action? ArchiveChanged;

    // =====================================================================
    // 작성 중인 메일 (Draft, 로컬 전용)
    // =====================================================================

    /// <summary>모든 계정의 작성 중인 메일</summary>
    public List<MailItem> GetDraftItems()
    {
        lock (_gate)
            return QueryItems("folder = $d", ("$d", (int)MailFolder.Drafts));
    }

    public int DraftCount()
    {
        lock (_gate)
            return (int)Scalar<long>("SELECT COUNT(*) FROM messages WHERE folder = $d", ("$d", (int)MailFolder.Drafts));
    }

    /// <summary>
    /// 작성 중인 메일을 저장 (같은 ID 가 있으면 내용을 바꿈). 서버와 동기화하지 않으므로 서버 ID 는 비워 둡니다.
    /// </summary>
    /// <param name="id">처음 저장이면 null</param>
    public MailItem SaveDraft(string? id, string accountId, MimeMessage message)
    {
        byte[] raw;
        using (var ms = new MemoryStream())
        {
            message.WriteTo(ms);
            raw = ms.ToArray();
        }
        var item = new MailItem
        {
            AccountId = accountId,
            Folder = MailFolder.Drafts,
            MessageId = message.MessageId ?? "",
            Subject = message.Subject ?? "",
            From = FormatAddresses(message.From),
            FromAddress = message.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            To = FormatAddresses(message.To),
            Date = DateTimeOffset.Now,
            Size = raw.Length,
            IsRead = true,
            HasAttachments = message.Attachments.Any(),
        };
        if (id != null) item.Id = id;

        string? previousAccount = null;
        lock (_gate)
        {
            InTransaction(() =>
            {
                if (id != null)
                {
                    previousAccount = Scalar<string>("SELECT account_id FROM messages WHERE id = $id", ("$id", id));
                    Exec("DELETE FROM messages WHERE id = $id", ("$id", id));
                }
                Insert(item, raw, SafePlainText(message), orIgnore: false);
            });
        }
        if (previousAccount != null && previousAccount != accountId) Changed?.Invoke(previousAccount);   // 보낸 사람 계정을 바꾼 경우
        Changed?.Invoke(accountId);
        return item;
    }

    /// <summary>
    /// 서버 삭제 확인 대상: IMAP 서버 INBOX 에 있는 것으로 기록된 로컬 Inbox 메일 중
    /// 메일 날짜가 <paramref name="since"/> 이후이거나, 최근 메일 <paramref name="recentCount"/>개 안에 드는 것 (합집합).
    /// </summary>
    public List<(MailItem Item, uint Uid, uint UidValidity)> GetServerDeleteCheckCandidates(string accountId, DateTimeOffset since, int recentCount)
    {
        lock (_gate)
        {
            using var cmd = Cmd($"""
                SELECT {ItemColumns}, server_uid_validity FROM messages
                 WHERE account_id = $a AND folder = $f AND server_id LIKE 'imap:%' AND server_folder = ''
                """, ("$a", accountId), ("$f", (int)MailFolder.Inbox));
            using var r = cmd.ExecuteReader();
            var all = new List<(MailItem, uint, uint)>();
            while (r.Read())
            {
                var it = ReadItem(r);
                uint.TryParse(it.ServerId.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var uid);
                all.Add((it, uid, (uint)r.GetInt64(15)));
            }
            // 날짜는 시간대가 섞인 문자열이라 SQL 대신 여기서 정렬·비교
            return all.OrderByDescending(x => x.Item1.Date)
                      .Where((x, index) => index < recentCount || x.Item1.Date >= since)
                      .ToList();
        }
    }

    /// <summary>서버에 아직 보내지 않은 변경이 있는 로컬 메일 ID</summary>
    public HashSet<string> PendingServerOpRefs(string accountId)
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT DISTINCT message_ref FROM server_ops WHERE account_id = $a", ("$a", accountId));
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>();
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    public void SetRead(IEnumerable<MailItem> items, bool read)
    {
        var list = items.ToList();
        UpdateEach(list, "UPDATE messages SET is_read = $r WHERE id = $id AND is_read <> $r",
            (read ? ServerOpKind.MarkRead : ServerOpKind.MarkUnread, MailFolder.Inbox), ("$r", read ? 1 : 0));
        if (read) RememberSenders(list.Where(i => i.Folder == MailFolder.Inbox));
    }

    // =====================================================================
    // 읽은 메일의 보낸 사람 (받는 사람 추천)
    // =====================================================================

    /// <summary>자동 발송 주소 (no-reply, noreply, do-not-reply 등): 받는 사람으로 추천하지 않음</summary>
    public static bool IsNoReply(string address)
    {
        int at = address.IndexOf('@');
        var local = (at >= 0 ? address[..at] : address).ToLowerInvariant();
        var compact = new string(local.Where(char.IsLetterOrDigit).ToArray());
        return compact.Contains("noreply") || compact.Contains("donotreply");
    }

    /// <summary>받은 편지함에서 읽은 메일의 보낸 사람을 기록 (no-reply 주소 제외)</summary>
    private void RememberSenders(IEnumerable<MailItem> items)
    {
        var now = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            foreach (var item in items)
            {
                var address = item.FromAddress.Trim();
                if (address.Length == 0 || !address.Contains('@') || IsNoReply(address)) continue;
                var name = "";
                if (InternetAddressList.TryParse(item.From, out var from))
                    name = from.Mailboxes.FirstOrDefault(m => string.Equals(m.Address, address, StringComparison.OrdinalIgnoreCase))?.Name?.Trim() ?? "";
                Exec("""
                    INSERT INTO known_senders (address, name, read_count, last_read) VALUES ($a, $n, 1, $t)
                    ON CONFLICT(address) DO UPDATE SET
                        read_count = read_count + 1,
                        last_read  = $t,
                        name       = CASE WHEN $n <> '' THEN $n ELSE name END
                    """, ("$a", address), ("$n", name), ("$t", now));
            }
        }
    }

    /// <summary>저장된 보낸 사람 전체 (설정 창 목록용, 최근에 읽은 순)</summary>
    public List<KnownSender> GetKnownSenders()
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT address, name, read_count, last_read FROM known_senders ORDER BY last_read DESC");
            using var r = cmd.ExecuteReader();
            var list = new List<KnownSender>();
            while (r.Read())
            {
                DateTimeOffset.TryParse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.None, out var last);
                list.Add(new KnownSender(r.GetString(0), r.GetString(1), r.GetInt32(2), last));
            }
            return list;
        }
    }

    /// <summary>저장된 보낸 사람 삭제 (다시 그 사람의 메일을 읽으면 다시 저장됨)</summary>
    public int DeleteKnownSenders(IEnumerable<string> addresses)
    {
        lock (_gate)
        {
            int n = 0;
            using var tx = _db.BeginTransaction();
            foreach (var a in addresses) n += Exec("DELETE FROM known_senders WHERE address = $a", ("$a", a));
            tx.Commit();
            return n;
        }
    }

    /// <summary>주소나 이름에 text 가 들어 있는 보낸 사람 (자주·최근에 읽은 순)</summary>
    public List<(string Address, string Name)> SearchKnownSenders(string text, int limit)
    {
        var escaped = text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        lock (_gate)
        {
            using var cmd = Cmd("""
                SELECT address, name FROM known_senders
                WHERE address LIKE $p ESCAPE '\' OR name LIKE $p ESCAPE '\'
                ORDER BY read_count DESC, last_read DESC
                LIMIT $n
                """, ("$p", "%" + escaped + "%"), ("$n", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<(string, string)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
            return list;
        }
    }

    public void SetJunkFlag(MailItem item, bool junk)
    {
        lock (_gate)
            Exec("UPDATE messages SET is_junk = $j WHERE id = $id", ("$j", junk ? 1 : 0), ("$id", item.Id));
    }

    /// <summary>영구 삭제</summary>
    public void Delete(IEnumerable<MailItem> items)
    {
        var list = items.ToList();
        UpdateEach(list, "DELETE FROM messages WHERE id = $id", (ServerOpKind.Delete, MailFolder.DeletedItems));
        lock (_gate) Exec("PRAGMA incremental_vacuum;");   // 지운 공간 반환
    }

    /// <summary>계정 데이터 전체 삭제</summary>
    public void RemoveAccount(string accountId)
    {
        lock (_gate)
        {
            InTransaction(() =>
            {
                // 보관함 메일은 계정과 별개로 보관하므로 남김 (서버 위치만 지움)
                Exec("DELETE FROM messages WHERE account_id = $a AND folder <> $arch", ("$a", accountId), ("$arch", (int)MailFolder.Archive));
                Exec("UPDATE messages SET server_id = '', server_folder = '', server_uid_validity = 0 WHERE account_id = $a",
                    ("$a", accountId));
                Exec("DELETE FROM sync_state WHERE account_id = $a", ("$a", accountId));
                Exec("DELETE FROM server_ops WHERE account_id = $a", ("$a", accountId));
            });
            _sync.Remove(accountId);
            Exec("PRAGMA incremental_vacuum;");
        }
    }

    public void NotifyChanged(string accountId) => Changed?.Invoke(accountId);

    // =====================================================================
    // 서버 반영 대기열
    // =====================================================================

    public List<ServerOp> GetPendingServerOps(string accountId) => QueryServerOps("account_id = $a", ("$a", accountId));

    /// <summary>요청 1건의 최신 상태 (앞선 이동으로 서버 위치가 바뀌었을 수 있음). 없으면 null</summary>
    public ServerOp? GetServerOp(long seq) => QueryServerOps("seq = $s", ("$s", seq)).FirstOrDefault();

    private List<ServerOp> QueryServerOps(string where, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = Cmd($"""
                SELECT seq, account_id, message_ref, kind, target, server_folder, server_uid, uid_validity, message_id, attempts
                  FROM server_ops WHERE {where} ORDER BY seq
                """, args);
            using var r = cmd.ExecuteReader();
            var list = new List<ServerOp>();
            while (r.Read())
                list.Add(new ServerOp(r.GetInt64(0), r.GetString(1), r.GetString(2), (ServerOpKind)r.GetInt32(3), (MailFolder)r.GetInt32(4),
                    r.GetString(5), (uint)r.GetInt64(6), (uint)r.GetInt64(7), r.GetString(8), r.GetInt32(9)));
            return list;
        }
    }

    public int PendingServerOpCount(string accountId)
    {
        lock (_gate)
            return (int)Scalar<long>("SELECT COUNT(*) FROM server_ops WHERE account_id = $a", ("$a", accountId));
    }

    /// <summary>처리 완료 (또는 더 이상 시도하지 않음)</summary>
    public void RemoveServerOp(long seq)
    {
        lock (_gate) Exec("DELETE FROM server_ops WHERE seq = $s", ("$s", seq));
    }

    public void FailServerOp(long seq, string error)
    {
        lock (_gate)
            Exec("UPDATE server_ops SET attempts = attempts + 1, last_error = $e WHERE seq = $s", ("$e", error), ("$s", seq));
    }

    /// <summary>서버에서 메일을 옮긴 뒤 새 위치 기록 (로컬 메일 + 그 메일의 남은 대기 요청). uid 0 = 모름(Message-ID 로 찾음)</summary>
    public void UpdateServerLocation(string messageRef, string serverFolder, uint uid, uint uidValidity)
    {
        lock (_gate)
        {
            InTransaction(() =>
            {
                Exec("UPDATE messages SET server_folder = $f, server_id = $sid, server_uid_validity = $v WHERE id = $id",
                    ("$f", serverFolder), ("$sid", "imap:" + uid), ("$v", (long)uidValidity), ("$id", messageRef));
                Exec("UPDATE server_ops SET server_folder = $f, server_uid = $u, uid_validity = $v WHERE message_ref = $id",
                    ("$f", serverFolder), ("$u", (long)uid), ("$v", (long)uidValidity), ("$id", messageRef));
            });
        }
    }

    // =====================================================================
    // 동기화 상태
    // =====================================================================

    /// <summary>동기화 상태 (MailService 가 직접 수정한 뒤 <see cref="SaveSyncState"/> 호출)</summary>
    public SyncState GetSyncState(string accountId)
    {
        lock (_gate)
        {
            if (_sync.TryGetValue(accountId, out var s)) return s;
            s = new SyncState();
            using (var cmd = Cmd("SELECT imap_uid_validity, imap_last_uid, pop3_initialized, pop3_seen_uids FROM sync_state WHERE account_id = $a",
                       ("$a", accountId)))
            using (var r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    s.ImapUidValidity = (uint)r.GetInt64(0);
                    s.ImapLastUid = (uint)r.GetInt64(1);
                    s.Pop3Initialized = r.GetInt64(2) != 0;
                    s.Pop3SeenUids = JsonSerializer.Deserialize<HashSet<string>>(r.GetString(3)) ?? [];
                }
            }
            _sync[accountId] = s;
            return s;
        }
    }

    public void SaveSyncState(string accountId)
    {
        lock (_gate)
            WriteSyncState(accountId, GetSyncState(accountId));
    }

    private void WriteSyncState(string accountId, SyncState s) =>
        Exec("""
            INSERT INTO sync_state (account_id, imap_uid_validity, imap_last_uid, pop3_initialized, pop3_seen_uids)
            VALUES ($a, $v, $u, $p, $seen)
            ON CONFLICT (account_id) DO UPDATE SET
                imap_uid_validity = excluded.imap_uid_validity,
                imap_last_uid     = excluded.imap_last_uid,
                pop3_initialized  = excluded.pop3_initialized,
                pop3_seen_uids    = excluded.pop3_seen_uids
            """,
            ("$a", accountId), ("$v", (long)s.ImapUidValidity), ("$u", (long)s.ImapLastUid),
            ("$p", s.Pop3Initialized ? 1 : 0), ("$seen", JsonSerializer.Serialize(s.Pop3SeenUids)));

    // =====================================================================
    // 이전 파일 저장소 → DB 이전
    // =====================================================================

    /// <summary>
    /// Data\{계정ID}\index.json, messages\*.eml, sync.json 을 DB 로 옮깁니다.
    /// 이미 있는 항목은 건너뛰므로(INSERT OR IGNORE) 도중에 실패해도 다시 실행하면 이어서 처리됩니다.
    /// 모두 성공하면 Data 폴더를 Data.migrated-yyyyMMddHHmmss 로 이름을 바꿔 백업으로 남깁니다.
    /// </summary>
    private void MigrateLegacyFiles()
    {
        var dataRoot = AppPaths.DataRoot;
        if (!Directory.Exists(dataRoot)) return;

        int migrated = 0, missing = 0;
        try
        {
            lock (_gate)
            {
                foreach (var dir in Directory.GetDirectories(dataRoot))
                {
                    var accountId = Path.GetFileName(dir);
                    var indexFile = Path.Combine(dir, "index.json");
                    var items = File.Exists(indexFile)
                        ? JsonSerializer.Deserialize<List<MailItem>>(File.ReadAllText(indexFile), SettingsService.JsonOptions) ?? []
                        : [];

                    InTransaction(() =>
                    {
                        foreach (var it in items)
                        {
                            var eml = Path.Combine(dir, "messages", it.Id + ".eml");
                            if (!File.Exists(eml)) { missing++; continue; }
                            var raw = File.ReadAllBytes(eml);
                            string body = "";
                            try
                            {
                                using var ms = new MemoryStream(raw);
                                body = SafePlainText(MimeMessage.Load(ms));
                            }
                            catch { /* 본문 파싱 실패해도 원본은 보관 */ }
                            it.AccountId = accountId;
                            it.Size = raw.Length;
                            Insert(it, raw, body, orIgnore: true);
                            migrated++;
                        }

                        var syncFile = Path.Combine(dir, "sync.json");
                        if (File.Exists(syncFile) && Scalar<long>("SELECT COUNT(*) FROM sync_state WHERE account_id = $a", ("$a", accountId)) == 0)
                        {
                            var s = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(syncFile), SettingsService.JsonOptions);
                            if (s != null) WriteSyncState(accountId, s);
                        }
                    });
                }
            }

            var backup = dataRoot + ".migrated-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            Directory.Move(dataRoot, backup);
            Log.Write($"Legacy mail files migrated to SQLite: {migrated} messages ({missing} missing .eml). Backup: {backup}");
        }
        catch (Exception ex)
        {
            // 실패해도 원본 파일은 그대로 남아 있고, 다음 실행 때 다시 시도
            Log.Error("legacy migration", ex);
        }
    }

    public static string FormatAddresses(InternetAddressList list) =>
        string.Join(", ", list.Mailboxes.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));
}

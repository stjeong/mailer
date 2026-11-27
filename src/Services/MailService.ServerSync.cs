using MailClient.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailFolder = MailClient.Models.MailFolder;

namespace MailClient.Services;

/// <param name="Done">서버에 반영한 변경 수</param>
/// <param name="Failed">이번에 실패한 변경 수 (다음에 다시 시도)</param>
/// <param name="Error">마지막 오류 (없으면 null)</param>
public sealed record ServerSyncResult(int Done, int Failed, string? Error);

/// <summary>
/// 이 프로그램에서 한 이동·삭제·읽음 표시를 IMAP 서버에 반영 (MailStore 의 server_ops 대기열을 순서대로 처리).
/// <list type="bullet">
/// <item>로컬 폴더 ↔ 서버 폴더: Inbox ↔ INBOX, Junk Email ↔ 서버 정크(스팸) 폴더, Deleted Items ↔ 서버 휴지통, Sent Items ↔ 서버 보낸 편지함.
///   서버 폴더는 SPECIAL-USE 표시로 찾고, 없으면 흔한 이름(Trash, Spam, [Gmail]/휴지통 등)으로 찾습니다.</item>
/// <item>메일은 저장된 UID(+UIDVALIDITY)로 찾고, UID 가 맞지 않으면 Message-ID 헤더로 검색합니다.</item>
/// <item>영구 삭제는 서버 휴지통으로 옮긴 뒤 휴지통에서 지웁니다 (Gmail 은 INBOX 에서 지우면 '보관'만 되므로).</item>
/// <item>서버에서 이미 없어진 메일은 완료로 처리합니다. 같은 요청이 <see cref="MaxAttempts"/> 번 실패하면 포기하고 기록합니다.</item>
/// </list>
/// 서버 → 로컬 방향(다른 기기에서 읽거나 지운 것)은 반영하지 않습니다.
/// </summary>
public partial class MailService
{
    private const int MaxAttempts = 5;
    private readonly Dictionary<string, SemaphoreSlim> _syncLocks = [];

    private SemaphoreSlim SyncLock(string accountId)
    {
        lock (_syncLocks)
        {
            if (!_syncLocks.TryGetValue(accountId, out var s)) _syncLocks[accountId] = s = new SemaphoreSlim(1, 1);
            return s;
        }
    }

    /// <summary>계정의 대기 중인 변경을 서버에 반영합니다. 같은 계정은 한 번에 하나만 처리합니다.</summary>
    public async Task<ServerSyncResult> SyncChangesAsync(MailAccount account, CancellationToken ct)
    {
        if (account.Protocol != IncomingProtocol.Imap || !account.SyncChangesToServer) return new(0, 0, null);
        var gate = SyncLock(account.Id);
        await gate.WaitAsync(ct);
        try
        {
            var ops = _store.GetPendingServerOps(account.Id);
            if (ops.Count == 0) return new(0, 0, null);

            using var client = new ImapClient();
            await client.ConnectAsync(account.IncomingHost, account.IncomingPort, account.IncomingSecurity, ct);
            await AuthenticateAsync(client, account, ct);
            var session = new SyncSession(client, _store.GetSyncState(account.Id).ImapUidValidity);

            int done = 0, failed = 0;
            string? lastError = null;
            var blocked = new HashSet<string>();   // 앞선 요청이 실패한 메일의 다음 요청은 순서를 지키기 위해 미룸
            foreach (var queued in ops)
            {
                ct.ThrowIfCancellationRequested();
                if (blocked.Contains(queued.MessageRef)) continue;
                // 앞선 이동이 같은 메일의 서버 위치를 바꿨을 수 있으므로 최신 상태로 다시 읽음
                if (_store.GetServerOp(queued.Seq) is not { } op) continue;
                try
                {
                    await ExecuteAsync(session, op, ct);
                    _store.RemoveServerOp(op.Seq);
                    done++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (client.IsConnected)
                {
                    failed++;
                    lastError = ex.Message;
                    blocked.Add(op.MessageRef);
                    if (op.Attempts + 1 >= MaxAttempts)
                    {
                        _store.RemoveServerOp(op.Seq);
                        Log.Write($"Server sync gave up [{account.EmailAddress}] {op.Kind} '{op.MessageId}': {ex.Message}");
                    }
                    else
                    {
                        _store.FailServerOp(op.Seq, ex.Message);
                        Log.Write($"Server sync failed [{account.EmailAddress}] {op.Kind} '{op.MessageId}' (attempt {op.Attempts + 1}): {ex.Message}");
                    }
                }
            }

            await client.DisconnectAsync(true, ct);
            if (done > 0) Log.Write($"Server sync [{account.EmailAddress}]: {done} change(s) applied, {failed} failed");
            return new(done, failed, lastError);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>한 번의 연결 동안 찾은 서버 폴더를 기억</summary>
    private sealed class SyncSession(ImapClient client, uint inboxUidValidity)
    {
        public ImapClient Client { get; } = client;
        /// <summary>이전 버전에서 받은 메일(UIDVALIDITY 미기록)은 INBOX 동기화 상태의 값으로 간주</summary>
        public uint InboxUidValidity { get; } = inboxUidValidity;
        public Dictionary<MailFolder, IMailFolder?> Special { get; } = [];
        public IList<IMailFolder>? AllFolders { get; set; }
    }

    private async Task ExecuteAsync(SyncSession s, ServerOp op, CancellationToken ct)
    {
        var folder = await GetServerFolderAsync(s, op.ServerFolder, ct);
        if (folder == null) return;   // 서버에서 폴더가 없어짐 → 메일도 없음

        await OpenWritableAsync(folder, ct);
        var uid = await LocateAsync(s, folder, op, ct);
        if (uid == null)
        {
            Log.Write($"Server sync: message not found on server, skipped ({op.Kind} '{op.MessageId}' in '{folder.FullName}')");
            return;
        }

        switch (op.Kind)
        {
            case ServerOpKind.MarkRead:
                await folder.StoreAsync(uid.Value, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true }, ct);
                break;

            case ServerOpKind.MarkUnread:
                await folder.StoreAsync(uid.Value, new StoreFlagsRequest(StoreAction.Remove, MessageFlags.Seen) { Silent = true }, ct);
                break;

            case ServerOpKind.Move:
            {
                if (op.Target == MailFolder.Archive) break;   // 보관함은 로컬 전용 (대기열에 들어오지 않지만 방어)
                var target = await GetTargetFolderAsync(s, op.Target, ct)
                    ?? throw new InvalidOperationException($"서버에서 '{op.Target.DisplayName()}'에 해당하는 폴더를 찾을 수 없습니다.");
                if (SameFolder(folder, target)) break;
                await MoveAsync(s, folder, uid.Value, target, op, ct);
                break;
            }

            case ServerOpKind.Delete:
            {
                // 휴지통으로 옮긴 뒤 휴지통에서 영구 삭제 (휴지통을 못 찾으면 현재 폴더에서 삭제)
                var trash = await GetTargetFolderAsync(s, MailFolder.DeletedItems, ct);
                var delFolder = folder;
                UniqueId? delUid = uid;
                if (trash != null && !SameFolder(folder, trash))
                {
                    delUid = await MoveAsync(s, folder, uid.Value, trash, op, ct);
                    delFolder = trash;
                    await OpenWritableAsync(trash, ct);
                    delUid ??= await FindByMessageIdAsync(trash, op.MessageId, ct);
                    if (delUid == null) break;   // 휴지통에서 못 찾음 (서버가 바로 지웠을 수 있음)
                }
                await delFolder.StoreAsync(delUid.Value, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
                if (s.Client.Capabilities.Contains(ImapCapability.UidPlus))
                    await delFolder.ExpungeAsync([delUid.Value], ct);   // 이 메일만
                else
                    await delFolder.ExpungeAsync(ct);                   // UIDPLUS 미지원: \Deleted 표시된 메일 모두
                break;
            }
        }
    }

    /// <summary>서버에서 메일을 옮기고 새 위치를 기록 (로컬 메일 + 남은 대기 요청). 새 UID 를 모르면 null</summary>
    private async Task<UniqueId?> MoveAsync(SyncSession s, IMailFolder from, UniqueId uid, IMailFolder to, ServerOp op, CancellationToken ct)
    {
        var newUid = await from.MoveToAsync(uid, to, ct);
        // 대상 폴더의 UIDVALIDITY (열지 않고 STATUS 로 조회). 새 UID 를 모르면 0 → 다음에 Message-ID 로 찾음
        if (!to.IsOpen) await to.StatusAsync(StatusItems.UidValidity, ct);
        _store.UpdateServerLocation(op.MessageRef, IsInbox(s, to) ? "" : to.FullName, newUid?.Id ?? 0, to.UidValidity);
        return newUid;
    }

    private static async Task OpenWritableAsync(IMailFolder folder, CancellationToken ct)
    {
        if (!folder.IsOpen || folder.Access != FolderAccess.ReadWrite)
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
    }

    private static async Task<UniqueId?> LocateAsync(SyncSession s, IMailFolder folder, ServerOp op, CancellationToken ct)
    {
        uint validity = op.UidValidity != 0 ? op.UidValidity : IsInbox(s, folder) ? s.InboxUidValidity : 0;
        if (op.Uid != 0 && validity != 0 && validity == folder.UidValidity)
        {
            var found = await folder.SearchAsync(SearchQuery.Uids([new UniqueId(validity, op.Uid)]), ct);
            if (found.Count > 0) return found[0];
        }
        return await FindByMessageIdAsync(folder, op.MessageId, ct);
    }

    private static async Task<UniqueId?> FindByMessageIdAsync(IMailFolder folder, string messageId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(messageId)) return null;
        var found = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", messageId), ct);
        return found.Count > 0 ? found[^1] : null;
    }

    private static bool IsInbox(SyncSession s, IMailFolder f) => SameFolder(f, s.Client.Inbox);

    private static bool SameFolder(IMailFolder a, IMailFolder b) =>
        string.Equals(a.FullName, b.FullName, a.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<IMailFolder?> GetServerFolderAsync(SyncSession s, string fullName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fullName) || fullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) return s.Client.Inbox;
        try { return await s.Client.GetFolderAsync(fullName, ct); }
        catch (FolderNotFoundException) { return null; }
    }

    // 서버 특수 폴더를 SPECIAL-USE 로 찾지 못할 때 쓰는 흔한 이름 (마지막 경로 요소와 비교)
    private static readonly Dictionary<MailFolder, string[]> FallbackNames = new()
    {
        [MailFolder.DeletedItems] = ["Trash", "Deleted Items", "Deleted Messages", "Deleted", "Bin", "휴지통", "지운 편지함"],
        [MailFolder.JunkEmail] = ["Junk", "Junk Email", "Junk E-mail", "Spam", "Bulk Mail", "스팸", "스팸메일함", "정크 메일"],
        [MailFolder.SentItems] = ["Sent", "Sent Items", "Sent Messages", "Sent Mail", "보낸 편지함", "보낸메일함"],
    };

    private static async Task<IMailFolder?> GetTargetFolderAsync(SyncSession s, MailFolder local, CancellationToken ct)
    {
        if (local == MailFolder.Inbox) return s.Client.Inbox;
        if (s.Special.TryGetValue(local, out var cached)) return cached;

        IMailFolder? found = null;
        var special = local switch
        {
            MailFolder.DeletedItems => SpecialFolder.Trash,
            MailFolder.JunkEmail => SpecialFolder.Junk,
            _ => SpecialFolder.Sent,
        };
        if (s.Client.Capabilities.Contains(ImapCapability.SpecialUse) || s.Client.Capabilities.Contains(ImapCapability.XList))
        {
            try { found = s.Client.GetFolder(special); }
            catch (NotSupportedException) { /* 이름으로 찾기 */ }
        }
        if (found == null)
        {
            s.AllFolders ??= await s.Client.GetFoldersAsync(s.Client.PersonalNamespaces[0], cancellationToken: ct);
            var names = FallbackNames[local];
            found = names.Select(n => s.AllFolders.FirstOrDefault(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                         .FirstOrDefault(f => f != null);
        }
        s.Special[local] = found;
        return found;
    }
}

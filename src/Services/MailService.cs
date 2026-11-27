using MailClient.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MailFolder = MailClient.Models.MailFolder;

namespace MailClient.Services;

/// <param name="SyncError">서버 변경 반영 중 오류 (없으면 null)</param>
/// <param name="RemovedCount">서버 INBOX 에서 없어져 로컬 Deleted Items 로 옮긴 메일 수</param>
/// <param name="NewItems">받은 편지함에 새로 들어온 메일 (정크 제외, 알림 표시용)</param>
public sealed record CheckResult(int NewCount, int JunkCount, string? SyncError = null, int RemovedCount = 0, IReadOnlyList<MailItem>? NewItems = null);

/// <summary>POP3/IMAP 수신, SMTP 발신 (서버 변경 반영은 MailService.ServerSync.cs)</summary>
public partial class MailService
{
    private readonly MailStore _store;
    private readonly Func<JunkSettings> _junkSettings;

    public MailService(MailStore store, Func<JunkSettings> junkSettings)
    {
        _store = store;
        _junkSettings = junkSettings;
    }

    // ---------------- 인증 ----------------

    private static async Task AuthenticateAsync(MailKit.IMailService client, MailAccount account, CancellationToken ct)
    {
        var user = string.IsNullOrWhiteSpace(account.UserName) ? account.EmailAddress : account.UserName;
        if (account.AuthMethod == AuthMethod.Password)
        {
            await client.AuthenticateAsync(user, account.Password, ct);
        }
        else
        {
            var token = await OAuthService.GetAccessTokenAsync(account, ct);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(user, token), ct);
        }
    }

    // ---------------- 수신 ----------------

    private readonly HashSet<string> _inProgress = [];

    /// <summary>
    /// 계정의 새 메일을 받습니다. 같은 계정을 이미 확인 중이면 중복 실행하지 않고 즉시 (0, 0)을 반환합니다.
    /// (동시에 실행되면 동기화 상태가 꼬여 같은 메일을 두 번 받을 수 있음)
    /// </summary>
    public async Task<CheckResult> CheckAccountAsync(MailAccount account, CancellationToken ct)
    {
        lock (_inProgress)
        {
            if (!_inProgress.Add(account.Id)) return new CheckResult(0, 0);
        }
        try
        {
            return await CheckAccountCoreAsync(account, ct);
        }
        finally
        {
            lock (_inProgress) _inProgress.Remove(account.Id);
        }
    }

    private async Task<CheckResult> CheckAccountCoreAsync(MailAccount account, CancellationToken ct)
    {
        // 받기 전에 대기 중인 변경을 먼저 서버에 반영 (실패해도 새 메일 받기는 계속)
        string? syncError = null;
        if (account.Protocol == IncomingProtocol.Imap && account.SyncChangesToServer && _store.PendingServerOpCount(account.Id) > 0)
        {
            try
            {
                var s = await SyncChangesAsync(account, ct);
                syncError = s.Error;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error($"server sync {account.EmailAddress}", ex);
                syncError = ex.Message;
            }
        }

        List<MailItem> goneFromServer = [];
        List<(MimeMessage Message, string ServerId, bool Seen)> downloaded;
        if (account.Protocol == IncomingProtocol.Imap)
            (downloaded, goneFromServer) = await FetchImapAsync(account, ct);
        else
            downloaded = await FetchPop3Async(account, ct);

        // 서버 INBOX 에서 없어진 최근 메일 → 로컬 Deleted Items (서버에 다시 보내지 않음, 복원 가능)
        if (goneFromServer.Count > 0)
        {
            _store.Move(goneFromServer, MailFolder.DeletedItems, queueServerOp: false);
            Log.Write($"[{account.EmailAddress}] {goneFromServer.Count} message(s) no longer on server INBOX → Deleted Items");
        }
        uint uidValidity = account.Protocol == IncomingProtocol.Imap ? _store.GetSyncState(account.Id).ImapUidValidity : 0;

        int junk = 0, added = 0;
        var newItems = new List<MailItem>();
        var junkSettings = _junkSettings();
        foreach (var (message, serverId, seen) in downloaded)
        {
            if (_store.ContainsMessageId(account.Id, message.MessageId ?? "")) continue;
            added++;

            var verdict = JunkFilter.Evaluate(message, junkSettings, account);
            var folder = verdict.IsJunk ? MailFolder.JunkEmail : MailFolder.Inbox;
            var item = _store.Add(account.Id, message, folder, serverId, seen, notify: false, uidValidity: uidValidity);
            if (verdict.IsJunk)
            {
                junk++;
                _store.SetJunkFlag(item, true);
                Log.Write($"Junk [{account.EmailAddress}] '{message.Subject}' score={verdict.Score} {verdict.Reason}");
            }
            else newItems.Add(item);
        }
        if (added > 0) _store.NotifyChanged(account.Id);
        return new CheckResult(added - junk, junk, syncError, goneFromServer.Count, newItems);
    }

    /// <summary>INBOX 의 새 메일을 받고, 최근 메일 중 서버에서 없어진 로컬 메일을 찾습니다.</summary>
    private async Task<(List<(MimeMessage Message, string ServerId, bool Seen)> Downloaded, List<MailItem> Gone)> FetchImapAsync(
        MailAccount account, CancellationToken ct)
    {
        var result = new List<(MimeMessage, string, bool)>();
        using var client = new ImapClient();
        await client.ConnectAsync(account.IncomingHost, account.IncomingPort, account.IncomingSecurity, ct);
        await AuthenticateAsync(client, account, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        var state = _store.GetSyncState(account.Id);
        IList<UniqueId> uids;

        if (state.ImapUidValidity == 0 || state.ImapUidValidity != inbox.UidValidity)
        {
            // 최초 동기화 (또는 UIDVALIDITY 변경): 최근 N 개만
            var all = await inbox.SearchAsync(SearchQuery.All, ct);
            int limit = Math.Max(1, account.InitialSyncLimit);
            uids = all.Skip(Math.Max(0, all.Count - limit)).ToList();
            state.ImapUidValidity = inbox.UidValidity;
            state.ImapLastUid = 0;
        }
        else
        {
            var range = new UniqueIdRange(new UniqueId(state.ImapLastUid + 1), UniqueId.MaxValue);
            uids = (await inbox.SearchAsync(SearchQuery.Uids(range), ct))
                .Where(u => u.Id > state.ImapLastUid)
                .ToList();
        }

        if (uids.Count > 0)
        {
            var summaries = await inbox.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct);
            var seen = summaries.ToDictionary(s => s.UniqueId, s => s.Flags?.HasFlag(MessageFlags.Seen) == true);

            foreach (var uid in uids.OrderBy(u => u.Id))
            {
                ct.ThrowIfCancellationRequested();
                var msg = await inbox.GetMessageAsync(uid, ct);
                result.Add((msg, "imap:" + uid.Id, seen.TryGetValue(uid, out var s) && s));
                if (uid.Id > state.ImapLastUid) state.ImapLastUid = uid.Id;
            }
        }

        _store.SaveSyncState(account.Id);

        // 받은 메일은 남기고, 확인 실패는 메일 받기를 막지 않음 (아무것도 옮기지 않음)
        List<MailItem> gone = [];
        try
        {
            gone = await FindGoneFromInboxAsync(account, inbox, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"check deleted on server {account.EmailAddress}", ex);
        }

        await client.DisconnectAsync(true, ct);
        return (result, gone);
    }

    /// <summary>서버 INBOX 에 없는지 확인할 기간 (로컬 메일 날짜 기준)</summary>
    public static readonly TimeSpan ServerDeleteCheckPeriod = TimeSpan.FromDays(7);

    /// <summary>기간과 관계없이 함께 확인할 최근 메일 수 (Inbox 날짜순)</summary>
    public const int ServerDeleteCheckRecentCount = 20;

    /// <summary>
    /// 로컬 Inbox 메일 중 최근 <see cref="ServerDeleteCheckPeriod"/> 동안의 메일과 최근 <see cref="ServerDeleteCheckRecentCount"/>개 메일
    /// (합집합) 가운데 서버 INBOX 에 없는 것을 찾습니다.
    /// <list type="bullet">
    /// <item>서버 INBOX 에서 최근(기간 + 여유 1일)에 도착한 메일의 UID 와 Message-ID 를 한 번에 조회하고,
    ///   그보다 오래된 후보(최근 N개에 포함된 것)는 UID 를 모아 한 번 더 조회해 비교합니다.</item>
    /// <item>없어 보이는 메일만 UID 와 Message-ID 로 각각 다시 검색해 확인합니다 (도착 시각과 메일 날짜가 다른 경우 대비).</item>
    /// <item>서버에 아직 보내지 않은 변경이 있는 메일은 건너뜁니다 (서버 상태가 로컬보다 뒤처져 있으므로).</item>
    /// </list>
    /// </summary>
    private async Task<List<MailItem>> FindGoneFromInboxAsync(MailAccount account, IMailFolder inbox, CancellationToken ct)
    {
        var since = DateTimeOffset.Now - ServerDeleteCheckPeriod;
        var pending = _store.PendingServerOpRefs(account.Id);
        var local = _store.GetServerDeleteCheckCandidates(account.Id, since, ServerDeleteCheckRecentCount)
                          .Where(x => !pending.Contains(x.Item.Id)).ToList();
        if (local.Count == 0) return [];

        // 이전 버전에서 받은 메일은 UIDVALIDITY 가 기록되지 않음 → 계정 동기화 상태 값으로 간주
        uint inboxValidity = _store.GetSyncState(account.Id).ImapUidValidity;
        bool UidUsable(uint uid, uint validity) => uid != 0 && (validity != 0 ? validity : inboxValidity) == inbox.UidValidity;

        // 1) 최근 도착분을 한 번에 조회
        var recent = await inbox.SearchAsync(SearchQuery.DeliveredAfter(since.AddDays(-1).LocalDateTime), ct);
        var serverUids = recent.Select(u => u.Id).ToHashSet();

        // 1-2) 기간 밖의 후보(최근 N개에 포함된 오래된 메일)는 UID 를 모아 한 번에 확인
        var olderUids = local.Where(x => UidUsable(x.Uid, x.UidValidity) && !serverUids.Contains(x.Uid))
                             .Select(x => new UniqueId(inbox.UidValidity, x.Uid)).ToList();
        if (olderUids.Count > 0)
            foreach (var u in await inbox.SearchAsync(SearchQuery.Uids(olderUids), ct)) serverUids.Add(u.Id);
        var serverMessageIds = new HashSet<string>(StringComparer.Ordinal);
        if (recent.Count > 0)
        {
            var request = new FetchRequest(MessageSummaryItems.UniqueId) { Headers = new HeaderSet([HeaderId.MessageId]) };
            foreach (var s in await inbox.FetchAsync(recent, request, ct))
            {
                var mid = s.Headers?[HeaderId.MessageId];
                if (!string.IsNullOrWhiteSpace(mid)) serverMessageIds.Add(MimeKit.Utils.MimeUtils.ParseMessageId(mid) ?? mid.Trim().Trim('<', '>'));
            }
        }

        var gone = new List<MailItem>();
        foreach (var (item, uid, validity) in local)
        {
            ct.ThrowIfCancellationRequested();
            if (UidUsable(uid, validity) && serverUids.Contains(uid)) continue;
            if (item.MessageId.Length > 0 && serverMessageIds.Contains(item.MessageId)) continue;

            // 2) 개별 확인: UID → Message-ID
            if (UidUsable(uid, validity) &&
                (await inbox.SearchAsync(SearchQuery.Uids([new UniqueId(inbox.UidValidity, uid)]), ct)).Count > 0) continue;
            if (item.MessageId.Length > 0 &&
                (await inbox.SearchAsync(SearchQuery.HeaderContains("Message-ID", item.MessageId), ct)).Count > 0) continue;
            // Message-ID 도 없고 UID 도 쓸 수 없으면 판단할 근거가 없으므로 그대로 둠
            if (item.MessageId.Length == 0 && !UidUsable(uid, validity)) continue;

            gone.Add(item);
        }
        return gone;
    }

    private async Task<List<(MimeMessage Message, string ServerId, bool Seen)>> FetchPop3Async(MailAccount account, CancellationToken ct)
    {
        var result = new List<(MimeMessage, string, bool)>();
        using var client = new Pop3Client();
        await client.ConnectAsync(account.IncomingHost, account.IncomingPort, account.IncomingSecurity, ct);
        await AuthenticateAsync(client, account, ct);

        int count = client.Count;
        var state = _store.GetSyncState(account.Id);

        // 메시지별 고유 ID (UIDL 미지원 서버는 Message-Id 헤더 사용)
        var uids = new List<string>(count);
        if (client.Capabilities.Contains(Pop3Capability.UIDL))
        {
            uids.AddRange(await client.GetMessageUidsAsync(ct));
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                var headers = await client.GetMessageHeadersAsync(i, ct);
                uids.Add(headers[HeaderId.MessageId] ?? $"idx-{i}-{headers[HeaderId.Date]}");
            }
        }

        if (!state.Pop3Initialized)
        {
            // 최초 동기화: 최근 N 개만 받고 나머지는 이미 본 것으로 처리
            int limit = Math.Max(1, account.InitialSyncLimit);
            foreach (var u in uids.Take(Math.Max(0, uids.Count - limit)))
                state.Pop3SeenUids.Add(u);
            state.Pop3Initialized = true;
        }

        var toDelete = new List<int>();
        for (int i = 0; i < uids.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (state.Pop3SeenUids.Contains(uids[i])) continue;

            var msg = await client.GetMessageAsync(i, ct);
            result.Add((msg, "pop3:" + uids[i], false));
            state.Pop3SeenUids.Add(uids[i]);
            if (!account.LeaveMessagesOnServer) toDelete.Add(i);
        }

        foreach (var i in toDelete)
            await client.DeleteMessageAsync(i, ct);

        // 서버에 더 이상 없는 UID 정리 (목록 크기 제한)
        var current = new HashSet<string>(uids);
        state.Pop3SeenUids.RemoveWhere(u => !current.Contains(u));
        if (!account.LeaveMessagesOnServer)
            foreach (var i in toDelete) state.Pop3SeenUids.Remove(uids[i]);

        _store.SaveSyncState(account.Id);
        await client.DisconnectAsync(true, ct); // QUIT 시 삭제 확정
        return result;
    }

    // ---------------- 발신 ----------------

    public async Task SendAsync(MailAccount account, MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient();
        await client.ConnectAsync(account.SmtpHost, account.SmtpPort, account.SmtpSecurity, ct);
        await AuthenticateAsync(client, account, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    // ---------------- 연결 테스트 ----------------

    public static async Task<string> TestConnectionAsync(MailAccount account, CancellationToken ct)
    {
        var log = new List<string>();
        if (account.Protocol == IncomingProtocol.Imap)
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync(account.IncomingHost, account.IncomingPort, account.IncomingSecurity, ct);
            await AuthenticateAsync(imap, account, ct);
            await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            log.Add($"IMAP 연결 성공 (Inbox 메일 {imap.Inbox.Count}개)");
            await imap.DisconnectAsync(true, ct);
        }
        else
        {
            using var pop = new Pop3Client();
            await pop.ConnectAsync(account.IncomingHost, account.IncomingPort, account.IncomingSecurity, ct);
            await AuthenticateAsync(pop, account, ct);
            log.Add($"POP3 연결 성공 (서버 메일 {pop.Count}개)");
            await pop.DisconnectAsync(true, ct);
        }

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(account.SmtpHost, account.SmtpPort, account.SmtpSecurity, ct);
        await AuthenticateAsync(smtp, account, ct);
        log.Add("SMTP 연결 및 인증 성공");
        await smtp.DisconnectAsync(true, ct);

        return string.Join(Environment.NewLine, log);
    }
}

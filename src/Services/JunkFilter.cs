using System.Text.RegularExpressions;
using MailClient.Models;
using MimeKit;

namespace MailClient.Services;

public sealed record JunkVerdict(bool IsJunk, int Score, string Reason);

/// <summary>
/// 규칙 기반 스팸(Junk) 판정기.
/// 1) 예외(허용) 주소/도메인 → 항상 정상
/// 2) 차단 주소/도메인 → 항상 스팸
/// 3) 서버 스팸 헤더, 키워드, 제목 패턴, 발신자 위조 의심 등으로 점수를 매겨 임계값 이상이면 스팸
/// </summary>
public static partial class JunkFilter
{
    public static JunkVerdict Evaluate(MimeMessage message, JunkSettings settings, MailAccount? account = null)
    {
        if (!settings.Enabled) return new(false, 0, "필터 꺼짐");

        var from = message.From.Mailboxes.FirstOrDefault();
        var fromAddress = from?.Address?.Trim().ToLowerInvariant() ?? "";
        var fromDomain = DomainOf(fromAddress);

        // 1) 예외 처리 (허용 목록)
        if (fromAddress.Length > 0)
        {
            if (account != null && fromAddress.Equals(account.EmailAddress.Trim(), StringComparison.OrdinalIgnoreCase))
                return new(false, 0, "본인 주소");
            if (settings.SafeSenders.Any(s => s.Trim().Equals(fromAddress, StringComparison.OrdinalIgnoreCase)))
                return new(false, 0, "허용된 발신자");
            if (settings.SafeDomains.Any(d => DomainMatches(fromDomain, d)))
                return new(false, 0, "허용된 도메인");

            // 2) 차단 목록
            if (settings.BlockedSenders.Any(s => s.Trim().Equals(fromAddress, StringComparison.OrdinalIgnoreCase)))
                return new(true, 100, "차단된 발신자");
            if (settings.BlockedDomains.Any(d => DomainMatches(fromDomain, d)))
                return new(true, 100, "차단된 도메인");
        }

        // 3) 휴리스틱 점수
        int score = 0;
        var reasons = new List<string>();
        void Add(int pts, string why) { score += pts; reasons.Add($"{why}(+{pts})"); }

        var spamFlag = message.Headers["X-Spam-Flag"];
        var spamStatus = message.Headers["X-Spam-Status"];
        if (spamFlag?.Trim().StartsWith("YES", StringComparison.OrdinalIgnoreCase) == true ||
            spamStatus?.Trim().StartsWith("Yes", StringComparison.OrdinalIgnoreCase) == true)
            Add(5, "서버 스팸 표시");

        if (from == null || fromAddress.Length == 0 || !fromAddress.Contains('@'))
            Add(3, "발신자 없음/잘못됨");

        var subject = message.Subject ?? "";
        if (string.IsNullOrWhiteSpace(subject))
            Add(1, "제목 없음");

        // 제목 대문자 남용
        var letters = subject.Where(char.IsLetter).ToList();
        if (letters.Count >= 10 && letters.Count(char.IsUpper) > letters.Count * 0.7)
            Add(2, "제목 대문자 남용");
        if (subject.Count(c => c == '!') >= 3 || subject.Contains("$$$"))
            Add(2, "제목 과도한 특수문자");

        // 키워드
        var body = GetPlainText(message);
        int keywordScore = 0;
        foreach (var kw in settings.SpamKeywords.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            var k = kw.Trim();
            if (subject.Contains(k, StringComparison.OrdinalIgnoreCase)) { keywordScore += 3; reasons.Add($"제목 키워드 '{k}'"); }
            else if (body.Contains(k, StringComparison.OrdinalIgnoreCase)) { keywordScore += 1; reasons.Add($"본문 키워드 '{k}'"); }
        }
        if (keywordScore > 0) Add(Math.Min(keywordScore, 8), "키워드");

        // 표시 이름에 다른 이메일 주소가 들어간 경우 (위조 의심)
        if (from != null && !string.IsNullOrEmpty(from.Name))
        {
            var m = EmailRegex().Match(from.Name);
            if (m.Success && !m.Value.Equals(fromAddress, StringComparison.OrdinalIgnoreCase))
                Add(2, "표시 이름 위조 의심");
        }

        // Reply-To 도메인이 다른 경우
        var replyTo = message.ReplyTo.Mailboxes.FirstOrDefault()?.Address;
        if (!string.IsNullOrEmpty(replyTo) && fromDomain.Length > 0 &&
            !DomainOf(replyTo).Equals(fromDomain, StringComparison.OrdinalIgnoreCase))
            Add(1, "Reply-To 도메인 불일치");

        if (string.IsNullOrEmpty(message.MessageId)) Add(1, "Message-Id 없음");
        if (message.Date == DateTimeOffset.MinValue) Add(1, "날짜 없음");

        // 수신자 목록에 본인이 없음 (숨은 참조 대량 발송)
        if (account != null && !string.IsNullOrEmpty(account.EmailAddress))
        {
            var me = account.EmailAddress.Trim();
            var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes);
            if (!recipients.Any(r => r.Address.Equals(me, StringComparison.OrdinalIgnoreCase)))
                Add(1, "수신자에 본인 없음");
        }

        // HTML 전용 + 링크 과다
        if (message.TextBody == null && message.HtmlBody != null)
        {
            var links = LinkRegex().Matches(message.HtmlBody).Count;
            if (links > 15) Add(2, "링크 과다");
        }

        bool junk = score >= Math.Max(1, settings.Threshold);
        return new(junk, score, string.Join(", ", reasons));
    }

    public static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 ? address[(at + 1)..].Trim().ToLowerInvariant() : "";
    }

    /// <summary>"company.com" 은 company.com 및 mail.company.com 에 일치</summary>
    public static bool DomainMatches(string domain, string rule)
    {
        rule = rule.Trim().TrimStart('@', '*', '.').ToLowerInvariant();
        if (rule.Length == 0 || domain.Length == 0) return false;
        return domain == rule || domain.EndsWith("." + rule, StringComparison.Ordinal);
    }

    public static string GetPlainText(MimeMessage message)
    {
        if (message.TextBody != null) return message.TextBody;
        if (message.HtmlBody != null) return HtmlUtil.HtmlToText(message.HtmlBody);
        return "";
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"<a\s", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();
}

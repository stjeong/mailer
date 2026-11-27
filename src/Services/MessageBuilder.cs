using System.Text;
using MailClient.Models;
using MimeKit;

namespace MailClient.Services;

/// <summary>메일 쓰기 창에 전달하는 초안</summary>
public class ComposeDraft
{
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Bcc { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string? InReplyTo { get; set; }
    public List<string> References { get; set; } = [];
    /// <summary>전달 시 원본 첨부파일</summary>
    public List<MimeEntity> ForwardedAttachments { get; set; } = [];
}

public static class MessageBuilder
{
    public static ComposeDraft CreateReply(MimeMessage original, MailAccount account, bool replyAll)
    {
        var draft = new ComposeDraft
        {
            Subject = Prefix(original.Subject, "RE: ")
        };

        var replyTo = original.ReplyTo.Mailboxes.Any() ? original.ReplyTo.Mailboxes : original.From.Mailboxes;
        var me = account.EmailAddress.Trim();
        var to = replyTo.Select(m => m).ToList();

        if (replyAll)
        {
            to.AddRange(original.To.Mailboxes);
            var cc = original.Cc.Mailboxes
                .Where(m => !m.Address.Equals(me, StringComparison.OrdinalIgnoreCase))
                .ToList();
            draft.Cc = Join(cc);
        }

        // 자기 자신 제거 (단, 받는 사람이 본인 하나뿐이면 유지)
        var filtered = to.Where(m => !m.Address.Equals(me, StringComparison.OrdinalIgnoreCase)).ToList();
        if (filtered.Count > 0) to = filtered;
        draft.To = Join(to.DistinctBy(m => m.Address.ToLowerInvariant()));

        if (!string.IsNullOrEmpty(original.MessageId))
        {
            draft.InReplyTo = original.MessageId;
            draft.References.AddRange(original.References);
            draft.References.Add(original.MessageId);
        }

        draft.Body = Environment.NewLine + Environment.NewLine + QuoteHeader(original) + Quote(JunkFilter.GetPlainText(original));
        return draft;
    }

    public static ComposeDraft CreateForward(MimeMessage original)
    {
        var draft = new ComposeDraft
        {
            Subject = Prefix(original.Subject, "FW: ")
        };

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("---------- 전달된 메시지 ----------");
        sb.AppendLine($"보낸 사람: {MailStore.FormatAddresses(original.From)}");
        sb.AppendLine($"날짜: {original.Date.LocalDateTime:yyyy-MM-dd (ddd) HH:mm}");
        sb.AppendLine($"제목: {original.Subject}");
        sb.AppendLine($"받는 사람: {MailStore.FormatAddresses(original.To)}");
        if (original.Cc.Count > 0) sb.AppendLine($"참조: {MailStore.FormatAddresses(original.Cc)}");
        sb.AppendLine();
        sb.Append(JunkFilter.GetPlainText(original));
        draft.Body = sb.ToString();

        draft.ForwardedAttachments.AddRange(original.Attachments);
        return draft;
    }

    public static MimeMessage Build(MailAccount account, ComposeDraft draft, IEnumerable<string> attachmentFiles)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(account.SenderName) ? account.EmailAddress : account.SenderName,
            account.EmailAddress));
        message.To.AddRange(ParseAddresses(draft.To));
        message.Cc.AddRange(ParseAddresses(draft.Cc));
        message.Bcc.AddRange(ParseAddresses(draft.Bcc));
        message.Subject = draft.Subject;
        message.Date = DateTimeOffset.Now;

        if (!string.IsNullOrEmpty(draft.InReplyTo))
        {
            message.InReplyTo = draft.InReplyTo;
            foreach (var r in draft.References.Distinct()) message.References.Add(r);
        }

        var builder = new BodyBuilder { TextBody = draft.Body };
        foreach (var file in attachmentFiles)
            builder.Attachments.Add(file);
        foreach (var entity in draft.ForwardedAttachments)
            builder.Attachments.Add(entity);

        message.Body = builder.ToMessageBody();
        return message;
    }

    // ---------- 작성 중인 메일 (Draft) ----------

    // 받는 사람 칸은 입력 중이라 주소 형식이 아닐 수 있으므로, 입력한 그대로를 이 머리글에 따로 보관
    private const string DraftToHeader = "X-MailClient-Draft-To";
    private const string DraftCcHeader = "X-MailClient-Draft-Cc";
    private const string DraftBccHeader = "X-MailClient-Draft-Bcc";
    // 메일 형식으로 저장하면 본문 끝에 줄바꿈이 붙으므로, 다시 열 때 원래 길이로 자르기 위해 보관 (열 때마다 빈 줄이 늘지 않게)
    private const string DraftBodyLengthHeader = "X-MailClient-Draft-Body-Length";

    /// <summary>
    /// 작성 중인 메일을 저장용 메시지로 만듭니다. 보내기와 달리 주소가 잘못돼도 실패하지 않습니다
    /// (올바른 주소만 받는 사람 머리글에 넣고, 입력한 글자는 그대로 따로 보관). 없어진 첨부 파일은 건너뜁니다.
    /// </summary>
    public static MimeMessage BuildDraft(MailAccount account, ComposeDraft draft, IEnumerable<string> attachmentFiles)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(account.SenderName) ? account.EmailAddress : account.SenderName,
            account.EmailAddress));
        message.To.AddRange(TryParseAddresses(draft.To));
        message.Cc.AddRange(TryParseAddresses(draft.Cc));
        message.Bcc.AddRange(TryParseAddresses(draft.Bcc));
        message.Headers.Add(DraftToHeader, Encoding.UTF8, draft.To);
        message.Headers.Add(DraftCcHeader, Encoding.UTF8, draft.Cc);
        message.Headers.Add(DraftBccHeader, Encoding.UTF8, draft.Bcc);
        message.Headers.Add(DraftBodyLengthHeader, draft.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        message.Subject = draft.Subject;
        message.Date = DateTimeOffset.Now;
        if (!string.IsNullOrEmpty(draft.InReplyTo))
        {
            message.InReplyTo = draft.InReplyTo;
            foreach (var r in draft.References.Distinct()) message.References.Add(r);
        }

        var builder = new BodyBuilder { TextBody = draft.Body };
        foreach (var file in attachmentFiles.Where(File.Exists))
            builder.Attachments.Add(file);
        foreach (var entity in draft.ForwardedAttachments)
            builder.Attachments.Add(entity);
        message.Body = builder.ToMessageBody();
        return message;
    }

    /// <summary>저장한 작성 중인 메일을 다시 메일 쓰기 창에 넣을 수 있게 읽음 (첨부 파일은 메일 안의 사본)</summary>
    public static ComposeDraft ReadDraft(MimeMessage message)
    {
        string Field(string header, InternetAddressList list) =>
            message.Headers.Contains(header) ? message.Headers[header] ?? "" : string.Join("; ", list.Mailboxes.Select(m => m.ToString()));
        var draft = new ComposeDraft
        {
            To = Field(DraftToHeader, message.To),
            Cc = Field(DraftCcHeader, message.Cc),
            Bcc = Field(DraftBccHeader, message.Bcc),
            Subject = message.Subject ?? "",
            Body = DraftBody(message),
            InReplyTo = message.InReplyTo,
            References = message.References.ToList(),
        };
        draft.ForwardedAttachments.AddRange(message.Attachments);
        return draft;
    }

    private static string DraftBody(MimeMessage message)
    {
        var body = message.TextBody ?? "";
        if (int.TryParse(message.Headers[DraftBodyLengthHeader], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var length) && length < body.Length)
        {
            var extra = body[length..];
            if (extra.Trim('\r', '\n').Length == 0) body = body[..length];   // 저장할 때 붙은 줄바꿈만 제거
        }
        return body;
    }

    private static InternetAddressList TryParseAddresses(string text)
    {
        var result = new InternetAddressList();
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { result.AddRange(ParseAddresses(part)); }
            catch (FormatException) { /* 입력 중인 주소: 머리글에는 넣지 않음 */ }
        }
        return result;
    }

    /// <summary>"a@b.com; 홍길동 &lt;c@d.com&gt;" 형태 파싱. 실패 시 FormatException</summary>
    public static InternetAddressList ParseAddresses(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new InternetAddressList();
        var normalized = text.Replace(';', ',').Trim().Trim(',');
        if (!InternetAddressList.TryParse(normalized, out var list))
            throw new FormatException($"잘못된 이메일 주소: {text}");
        foreach (var mb in list.Mailboxes)
        {
            if (!mb.Address.Contains('@') || mb.Address.StartsWith('@') || mb.Address.EndsWith('@'))
                throw new FormatException($"잘못된 이메일 주소: {mb}");
        }
        return list;
    }

    private static string Prefix(string? subject, string prefix)
    {
        subject ??= "";
        var p = prefix.Trim();
        if (subject.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return subject;
        if (p == "RE:" && subject.StartsWith("답장:", StringComparison.Ordinal)) return subject;
        if (p == "FW:" && (subject.StartsWith("FWD:", StringComparison.OrdinalIgnoreCase) || subject.StartsWith("전달:"))) return subject;
        return prefix + subject;
    }

    private static string QuoteHeader(MimeMessage m) =>
        $"{m.Date.LocalDateTime:yyyy-MM-dd HH:mm}, {MailStore.FormatAddresses(m.From)} 작성:" + Environment.NewLine;

    private static string Quote(string text) =>
        string.Join(Environment.NewLine, text.Replace("\r\n", "\n").Split('\n').Select(l => "> " + l));

    private static string Join(IEnumerable<MailboxAddress> list) =>
        string.Join("; ", list.Select(m => m.ToString()));
}

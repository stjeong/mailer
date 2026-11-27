using MailClient.Models;
using MimeKit;

namespace MailClient.Services;

/// <summary>받은 메일 규칙 적용 (보낸 사람이 일치하면 폴더에 .eml 저장)</summary>
public static class MailRules
{
    /// <summary>"이름 &lt;주소&gt;" 형식도 받아 주소만 돌려줍니다. 이메일 주소가 아니면 null.</summary>
    public static string? NormalizeAddress(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!MailboxAddress.TryParse(text, out var mailbox)) return null;
        var address = mailbox.Address.Trim();
        int at = address.IndexOf('@');
        return at > 0 && at < address.Length - 1 ? address : null;
    }

    /// <summary>폴더 설정 값 → 실제 절대 경로 (환경 변수 확장). 절대 경로가 아니면 null.</summary>
    public static string? ResolveDirectory(string? configured)
    {
        var text = configured?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(text);
        return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : null;
    }

    public static bool IsComplete(MailRule rule) =>
        NormalizeAddress(rule.Sender) != null && ResolveDirectory(rule.SaveDirectory) != null;

    public static bool Matches(MailRule rule, MimeMessage message)
    {
        if (!rule.Enabled) return false;
        var sender = NormalizeAddress(rule.Sender);
        if (sender == null || ResolveDirectory(rule.SaveDirectory) == null) return false;
        return message.From.Mailboxes.Any(m => string.Equals(m.Address?.Trim(), sender, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 메일을 규칙의 폴더에 .eml 로 저장하고 파일 경로를 돌려줍니다.
    /// 파일 이름: "받은 날짜 시각 제목.eml" (같은 이름이 있으면 " (2)" 등을 붙임)
    /// </summary>
    public static string SaveEml(MailRule rule, MimeMessage message)
    {
        var dir = ResolveDirectory(rule.SaveDirectory) ?? throw new InvalidOperationException("저장할 폴더가 올바르지 않습니다.");
        Directory.CreateDirectory(dir);

        var date = message.Date == DateTimeOffset.MinValue ? DateTime.Now : message.Date.LocalDateTime;
        var subject = string.IsNullOrWhiteSpace(message.Subject) ? "(제목 없음)" : message.Subject.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) subject = subject.Replace(c, '_');
        subject = subject.Replace("\r", "").Replace("\n", " ").TrimEnd('.', ' ');
        if (subject.Length > 80) subject = subject[..80].TrimEnd('.', ' ');
        var baseName = $"{date:yyyy-MM-dd HHmmss} {subject}";

        var path = Path.Combine(dir, baseName + ".eml");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(dir, $"{baseName} ({n}).eml");

        // 중간에 실패해서 반쯤 쓴 파일이 남지 않도록 임시 파일에 쓴 뒤 이름 변경
        var temp = path + ".tmp";
        try
        {
            message.WriteTo(temp);
            File.Move(temp, path);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
        return path;
    }
}

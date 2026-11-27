using MailClient.Models;
using MailKit.Security;

namespace MailClient.Services;

public sealed record ProviderPreset(
    string Name,
    IncomingProtocol Protocol,
    string IncomingHost,
    int IncomingPort,
    SecureSocketOptions IncomingSecurity,
    string SmtpHost,
    int SmtpPort,
    SecureSocketOptions SmtpSecurity,
    AuthMethod DefaultAuth,
    string Note)
{
    public override string ToString() => Name;
}

public static class ProviderPresets
{
    public static readonly ProviderPreset[] All =
    [
        new("Gmail (IMAP)", IncomingProtocol.Imap, "imap.gmail.com", 993, SecureSocketOptions.SslOnConnect,
            "smtp.gmail.com", 465, SecureSocketOptions.SslOnConnect, AuthMethod.Password,
            "Gmail: 2단계 인증 후 발급한 '앱 비밀번호'를 쓰거나 OAuth(Google)를 선택하세요."),
        new("Gmail (POP3)", IncomingProtocol.Pop3, "pop.gmail.com", 995, SecureSocketOptions.SslOnConnect,
            "smtp.gmail.com", 465, SecureSocketOptions.SslOnConnect, AuthMethod.Password,
            "Gmail 설정 > 전달 및 POP/IMAP 에서 POP를 사용으로 설정하세요. 앱 비밀번호 또는 OAuth(Google) 사용."),
        new("Outlook.com / Hotmail (IMAP)", IncomingProtocol.Imap, "outlook.office365.com", 993, SecureSocketOptions.SslOnConnect,
            "smtp-mail.outlook.com", 587, SecureSocketOptions.StartTls, AuthMethod.OAuthMicrosoft,
            "Microsoft는 비밀번호(기본 인증)를 차단하므로 OAuth(Microsoft)가 필요합니다. [OAuth 로그인...]을 눌러 로그인하세요."),
        new("Outlook.com / Hotmail (POP3)", IncomingProtocol.Pop3, "outlook.office365.com", 995, SecureSocketOptions.SslOnConnect,
            "smtp-mail.outlook.com", 587, SecureSocketOptions.StartTls, AuthMethod.OAuthMicrosoft,
            "Outlook.com 설정에서 POP 사용을 켜고 OAuth(Microsoft)로 로그인하세요."),
        new("Microsoft 365 (IMAP)", IncomingProtocol.Imap, "outlook.office365.com", 993, SecureSocketOptions.SslOnConnect,
            "smtp.office365.com", 587, SecureSocketOptions.StartTls, AuthMethod.OAuthMicrosoft,
            "회사/학교 계정. 관리자가 IMAP 및 SMTP AUTH를 허용해야 합니다."),
        new("Naver (IMAP)", IncomingProtocol.Imap, "imap.naver.com", 993, SecureSocketOptions.SslOnConnect,
            "smtp.naver.com", 465, SecureSocketOptions.SslOnConnect, AuthMethod.Password,
            "네이버 메일 환경설정 > POP3/IMAP 설정에서 사용함으로 설정하세요. 2단계 인증 시 애플리케이션 비밀번호 사용."),
        new("Naver (POP3)", IncomingProtocol.Pop3, "pop.naver.com", 995, SecureSocketOptions.SslOnConnect,
            "smtp.naver.com", 465, SecureSocketOptions.SslOnConnect, AuthMethod.Password,
            "네이버 메일 환경설정 > POP3/IMAP 설정에서 POP3를 사용함으로 설정하세요."),
        new("Daum (IMAP)", IncomingProtocol.Imap, "imap.daum.net", 993, SecureSocketOptions.SslOnConnect,
            "smtp.daum.net", 465, SecureSocketOptions.SslOnConnect, AuthMethod.Password, ""),
        new("Daum (POP3)", IncomingProtocol.Pop3, "pop.daum.net", 995, SecureSocketOptions.SslOnConnect,
            "smtp.daum.net", 465, SecureSocketOptions.SslOnConnect, AuthMethod.Password, ""),
        new("직접 설정 (IMAP)", IncomingProtocol.Imap, "", 993, SecureSocketOptions.SslOnConnect,
            "", 587, SecureSocketOptions.StartTls, AuthMethod.Password, ""),
        new("직접 설정 (POP3)", IncomingProtocol.Pop3, "", 995, SecureSocketOptions.SslOnConnect,
            "", 587, SecureSocketOptions.StartTls, AuthMethod.Password, ""),
    ];

    /// <summary>이메일 도메인으로 프리셋 추측</summary>
    public static ProviderPreset? Guess(string email, IncomingProtocol protocol)
    {
        var at = email.LastIndexOf('@');
        if (at < 0) return null;
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        string? prefix = domain switch
        {
            "gmail.com" or "googlemail.com" => "Gmail",
            "outlook.com" or "hotmail.com" or "live.com" or "msn.com" or "outlook.kr" or "hotmail.co.kr" => "Outlook.com",
            "naver.com" => "Naver",
            "daum.net" or "hanmail.net" => "Daum",
            _ => null
        };
        if (prefix == null) return null;
        var suffix = protocol == IncomingProtocol.Imap ? "(IMAP)" : "(POP3)";
        return All.FirstOrDefault(p => p.Name.StartsWith(prefix) && p.Name.EndsWith(suffix));
    }
}

using System.Text.Json.Serialization;
using MailKit.Security;

namespace MailClient.Models;

public enum IncomingProtocol
{
    Imap,
    Pop3
}

public enum AuthMethod
{
    /// <summary>일반 비밀번호 또는 앱 비밀번호</summary>
    Password,
    /// <summary>Google OAuth 2.0</summary>
    OAuthGoogle,
    /// <summary>Microsoft (Outlook.com / Microsoft 365) OAuth 2.0</summary>
    OAuthMicrosoft
}

public class MailAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string EmailAddress { get; set; } = "";
    public string SenderName { get; set; } = "";

    public IncomingProtocol Protocol { get; set; } = IncomingProtocol.Imap;
    public string IncomingHost { get; set; } = "";
    public int IncomingPort { get; set; } = 993;
    public SecureSocketOptions IncomingSecurity { get; set; } = SecureSocketOptions.SslOnConnect;

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public SecureSocketOptions SmtpSecurity { get; set; } = SecureSocketOptions.StartTls;

    public string UserName { get; set; } = "";
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;

    /// <summary>DPAPI(CurrentUser)로 암호화된 비밀번호 (Base64)</summary>
    public string EncryptedPassword { get; set; } = "";

    /// <summary>OAuth 클라이언트 ID / Secret: 사용자가 직접 등록한 앱을 쓸 때만 채움 (비어 있으면 앱 기본값, OAuthClients 참고)</summary>
    public string OAuthClientId { get; set; } = "";
    /// <summary>현재 Refresh Token 을 발급받은 클라이언트 ID (토큰 갱신에 같은 클라이언트를 써야 함)</summary>
    public string OAuthTokenClientId { get; set; } = "";
    public string EncryptedOAuthClientSecret { get; set; } = "";
    /// <summary>DPAPI로 암호화된 OAuth Refresh Token</summary>
    public string EncryptedRefreshToken { get; set; } = "";

    /// <summary>POP3: 서버에 메일 사본 남기기</summary>
    public bool LeaveMessagesOnServer { get; set; } = true;

    /// <summary>IMAP: 이 프로그램에서 한 이동·삭제·읽음 표시를 서버에도 반영.
    /// 기존 계정은 꺼진 상태로 유지되도록 기본값 false (새 계정은 계정 창에서 기본으로 켬)</summary>
    public bool SyncChangesToServer { get; set; }

    /// <summary>최초 동기화 시 가져올 최근 메일 최대 개수</summary>
    public int InitialSyncLimit { get; set; } = 100;

    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string Password
    {
        get => Services.Crypto.Unprotect(EncryptedPassword);
        set => EncryptedPassword = Services.Crypto.Protect(value);
    }

    [JsonIgnore]
    public string OAuthClientSecret
    {
        get => Services.Crypto.Unprotect(EncryptedOAuthClientSecret);
        set => EncryptedOAuthClientSecret = Services.Crypto.Protect(value);
    }

    [JsonIgnore]
    public string RefreshToken
    {
        get => Services.Crypto.Unprotect(EncryptedRefreshToken);
        set => EncryptedRefreshToken = Services.Crypto.Protect(value);
    }

    public override string ToString() =>
        string.IsNullOrWhiteSpace(DisplayName) ? EmailAddress : DisplayName;
}

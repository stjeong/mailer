using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailClient.Models;
using MailKit.Security;

namespace MailClient.Services;

/// <summary>
/// 계정 목록 내보내기(JSON).
/// 서버 설정 등 일반 항목은 평문으로, 비밀 값(비밀번호, OAuth 클라이언트 비밀, Refresh Token)은
/// 사용자가 입력한 내보내기 암호로 암호화해 저장합니다.
///  - 키 유도: PBKDF2-HMAC-SHA256 (파일마다 임의 Salt, 600,000회 반복) → 256비트 키
///  - 암호화: AES-256-GCM (계정마다 임의 Nonce, 계정 ID 를 추가 인증 데이터로 사용해 다른 계정과 바꿔치기 방지)
/// DPAPI 로 암호화된 설정 파일 값은 다른 PC/사용자에서 복호화할 수 없으므로 파일에 넣지 않습니다.
/// 버전 2 부터 "도구 > 설정" 내용(<see cref="ExportedSettings"/>)을 선택적으로 포함합니다 (비밀 값이 없어 평문).
/// </summary>
public static class AccountExport
{
    public const string FormatName = "MailClient.AccountList";
    /// <summary>1: 계정만, 2: 계정 + 선택적 설정</summary>
    public const int CurrentVersion = 2;
    public const int MinPasswordLength = 8;
    private const int Iterations = 600_000;
    private const int SaltSize = 16, KeySize = 32, NonceSize = 12, TagSize = 16;

    // ---------- 파일 형식 ----------

    public sealed class ExportFile
    {
        public string Format { get; set; } = "";   // 내보낼 때 설정 (필드가 없는 파일은 거부)
        public int Version { get; set; }
        public DateTimeOffset ExportedAt { get; set; }
        public KdfInfo Encryption { get; set; } = new();
        public List<ExportedAccount> Accounts { get; set; } = [];
        /// <summary>도구 > 설정 내용 (포함하지 않았으면 null)</summary>
        public ExportedSettings? Settings { get; set; }
    }

    /// <summary>"도구 > 설정" 창의 내용 (창 크기 등 화면 상태와 계정은 제외)</summary>
    public sealed class ExportedSettings
    {
        public int CheckIntervalMinutes { get; set; } = 5;
        public bool CheckOnStartup { get; set; } = true;
        public bool ShowNotification { get; set; } = true;
        public bool ConfirmPermanentDelete { get; set; } = true;
        public bool HtmlLeftAlign { get; set; } = true;
        public JunkSettings Junk { get; set; } = new();

        public static ExportedSettings From(AppSettings s) => new()
        {
            CheckIntervalMinutes = s.CheckIntervalMinutes,
            CheckOnStartup = s.CheckOnStartup,
            ShowNotification = s.ShowNotification,
            ConfirmPermanentDelete = s.ConfirmPermanentDelete,
            HtmlLeftAlign = s.HtmlLeftAlign,
            Junk = CloneJunk(s.Junk)
        };

        public void ApplyTo(AppSettings s)
        {
            s.CheckIntervalMinutes = Math.Clamp(CheckIntervalMinutes, 1, 1440);
            s.CheckOnStartup = CheckOnStartup;
            s.ShowNotification = ShowNotification;
            s.ConfirmPermanentDelete = ConfirmPermanentDelete;
            s.HtmlLeftAlign = HtmlLeftAlign;
            var j = CloneJunk(Junk ?? new JunkSettings());
            j.Threshold = Math.Clamp(j.Threshold, 1, 50);
            j.SafeSenders ??= []; j.SafeDomains ??= []; j.BlockedSenders ??= []; j.BlockedDomains ??= []; j.SpamKeywords ??= [];
            s.Junk = j;
        }

        private static JunkSettings CloneJunk(JunkSettings j) =>
            JsonSerializer.Deserialize<JunkSettings>(JsonSerializer.Serialize(j, SettingsService.JsonOptions), SettingsService.JsonOptions)!;
    }

    /// <summary>가져오기 결과: 선택한 계정(비밀 값 복호화됨)과 설정(선택한 경우)</summary>
    public sealed record ImportResult(List<MailAccount> Accounts, ExportedSettings? Settings);

    public sealed class KdfInfo
    {
        public string Kdf { get; set; } = "PBKDF2-HMAC-SHA256";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Cipher { get; set; } = "AES-256-GCM";
    }

    public sealed class ExportedAccount
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string EmailAddress { get; set; } = "";
        public string SenderName { get; set; } = "";
        public IncomingProtocol Protocol { get; set; }
        public string IncomingHost { get; set; } = "";
        public int IncomingPort { get; set; }
        public SecureSocketOptions IncomingSecurity { get; set; }
        public string SmtpHost { get; set; } = "";
        public int SmtpPort { get; set; }
        public SecureSocketOptions SmtpSecurity { get; set; }
        public string UserName { get; set; } = "";
        public AuthMethod AuthMethod { get; set; }
        /// <summary>직접 등록한 클라이언트 ID 만 (앱 기본값을 쓰는 계정은 빈 값)</summary>
        public string OAuthClientId { get; set; } = "";
        /// <summary>함께 내보내는 Refresh Token 을 발급받은 클라이언트 ID (비밀 아님)</summary>
        public string OAuthTokenClientId { get; set; } = "";
        public bool LeaveMessagesOnServer { get; set; }
        public bool SyncChangesToServer { get; set; }   // 이전 파일에는 없음 → false
        public int InitialSyncLimit { get; set; }
        public bool Enabled { get; set; }
        /// <summary>암호화된 비밀 값 (없으면 null)</summary>
        public SealedBox? Secrets { get; set; }
    }

    public sealed class SealedBox
    {
        public string Nonce { get; set; } = "";
        public string Ciphertext { get; set; } = "";
        public string Tag { get; set; } = "";
    }

    private sealed class SecretValues
    {
        public string Password { get; set; } = "";
        public string OAuthClientSecret { get; set; } = "";
        public string RefreshToken { get; set; } = "";
    }

    // ---------- 내보내기 ----------

    /// <param name="accounts">내보낼 계정 (비어 있어도 됨)</param>
    /// <param name="settings">함께 내보낼 설정 (null 이면 제외)</param>
    /// <param name="password">비밀 값 암호화용 암호. 계정이 하나라도 있으면 필수 (8자 이상)</param>
    public static void Export(IEnumerable<MailAccount> accounts, ExportedSettings? settings, string? password, string path)
    {
        var list = accounts.ToList();
        if (list.Count == 0 && settings == null)
            throw new ArgumentException("내보낼 계정이나 설정을 선택하세요.");
        if (list.Count > 0 && (password == null || password.Length < MinPasswordLength))
            throw new ArgumentException($"암호는 {MinPasswordLength}자 이상이어야 합니다.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = list.Count > 0 ? DeriveKey(password!, salt, Iterations) : [];
        try
        {
            var file = new ExportFile
            {
                Format = FormatName,
                Version = CurrentVersion,
                ExportedAt = DateTimeOffset.Now,
                Encryption = new KdfInfo { Iterations = Iterations, Salt = Convert.ToBase64String(salt) },
                Settings = settings
            };

            foreach (var a in list)
            {
                var secrets = new SecretValues
                {
                    Password = a.Password,
                    OAuthClientSecret = a.OAuthClientSecret,
                    RefreshToken = a.RefreshToken
                };
                bool hasSecret = secrets.Password.Length + secrets.OAuthClientSecret.Length + secrets.RefreshToken.Length > 0;

                file.Accounts.Add(new ExportedAccount
                {
                    Id = a.Id,
                    DisplayName = a.DisplayName,
                    EmailAddress = a.EmailAddress,
                    SenderName = a.SenderName,
                    Protocol = a.Protocol,
                    IncomingHost = a.IncomingHost,
                    IncomingPort = a.IncomingPort,
                    IncomingSecurity = a.IncomingSecurity,
                    SmtpHost = a.SmtpHost,
                    SmtpPort = a.SmtpPort,
                    SmtpSecurity = a.SmtpSecurity,
                    UserName = a.UserName,
                    AuthMethod = a.AuthMethod,
                    OAuthClientId = a.OAuthClientId,
                    OAuthTokenClientId = a.OAuthTokenClientId,
                    LeaveMessagesOnServer = a.LeaveMessagesOnServer,
                    SyncChangesToServer = a.SyncChangesToServer,
                    InitialSyncLimit = a.InitialSyncLimit,
                    Enabled = a.Enabled,
                    Secrets = hasSecret ? Seal(key, a.Id, JsonSerializer.SerializeToUtf8Bytes(secrets)) : null
                });
            }

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, SettingsService.JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // ---------- 가져오기 (복호화) ----------

    /// <summary>파일 형식만 확인하고 내용(비밀 값은 암호화된 상태 그대로)을 읽습니다. 암호 불필요.</summary>
    public static ExportFile Peek(string path)
    {
        ExportFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ExportFile>(File.ReadAllText(path), SettingsService.JsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("MailClient 계정 목록 파일이 아닙니다.");
        }
        if (file == null || file.Format != FormatName) throw new InvalidDataException("MailClient 계정 목록 파일이 아닙니다.");
        if (file.Version < 1 || file.Encryption is null || file.Accounts is null)
            throw new InvalidDataException("계정 목록 파일 형식이 올바르지 않습니다.");
        if (file.Version > CurrentVersion) throw new InvalidDataException("이 프로그램보다 새 버전에서 만든 파일입니다.");
        return file;
    }

    /// <summary>선택한 계정 중 암호화된 비밀 값이 있는 계정이 있는지 (= 가져올 때 암호가 필요한지)</summary>
    public static bool NeedsPassword(ExportFile file, IEnumerable<int> accountIndices) =>
        accountIndices.Any(i => file.Accounts[i].Secrets != null);

    /// <summary>
    /// 내보낸 파일에서 선택한 계정(파일 내 순번)과 설정을 읽어 옵니다.
    /// 선택한 계정에 비밀 값이 있으면 <paramref name="password"/> 가 필요하며, 틀리면 <see cref="CryptographicException"/>.
    /// </summary>
    /// <param name="accountIndices">가져올 계정의 파일 내 순번 (null 이면 전체)</param>
    /// <param name="includeSettings">파일의 설정을 함께 가져올지</param>
    public static ImportResult Import(string path, string? password, IReadOnlyCollection<int>? accountIndices = null, bool includeSettings = true)
    {
        var file = Peek(path);
        var indices = (accountIndices ?? Enumerable.Range(0, file.Accounts.Count).ToList())
            .Where(i => i >= 0 && i < file.Accounts.Count).Distinct().OrderBy(i => i).ToList();

        bool needKey = NeedsPassword(file, indices);
        if (needKey && string.IsNullOrEmpty(password))
            throw new ArgumentException("선택한 계정의 비밀 값을 복원하려면 암호가 필요합니다.", nameof(password));

        var key = needKey ? DeriveKey(password!, Convert.FromBase64String(file.Encryption.Salt), file.Encryption.Iterations) : [];
        try
        {
            var result = new List<MailAccount>();
            foreach (var e in indices.Select(i => file.Accounts[i]))
            {
                var a = new MailAccount
                {
                    Id = e.Id,
                    DisplayName = e.DisplayName,
                    EmailAddress = e.EmailAddress,
                    SenderName = e.SenderName,
                    Protocol = e.Protocol,
                    IncomingHost = e.IncomingHost,
                    IncomingPort = e.IncomingPort,
                    IncomingSecurity = e.IncomingSecurity,
                    SmtpHost = e.SmtpHost,
                    SmtpPort = e.SmtpPort,
                    SmtpSecurity = e.SmtpSecurity,
                    UserName = e.UserName,
                    AuthMethod = e.AuthMethod,
                    OAuthClientId = e.OAuthClientId,
                    OAuthTokenClientId = e.OAuthTokenClientId,
                    LeaveMessagesOnServer = e.LeaveMessagesOnServer,
                    SyncChangesToServer = e.SyncChangesToServer,
                    InitialSyncLimit = e.InitialSyncLimit,
                    Enabled = e.Enabled
                };
                if (e.Secrets != null)
                {
                    byte[] plain;
                    try
                    {
                        plain = Open(key, e.Id, e.Secrets);
                    }
                    catch (CryptographicException)
                    {
                        throw new CryptographicException("암호가 올바르지 않거나 파일이 손상되었습니다.");
                    }
                    var s = JsonSerializer.Deserialize<SecretValues>(plain)!;
                    CryptographicOperations.ZeroMemory(plain);
                    a.Password = s.Password;
                    a.OAuthClientSecret = s.OAuthClientSecret;
                    a.RefreshToken = s.RefreshToken;
                }
                result.Add(a);
            }
            OAuthClients.Normalize(result);   // 이전 형식 파일: 기본값과 같은 클라이언트 ID 정리, 토큰 클라이언트 기록
            return new ImportResult(result, includeSettings ? file.Settings : null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>가져오기 계획: 새로 추가할 계정과 이미 있는 계정(충돌)</summary>
    public sealed record ImportPlan(List<MailAccount> NewAccounts, List<(MailAccount Imported, MailAccount Existing)> Conflicts);

    /// <summary>
    /// 가져온 계정을 기존 목록과 비교합니다. 같은 ID 가 우선이고, 없으면 이메일 주소(대소문자 무시)로 같은 계정을 찾습니다.
    /// 파일 안에서 같은 계정이 중복되면 처음 것만 사용합니다.
    /// </summary>
    public static ImportPlan PlanImport(IReadOnlyList<MailAccount> existing, IEnumerable<MailAccount> imported)
    {
        var newOnes = new List<MailAccount>();
        var conflicts = new List<(MailAccount, MailAccount)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in imported)
        {
            if (!seen.Add(a.Id) | !seen.Add("mail:" + a.EmailAddress)) continue;   // 파일 내 중복
            var match = existing.FirstOrDefault(x => x.Id == a.Id)
                        ?? existing.FirstOrDefault(x => x.EmailAddress.Equals(a.EmailAddress, StringComparison.OrdinalIgnoreCase));
            if (match != null) conflicts.Add((a, match));
            else newOnes.Add(a);
        }
        return new ImportPlan(newOnes, conflicts);
    }

    // ---------- 암호 기본 요소 ----------

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);

    private static SealedBox Seal(byte[] key, string accountId, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(accountId));
        CryptographicOperations.ZeroMemory(plaintext);
        return new SealedBox
        {
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(cipher),
            Tag = Convert.ToBase64String(tag)
        };
    }

    private static byte[] Open(byte[] key, string accountId, SealedBox box)
    {
        var cipher = Convert.FromBase64String(box.Ciphertext);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(Convert.FromBase64String(box.Nonce), cipher, Convert.FromBase64String(box.Tag), plain,
            Encoding.UTF8.GetBytes(accountId));
        return plain;
    }
}

using System.Reflection;
using System.Text.Json;
using MailClient.Models;

namespace MailClient.Services;

/// <summary>
/// OAuth 클라이언트 ID 결정.
/// <list type="bullet">
/// <item>기본값: 실행 파일에 포함된 oauth-clients.json (exe 옆에 같은 이름의 파일이 있으면 비어 있지 않은 값으로 덮어씀).</item>
/// <item>계정의 <see cref="MailAccount.OAuthClientId"/> 는 사용자가 직접 등록한 앱을 쓸 때만 채우는 덮어쓰기 값입니다
///   (비어 있으면 기본값 사용). 그래서 내보내기 파일에도 기본값은 들어가지 않습니다.</item>
/// <item>Refresh Token 은 발급받은 클라이언트에 묶이므로 <see cref="MailAccount.OAuthTokenClientId"/> 에 기록해 갱신에 사용합니다.
///   (앱 업데이트로 기본값이 바뀌어도 기존 로그인은 그대로 동작)</item>
/// </list>
/// </summary>
public static class OAuthClients
{
    public sealed record ClientInfo(string ClientId, string ClientSecret)
    {
        public bool IsSet => ClientId.Length > 0;
    }

    private sealed class FileModel
    {
        public Entry? Microsoft { get; set; }
        public Entry? Google { get; set; }
    }

    private sealed class Entry
    {
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
    }

    public const string FileName = "oauth-clients.json";

    private static readonly Lazy<Dictionary<AuthMethod, ClientInfo>> Defaults = new(Load);

    /// <summary>테스트용: 기본값을 직접 지정 (null 이면 파일에서 다시 읽음)</summary>
    internal static Dictionary<AuthMethod, ClientInfo>? Override { get; set; }

    private static Dictionary<AuthMethod, ClientInfo> Current => Override ?? Defaults.Value;

    public static ClientInfo Default(AuthMethod method) =>
        Current.TryGetValue(method, out var c) ? c : new ClientInfo("", "");

    public static bool HasDefault(AuthMethod method) => Default(method).IsSet;

    /// <summary>로그인에 사용할 클라이언트 ID (직접 입력값 → 앱 기본값)</summary>
    public static string EffectiveClientId(MailAccount a) =>
        a.OAuthClientId.Length > 0 ? a.OAuthClientId : Default(a.AuthMethod).ClientId;

    /// <summary>클라이언트 ID 에 맞는 보안 비밀 (Google 데스크톱 앱만 사용, Microsoft 는 없음)</summary>
    public static string SecretFor(MailAccount a, string clientId)
    {
        if (clientId.Length == 0) return "";
        if (clientId == a.OAuthClientId) return a.OAuthClientSecret;
        var d = Default(a.AuthMethod);
        return clientId == d.ClientId ? d.ClientSecret : "";
    }

    /// <summary>Refresh Token 갱신에 쓸 클라이언트 ID (토큰을 받은 클라이언트, 기록이 없으면 현재 값)</summary>
    public static string TokenClientId(MailAccount a) =>
        a.OAuthTokenClientId.Length > 0 ? a.OAuthTokenClientId : EffectiveClientId(a);

    /// <summary>
    /// 이전 버전 계정 정리: 토큰의 클라이언트 ID 를 기록하고, 직접 입력값이 앱 기본값과 같으면 비워 기본값을 쓰게 합니다.
    /// 바뀐 것이 있으면 true.
    /// </summary>
    public static bool Normalize(IEnumerable<MailAccount> accounts)
    {
        bool changed = false;
        foreach (var a in accounts)
        {
            if (a.AuthMethod == AuthMethod.Password) continue;
            if (a.OAuthTokenClientId.Length == 0 && a.OAuthClientId.Length > 0 && a.EncryptedRefreshToken.Length > 0)
            {
                a.OAuthTokenClientId = a.OAuthClientId;
                changed = true;
            }
            var d = Default(a.AuthMethod);
            if (d.IsSet && a.OAuthClientId == d.ClientId && (a.OAuthClientSecret.Length == 0 || a.OAuthClientSecret == d.ClientSecret))
            {
                a.OAuthClientId = "";
                a.OAuthClientSecret = "";
                changed = true;
            }
        }
        return changed;
    }

    private static Dictionary<AuthMethod, ClientInfo> Load()
    {
        var result = new Dictionary<AuthMethod, ClientInfo>
        {
            [AuthMethod.OAuthMicrosoft] = new("", ""),
            [AuthMethod.OAuthGoogle] = new("", ""),
        };
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(FileName);
            if (s != null) Merge(result, JsonSerializer.Deserialize<FileModel>(s, Options));
        }
        catch (Exception ex) { Log.Error("oauth-clients (embedded)", ex); }

        var external = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(external))
        {
            try
            {
                Merge(result, JsonSerializer.Deserialize<FileModel>(File.ReadAllText(external), Options));
                Log.Write($"OAuth clients overridden by {external}");
            }
            catch (Exception ex) { Log.Error($"oauth-clients ({external})", ex); }
        }
        return result;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    private static void Merge(Dictionary<AuthMethod, ClientInfo> target, FileModel? m)
    {
        if (m == null) return;
        void One(AuthMethod method, Entry? e)
        {
            if (e == null) return;
            var cur = target[method];
            target[method] = new ClientInfo(
                string.IsNullOrWhiteSpace(e.ClientId) ? cur.ClientId : e.ClientId.Trim(),
                string.IsNullOrWhiteSpace(e.ClientSecret) ? cur.ClientSecret : e.ClientSecret.Trim());
        }
        One(AuthMethod.OAuthMicrosoft, m.Microsoft);
        One(AuthMethod.OAuthGoogle, m.Google);
    }
}

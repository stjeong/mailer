using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailClient.Models;

namespace MailClient.Services;

/// <summary>
/// Google / Microsoft OAuth 2.0 (Authorization Code + PKCE, 루프백 리디렉션) 구현.
/// 클라이언트 ID 는 앱 기본값(oauth-clients.json) 또는 사용자가 직접 등록한 값 (<see cref="OAuthClients"/>).
///  - Google: Google Cloud Console > OAuth 클라이언트 ID (데스크톱 앱), Gmail API 범위 https://mail.google.com/
///  - Microsoft: Azure Portal > 앱 등록 (모바일 및 데스크톱 플랫폼, 리디렉션 URI http://localhost),
///    API 권한: IMAP.AccessAsUser.All, POP.AccessAsUser.All, SMTP.Send, offline_access
/// </summary>
public static class OAuthService
{
    private sealed record Provider(string AuthEndpoint, string TokenEndpoint, string Scope, string RedirectHost);

    private static readonly Provider Google = new(
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        "https://mail.google.com/",
        "127.0.0.1");

    private static readonly Provider Microsoft = new(
        "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/POP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access",
        "localhost");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    /// <summary>인증 URL 을 여는 동작 (기본: 시스템 기본 브라우저). 테스트에서 교체 가능</summary>
    public static Action<string> OpenBrowser { get; set; } =
        url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private static readonly Dictionary<string, (string Token, DateTime Expires)> Cache = new();
    private static readonly object Gate = new();

    private static Provider ProviderOf(MailAccount account) => account.AuthMethod switch
    {
        AuthMethod.OAuthGoogle => Google,
        AuthMethod.OAuthMicrosoft => Microsoft,
        _ => throw new InvalidOperationException("OAuth 계정이 아닙니다.")
    };

    public static void ClearCache(string accountId)
    {
        lock (Gate) Cache.Remove(accountId);
    }

    /// <summary>
    /// 액세스 토큰을 반환합니다. 캐시 → Refresh Token 순으로 시도하며, 실패 시 예외를 던집니다.
    /// (대화형 로그인은 <see cref="AuthorizeInteractiveAsync"/> 로 계정 설정 화면에서 수행)
    /// </summary>
    public static async Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken ct)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(account.Id, out var c) && c.Expires > DateTime.UtcNow.AddMinutes(2))
                return c.Token;
        }

        var refresh = account.RefreshToken;
        if (string.IsNullOrEmpty(refresh))
            throw new InvalidOperationException("OAuth 로그인이 필요합니다. [계정 편집]에서 'OAuth 로그인' 버튼을 눌러주세요.");

        var provider = ProviderOf(account);
        // 토큰은 발급받은 클라이언트로만 갱신 가능
        var clientId = OAuthClients.TokenClientId(account);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
        };
        var secret = OAuthClients.SecretFor(account, clientId);
        if (!string.IsNullOrEmpty(secret)) form["client_secret"] = secret;
        if (provider == Microsoft) form["scope"] = provider.Scope;

        var json = await PostTokenAsync(provider, form, ct);
        return StoreTokens(account, json);
    }

    /// <summary>시스템 브라우저를 열어 사용자 로그인/동의를 받고 토큰을 발급받습니다.</summary>
    public static async Task AuthorizeInteractiveAsync(MailAccount account, CancellationToken ct)
    {
        var clientId = OAuthClients.EffectiveClientId(account);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("이 앱에 기본 OAuth 클라이언트 ID가 없습니다. 계정 창에서 직접 등록한 클라이언트 ID를 입력하세요.");

        var provider = ProviderOf(account);
        int port = GetFreePort();
        // HttpListener 접두사는 '/' 로 끝나야 하지만, Microsoft 는 등록된 리디렉션 URI(http://localhost)와
        // 경로까지 비교하므로(포트만 무시) 끝의 '/' 없이 보냅니다. 브라우저는 어느 쪽이든 루트 경로로 접속합니다.
        var listenerPrefix = $"http://{provider.RedirectHost}:{port}/";
        var redirectUri = provider == Microsoft ? $"http://{provider.RedirectHost}:{port}" : listenerPrefix;

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = provider.Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        if (!string.IsNullOrWhiteSpace(account.EmailAddress)) query["login_hint"] = account.EmailAddress;
        if (provider == Google)
        {
            query["access_type"] = "offline";
            query["prompt"] = "consent";
        }
        else
        {
            query["prompt"] = "select_account";
        }

        var authUrl = provider.AuthEndpoint + "?" + string.Join("&",
            query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        using var listener = new HttpListener();
        listener.Prefixes.Add(listenerPrefix);
        listener.Start();

        // 취소(사용자) 또는 시간 초과 시 리스너를 멈춰 대기 중인 GetContextAsync 를 즉시 깨움
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LoginTimeout);
        using var stopOnCancel = timeout.Token.Register(() =>
        {
            try { listener.Stop(); } catch { /* 이미 종료됨 */ }
        });

        try
        {
            OpenBrowser(authUrl);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"웹 브라우저를 열 수 없습니다: {ex.Message}\n\n다음 주소를 직접 열어 주세요:\n{authUrl}");
        }

        // 리디렉션(code 또는 error 포함)이 올 때까지 대기. favicon 등 다른 요청은 무시
        HttpListenerContext context;
        while (true)
        {
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (timeout.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();   // 사용자가 취소
                throw new TimeoutException($"OAuth 로그인을 {LoginTimeout.TotalMinutes:0}분 동안 완료하지 않아 취소되었습니다. 다시 시도하세요.");
            }

            var q = context.Request.QueryString;
            if (q["code"] != null || q["error"] != null) break;
            context.Response.StatusCode = 404;
            context.Response.Close();
        }

        var qs = context.Request.QueryString;
        var code = qs["code"];
        var error = qs["error"];
        var returnedState = qs["state"];

        var html = code != null && returnedState == state
            ? "<html><head><meta charset='utf-8'></head><body style='font-family:sans-serif'><h3>로그인 완료</h3>이 창을 닫고 메일 프로그램으로 돌아가세요.</body></html>"
            : $"<html><head><meta charset='utf-8'></head><body style='font-family:sans-serif'><h3>로그인 실패</h3>{WebUtility.HtmlEncode(error ?? "알 수 없는 오류")}</body></html>";
        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, ct);
        context.Response.OutputStream.Close();
        listener.Stop();

        if (error != null) throw new InvalidOperationException($"OAuth 오류: {error} {qs["error_description"]}");
        if (code == null || returnedState != state) throw new InvalidOperationException("OAuth 응답이 올바르지 않습니다.");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        };
        var secret = OAuthClients.SecretFor(account, clientId);
        if (!string.IsNullOrEmpty(secret)) form["client_secret"] = secret;
        if (provider == Microsoft) form["scope"] = provider.Scope;

        var json = await PostTokenAsync(provider, form, ct);
        StoreTokens(account, json);
        account.OAuthTokenClientId = clientId;
        if (string.IsNullOrEmpty(account.RefreshToken))
            throw new InvalidOperationException("Refresh Token 을 받지 못했습니다. (offline_access 권한 확인)");
    }

    private static async Task<JsonElement> PostTokenAsync(Provider provider, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await Http.PostAsync(provider.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();
        if (!resp.IsSuccessStatusCode)
        {
            var err = root.TryGetProperty("error_description", out var d) ? d.GetString()
                    : root.TryGetProperty("error", out var e) ? e.GetString() : body;
            throw new InvalidOperationException($"토큰 발급 실패: {err}");
        }
        return root;
    }

    private static string StoreTokens(MailAccount account, JsonElement json)
    {
        var access = json.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("access_token 없음");
        var expiresIn = json.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var sec) ? sec : 3600;
        if (json.TryGetProperty("refresh_token", out var r) && !string.IsNullOrEmpty(r.GetString()))
            account.RefreshToken = r.GetString()!;

        lock (Gate) Cache[account.Id] = (access, DateTime.UtcNow.AddSeconds(expiresIn));
        return access;
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

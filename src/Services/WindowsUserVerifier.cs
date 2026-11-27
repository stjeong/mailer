using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Windows.Security.Credentials.UI;

namespace MailClient.Services;

public enum UserVerificationOutcome
{
    /// <summary>현재 로그인한 Windows 사용자 본인임이 확인됨</summary>
    Verified,
    /// <summary>사용자가 취소함</summary>
    Cancelled,
    /// <summary>확인 실패 (암호 틀림 반복, 다른 사용자, 시스템 오류 등)</summary>
    Failed
}

public sealed record UserVerificationResult(UserVerificationOutcome Outcome, string Method, string? Error = null)
{
    public bool IsVerified => Outcome == UserVerificationOutcome.Verified;
}

/// <summary>
/// 민감한 작업(계정 목록 내보내기 등) 전에 현재 로그인한 Windows 사용자 본인인지 확인합니다.
/// <list type="number">
/// <item>Windows Hello(PIN/지문/얼굴)를 쓸 수 있으면 표준 "Windows 보안" 확인 창을 띄웁니다.</item>
/// <item>쓸 수 없으면 "Windows 보안" 자격 증명 창(현재 사용자만 표시)으로 Windows 암호를 받아
///   LogonUser 로 검증하고, 로그온된 계정의 SID 가 현재 사용자와 같은지 확인합니다.</item>
/// </list>
/// 입력된 암호는 문자 배열로만 다루고 사용 직후 지웁니다. 암호를 저장하거나 기록하지 않습니다.
/// </summary>
public static class WindowsUserVerifier
{
    private const int MaxPasswordAttempts = 3;

    public static async Task<UserVerificationResult> VerifyAsync(IWin32Window owner, string caption, string message)
    {
        // 1) Windows Hello
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability == UserConsentVerifierAvailability.Available)
            {
                var r = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(owner.Handle, message);
                switch (r)
                {
                    case UserConsentVerificationResult.Verified:
                        return new(UserVerificationOutcome.Verified, "Windows Hello");
                    case UserConsentVerificationResult.Canceled:
                        return new(UserVerificationOutcome.Cancelled, "Windows Hello");
                    case UserConsentVerificationResult.RetriesExhausted:
                        return new(UserVerificationOutcome.Failed, "Windows Hello", "확인 시도 횟수를 초과했습니다.");
                    case UserConsentVerificationResult.DeviceBusy:
                        return new(UserVerificationOutcome.Failed, "Windows Hello", "인증 장치가 사용 중입니다. 잠시 후 다시 시도하세요.");
                    // NotConfiguredForUser / DeviceNotPresent / DisabledByPolicy → 암호 확인으로 진행
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("windows hello", ex);   // Hello 를 쓸 수 없는 환경 → 암호 확인으로 진행
        }

        // 2) Windows 암호
        return PromptWindowsPassword(owner.Handle, caption, message);
    }

    // =====================================================================
    // Windows 암호 확인 (CredUI + LogonUser)
    // =====================================================================

    private static UserVerificationResult PromptWindowsPassword(IntPtr owner, string caption, string message)
    {
        const string method = "Windows 암호";
        var current = WindowsIdentity.GetCurrent().User;
        int authError = 0;

        for (int attempt = 0; attempt < MaxPasswordAttempts; attempt++)
        {
            var info = new CREDUI_INFO
            {
                cbSize = Marshal.SizeOf<CREDUI_INFO>(),
                hwndParent = owner,
                pszCaptionText = caption,
                pszMessageText = message
            };
            uint authPackage = 0;
            int save = 0;
            int rc = CredUIPromptForWindowsCredentials(ref info, authError, ref authPackage, IntPtr.Zero, 0,
                out var outBuf, out var outSize, ref save, CREDUIWIN_ENUMERATE_CURRENT_USER);

            if (rc == ERROR_CANCELLED) return new(UserVerificationOutcome.Cancelled, method);
            if (rc != 0) return new(UserVerificationOutcome.Failed, method, $"Windows 보안 창을 열 수 없습니다. (오류 {rc})");

            var user = new char[1024];
            var domain = new char[1024];
            var password = new char[1024];
            try
            {
                int userLen = user.Length, domainLen = domain.Length, pwLen = password.Length;
                if (!CredUnPackAuthenticationBuffer(CRED_PACK_PROTECTED_CREDENTIALS, outBuf, outSize,
                        user, ref userLen, domain, ref domainLen, password, ref pwLen))
                {
                    return new(UserVerificationOutcome.Failed, method,
                        "입력한 자격 증명을 확인할 수 없습니다. PIN 대신 Windows 암호를 입력하세요.");
                }

                var (logonUser, logonDomain) = SplitUserName(new string(user, 0, Math.Max(0, userLen - 1)).TrimEnd('\0'),
                                                             new string(domain, 0, Math.Max(0, domainLen - 1)).TrimEnd('\0'));

                int err = TryLogon(logonUser, logonDomain, password, out var sid);
                if (err == 0)
                {
                    if (sid != null && sid == current)
                        return new(UserVerificationOutcome.Verified, method);
                    return new(UserVerificationOutcome.Failed, method, "현재 로그인한 Windows 사용자의 계정이 아닙니다.");
                }
                if (err != ERROR_LOGON_FAILURE)
                    return new(UserVerificationOutcome.Failed, method, LogonErrorText(err));

                authError = err;   // 다음 창에 "암호가 올바르지 않습니다" 표시
            }
            finally
            {
                Array.Clear(password);
                Array.Clear(user);
                Array.Clear(domain);
                ZeroAndFree(outBuf, outSize);
            }
        }
        return new(UserVerificationOutcome.Failed, method, "암호가 여러 번 틀렸습니다.");
    }

    /// <summary>"MicrosoftAccount\a@b.com", "PC\user", "user@domain" 등을 (사용자, 도메인)으로 분리</summary>
    internal static (string User, string? Domain) SplitUserName(string userName, string domain)
    {
        if (!string.IsNullOrEmpty(domain)) return (userName, domain);
        var u = new char[514];
        var d = new char[338];
        if (CredUIParseUserName(userName, u, u.Length, d, d.Length) == 0)
        {
            var parsedUser = new string(u).TrimEnd('\0');
            var parsedDomain = new string(d).TrimEnd('\0');
            return (parsedUser, parsedDomain.Length > 0 ? parsedDomain : null);
        }
        return (userName, null);
    }

    /// <summary>로그온을 시도하고 성공하면 그 계정의 SID 를 돌려줍니다. 반환값은 Win32 오류 코드(0=성공).</summary>
    private static int TryLogon(string user, string? domain, char[] password, out SecurityIdentifier? sid)
    {
        sid = null;
        var pin = GCHandle.Alloc(password, GCHandleType.Pinned);
        try
        {
            var pwPtr = pin.AddrOfPinnedObject();
            // 네트워크 로그온이 가장 가볍고, 허용되지 않는 환경이면 대화형 로그온으로 재시도
            foreach (var type in new[] { LOGON32_LOGON_NETWORK, LOGON32_LOGON_INTERACTIVE })
            {
                if (LogonUser(user, domain, pwPtr, type, LOGON32_PROVIDER_DEFAULT, out var token))
                {
                    using (token)
                    using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                        sid = identity.User;
                    return 0;
                }
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_LOGON_TYPE_NOT_GRANTED) return err;
            }
            return ERROR_LOGON_TYPE_NOT_GRANTED;
        }
        finally
        {
            pin.Free();
        }
    }

    private static string LogonErrorText(int err) => err switch
    {
        ERROR_ACCOUNT_RESTRICTION => "계정 제한으로 확인할 수 없습니다. (암호가 없는 계정은 확인할 수 없습니다.)",
        ERROR_ACCOUNT_LOCKED_OUT => "계정이 잠겨 있습니다.",
        ERROR_LOGON_TYPE_NOT_GRANTED => "이 계정은 로그온 확인이 허용되지 않습니다.",
        _ => $"Windows 계정을 확인할 수 없습니다. (오류 {err})"
    };

    private static void ZeroAndFree(IntPtr buffer, uint size)
    {
        if (buffer == IntPtr.Zero) return;
        for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
        Marshal.FreeCoTaskMem(buffer);
    }

    // =====================================================================
    // Win32
    // =====================================================================

    private const int CREDUIWIN_ENUMERATE_CURRENT_USER = 0x200;
    private const int CRED_PACK_PROTECTED_CREDENTIALS = 0x1;
    private const int LOGON32_LOGON_INTERACTIVE = 2;
    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;
    private const int ERROR_CANCELLED = 1223;
    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_ACCOUNT_RESTRICTION = 1327;
    private const int ERROR_ACCOUNT_LOCKED_OUT = 1909;
    private const int ERROR_LOGON_TYPE_NOT_GRANTED = 1385;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFO
    {
        public int cbSize;
        public IntPtr hwndParent;
        public string pszMessageText;
        public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentials(ref CREDUI_INFO pUiInfo, int dwAuthError, ref uint pulAuthPackage,
        IntPtr pvInAuthBuffer, uint ulInAuthBufferSize, out IntPtr ppvOutAuthBuffer, out uint pulOutAuthBufferSize,
        ref int pfSave, int dwFlags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredUnPackAuthenticationBuffer(int dwFlags, IntPtr pAuthBuffer, uint cbAuthBuffer,
        [Out] char[] pszUserName, ref int pcchMaxUserName,
        [Out] char[] pszDomainName, ref int pcchMaxDomainName,
        [Out] char[] pszPassword, ref int pcchMaxPassword);

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIParseUserName(string userName, [Out] char[] user, int userBufferSize,
        [Out] char[] domain, int domainBufferSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string lpszUsername, string? lpszDomain, IntPtr lpszPassword,
        int dwLogonType, int dwLogonProvider, out SafeAccessTokenHandle phToken);
}

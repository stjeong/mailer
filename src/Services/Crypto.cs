using System.Security.Cryptography;
using System.Text;

namespace MailClient.Services;

/// <summary>Windows DPAPI(현재 사용자 범위)를 이용한 비밀 값 암호화</summary>
public static class Crypto
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MailClient.v1");

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}

namespace MailClient.Services;

public static class AppPaths
{
    /// <summary>데이터 루트. 환경 변수 MAILCLIENT_DATA 로 변경 가능 (포터블/테스트 용)</summary>
    public static string Root { get; } =
        Environment.GetEnvironmentVariable("MAILCLIENT_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MailClient");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string DatabaseFile => Path.Combine(Root, "mail.db");
    /// <summary>이전 버전의 파일 기반 메일 저장 위치 (SQLite 로 자동 이전됨)</summary>
    public static string DataRoot => Path.Combine(Root, "Data");
    public static string LogFile => Path.Combine(Root, "mailclient.log");

    /// <summary>설정의 DB 경로 → 실제 파일 경로 (비어 있으면 기본 위치, 환경 변수 확장, 절대 경로)</summary>
    public static string ResolveDatabase(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? DatabaseFile
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 로그 실패는 무시
        }
    }

    public static void Error(string context, Exception ex) => Write($"ERROR {context}: {ex}");
}

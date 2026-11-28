using Microsoft.Win32;

namespace MailClient.Services;

/// <summary>
/// Windows 시작 시 자동 실행 (HKCU\Software\Microsoft\Windows\CurrentVersion\Run 의 "MailClient" 값).
/// 설정 파일이 아니라 레지스트리 값 자체가 상태이므로, 설정 창은 열 때마다 레지스트리를 읽습니다.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "MailClient";

    /// <summary>
    /// msix 로 설치된 앱은 레지스트리 쓰기가 패키지 전용 위치로 가상화되어 Windows 가 Run 값을 보지 못하므로 지원하지 않음
    /// (msix 는 매니페스트의 시작 작업(startupTask)으로 해야 함)
    /// </summary>
    public static bool IsSupported => !AppPaths.IsPackaged;

    /// <summary>등록할 명령: 지금 실행 중인 MailClient.exe (공백이 있어도 되도록 따옴표)</summary>
    public static string Command => $"\"{Environment.ProcessPath}\"";

    /// <summary>등록된 명령 (없으면 null)</summary>
    public static string? RegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string is { Length: > 0 } s ? s : null;
    }

    public static bool IsEnabled() => RegisteredCommand() != null;

    /// <summary>등록돼 있지만 다른 위치의 실행 파일을 가리키는지 (프로그램 폴더를 옮긴 경우 등)</summary>
    public static bool PointsElsewhere() =>
        RegisteredCommand() is { } cmd && !string.Equals(cmd.Trim(), Command, StringComparison.OrdinalIgnoreCase);

    /// <summary>켜면 지금 실행 파일 경로로 등록(이미 있으면 갱신), 끄면 값을 지움</summary>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Write(enabled ? $"Run at startup: registered {Command}" : "Run at startup: removed");
    }
}

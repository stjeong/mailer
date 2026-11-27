using System.Text.Json;
using System.Text.Json.Serialization;
using MailClient.Models;

namespace MailClient.Services;

public static class SettingsService
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    if (settings.CheckIntervalMinutes < 1) settings.CheckIntervalMinutes = 5;
                    settings.Junk ??= new JunkSettings();
                    settings.Accounts ??= [];
                    // 이전 버전 계정: 앱 기본값과 같은 클라이언트 ID 는 비워 기본값을 따르게 하고, 토큰의 클라이언트를 기록
                    if (OAuthClients.Normalize(settings.Accounts)) Save(settings);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("settings load", ex);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
    }
}

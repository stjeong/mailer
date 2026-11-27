using Microsoft.Win32;
using MailClient.Models;
using MailClient.Services;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace MailClient.Forms;

/// <summary>
/// Windows 토스트 알림 (알림 센터에 남는 알림).
/// <para>
/// zip 으로 실행할 때는 패키지 ID 가 없으므로 앱 ID(AppUserModelID)를 사용자 레지스트리에 등록해 표시 이름과 아이콘을 정합니다.
/// (HKCU\Software\Classes\AppUserModelId\{AppId}, 관리자 권한 불필요)
/// MSIX 로 설치해 실행할 때는 패키지 ID 의 앱 이름·로고를 그대로 씁니다 (레지스트리 등록 안 함).
/// </para>
/// <para>
/// 알림을 누르면 프로그램이 실행 중일 때 <see cref="Activated"/> 가 발생합니다. 프로그램을 닫은 뒤 알림 센터에 남은 알림을
/// 누르면 아무 동작도 하지 않습니다 (COM 활성화 서버를 등록하지 않음).
/// </para>
/// </summary>
internal static class ToastNotifier
{
    public const string AppId = "MailClient.Desktop";
    private const string Group = "newmail";

    /// <summary>알림을 눌렀을 때 (인수 문자열). UI 스레드가 아닌 곳에서 호출될 수 있음.</summary>
    public static event Action<string>? Activated;

    // 알림 개체를 잡아 두어야 누름 이벤트가 사라지지 않음 (가장 최근 몇 개만)
    private static readonly List<ToastNotification> Recent = [];
    private static bool _registered;

    /// <summary>MSIX 로 설치되어 패키지 ID 를 가지고 실행 중인지</summary>
    private static readonly bool IsPackaged = DetectPackage();

    /// <summary>새 메일 알림을 표시합니다. 표시할 수 없으면(알림 꺼짐, 오류) false.</summary>
    public static bool ShowNewMail(IReadOnlyList<MailItem> items, int totalNew)
    {
        try
        {
            EnsureRegistered();
            var notifier = IsPackaged
                ? ToastNotificationManager.CreateToastNotifier()
                : ToastNotificationManager.CreateToastNotifier(AppId);
            // 사용자가 이 앱의 알림을 껐으면 표시하지 않음. 바로 가기 없이 실행하는 앱은 이 값을 읽을 때
            // "요소를 찾을 수 없음"(0x80070490) 오류가 날 수 있는데, 알림 표시는 되므로 그때는 그냥 표시
            try
            {
                if (notifier.Setting != NotificationSetting.Enabled)
                {
                    Log.Write($"Toast disabled: {notifier.Setting}");
                    return false;
                }
            }
            catch (System.Runtime.InteropServices.COMException) { }

            var (title, lines) = Compose(items, totalNew);
            var latest = items.OrderByDescending(i => i.Date).FirstOrDefault();
            var args = latest == null ? "" : $"account={Uri.EscapeDataString(latest.AccountId)}&id={Uri.EscapeDataString(latest.Id)}";

            var toast = new ToastNotification(BuildXml(title, lines, args)) { Group = Group, Tag = DateTime.Now.Ticks.ToString() };
            toast.Activated += (_, e) => Activated?.Invoke((e as ToastActivatedEventArgs)?.Arguments ?? "");
            toast.Failed += (_, e) => Log.Write($"Toast failed: {e.ErrorCode?.Message}");
            lock (Recent)
            {
                Recent.Add(toast);
                if (Recent.Count > 10) Recent.RemoveAt(0);
            }
            notifier.Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("toast", ex);
            return false;
        }
    }

    /// <summary>알림 센터에 남아 있는 새 메일 알림을 지웁니다 (사용자가 메일을 확인한 뒤).</summary>
    public static void ClearNewMail()
    {
        if (!_registered) return;
        try
        {
            if (IsPackaged) ToastNotificationManager.History.RemoveGroup(Group);
            else ToastNotificationManager.History.RemoveGroup(Group, AppId);
        }
        catch (Exception ex) { Log.Error("toast clear", ex); }
    }

    /// <summary>알림 제목과 본문 줄 (토스트는 제목 + 최대 2줄)</summary>
    internal static (string Title, List<string> Lines) Compose(IReadOnlyList<MailItem> items, int totalNew)
    {
        var sorted = items.OrderByDescending(i => i.Date).ToList();
        static string Subject(MailItem i) => string.IsNullOrWhiteSpace(i.Subject) ? "(제목 없음)" : i.Subject;
        static string Sender(MailItem i) { var n = MainForm.DisplayNames(i.From); return string.IsNullOrWhiteSpace(n) ? "(보낸 사람 없음)" : n; }

        if (totalNew == 1 && sorted.Count == 1)
            return (Sender(sorted[0]), [Subject(sorted[0])]);

        var lines = sorted.Take(2).Select(i => $"{Sender(i)} - {Subject(i)}").ToList();
        int rest = totalNew - lines.Count;
        if (rest > 0 && lines.Count > 0) lines[^1] += $"  (외 {rest}개)";
        return ($"새 메일 {totalNew}개", lines);
    }

    private static XmlDocument BuildXml(string title, List<string> lines, string args)
    {
        var doc = new XmlDocument();
        doc.LoadXml("<toast activationType=\"foreground\"><visual><binding template=\"ToastGeneric\"></binding></visual>" +
                    "<audio src=\"ms-winsoundevent:Notification.Mail\"/></toast>");
        ((XmlElement)doc.SelectSingleNode("/toast")!).SetAttribute("launch", args);
        var binding = doc.SelectSingleNode("//binding")!;
        foreach (var text in lines.Prepend(title))
        {
            var el = doc.CreateElement("text");
            el.AppendChild(doc.CreateTextNode(text));   // 텍스트 노드로 넣어 특수 문자 안전
            binding.AppendChild(el);
        }
        return doc;
    }

    /// <summary>앱 ID 의 표시 이름·아이콘을 사용자 레지스트리에 등록 (실행할 때마다 경로를 최신으로)</summary>
    private static void EnsureRegistered()
    {
        if (_registered) return;
        if (IsPackaged) { _registered = true; return; }
        var iconPath = Path.Combine(AppPaths.Root, "toast-icon.png");
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            using var bmp = AppIcon.Render(256, newMail: false);
            bmp.Save(iconPath, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Log.Error("toast icon", ex); }

        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
        key.SetValue("DisplayName", "MailClient");
        if (File.Exists(iconPath)) key.SetValue("IconUri", iconPath);
        _registered = true;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    private static bool DetectPackage()
    {
        const int AppModelErrorNoPackage = 15700;
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch { return false; }
    }
}

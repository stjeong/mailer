using MailClient.Forms;
using MailClient.Services;

namespace MailClient;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "MailClient.SingleInstance.v1", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("메일 프로그램이 이미 실행 중입니다.", "MailClient", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        // 어떤 빌드가 실행됐는지 확인할 수 있도록 실행 파일 경로와 빌드 시각을 기록
        var exe = Environment.ProcessPath ?? "";
        Log.Write($"App start: {exe} (built {File.GetLastWriteTime(exe):yyyy-MM-dd HH:mm:ss}, " +
                  $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription})");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("UI thread", e.Exception);
            MessageBox.Show(e.Exception.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("unhandled", ex);
        };

        Application.Run(new MainForm());
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MailClient.Services;

/// <summary>GitHub Release 의 최신 버전 정보와 이 실행 형태(msix / zip)에 맞는 설치 파일</summary>
public sealed record UpdateInfo(Version Version, string Tag, string AssetName, string DownloadUrl, long Size,
    string ReleaseUrl, string Notes);

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// GitHub Release(.github/workflows/release.yml 이 만든 것)에서 최신 버전을 확인하고 받아 설치합니다.
/// <list type="bullet">
/// <item>msix 로 설치된 경우: 새 .msix 를 받아 Windows 앱 설치 관리자로 엽니다.</item>
/// <item>zip 으로 실행 중인 경우: 새 zip 을 받아 풀어 두고, 프로그램이 끝나면 PowerShell 스크립트가 실행 폴더에 덮어쓴 뒤 다시 실행합니다.</item>
/// </list>
/// </summary>
public static class UpdateService
{
    /// <summary>릴리스를 받을 GitHub 저장소 (owner/name). 로그인 없이 조회하므로 공개 저장소여야 합니다.</summary>
    public const string Repository = "stjeong/mailer";

    public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    public static string CurrentVersionText => Display(CurrentVersion);

    public static string Display(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    private static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MailClient", CurrentVersionText));
        return http;
    }

    /// <summary>새 버전이 있으면 그 정보, 이미 최신이면 null</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new UpdateException("GitHub 에 연결할 수 없습니다.\n" + ex.Message, ex); }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new UpdateException($"GitHub 저장소({Repository})에서 릴리스를 찾을 수 없습니다.\n저장소가 비공개이거나 아직 릴리스가 없습니다.");
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub 응답 오류: {(int)response.StatusCode} {response.ReasonPhrase}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
                throw new UpdateException($"릴리스 태그를 버전으로 읽을 수 없습니다: {tag}");
            var latest = Normalize(parsed);
            Log.Write($"Update check: current {CurrentVersionText}, latest {tag}, packaged={AppPaths.IsPackaged}");
            if (latest <= CurrentVersion) return null;

            // 실행 형태에 맞는 파일 (release.yml / build/Package.ps1 의 이름 규칙)
            var suffix = AppPaths.IsPackaged ? $"-{Arch}.msix" : $"-win-{Arch}.zip";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                return new UpdateInfo(latest, tag, name,
                    asset.GetProperty("browser_download_url").GetString() ?? "",
                    asset.GetProperty("size").GetInt64(),
                    root.GetProperty("html_url").GetString() ?? "",
                    root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
            }
            throw new UpdateException($"{tag} 릴리스에 이 PC 용 설치 파일(*{suffix})이 없습니다.");
        }
    }

    /// <summary>설치 파일을 받아 로컬 경로를 돌려줍니다. progress: 0~1</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(DownloadRoot(), Display(info.Version));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, info.AssetName);
        var partial = path + ".part";

        using var http = CreateClient();
        try
        {
            using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? info.Size;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            try { File.Delete(partial); } catch { }
            throw new UpdateException("업데이트 파일을 받지 못했습니다.\n" + ex.Message, ex);
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
        Log.Write($"Update downloaded: {path}");
        return path;
    }

    /// <summary>
    /// 받은 파일로 설치를 시작합니다. 성공하면 호출한 쪽에서 프로그램을 끝내야 합니다
    /// (msix: 설치 관리자가 실행 중인 앱을 닫아야 하고, zip: 끝나기를 기다렸다가 파일을 바꿈).
    /// </summary>
    public static void StartInstall(string downloadedFile)
    {
        if (AppPaths.IsPackaged) StartMsixInstall(downloadedFile);
        else StartZipInstall(downloadedFile);
    }

    // msix 로 실행 중이면 %LocalAppData% 에 쓴 파일이 패키지 전용 위치로 리디렉션되어 설치 관리자가 찾지 못할 수 있으므로
    // 패키지의 TempState 폴더(실제 경로)를 씁니다.
    private static string DownloadRoot() =>
        AppPaths.IsPackaged
            ? Path.Combine(Windows.Storage.ApplicationData.Current.TemporaryFolder.Path, "update")
            : Path.Combine(Path.GetTempPath(), "MailClient-update");

    private static void StartMsixInstall(string msix)
    {
        try
        {
            Process.Start(new ProcessStartInfo(msix) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new UpdateException("앱 설치 관리자를 열 수 없습니다. Microsoft Store 의 '앱 설치 관리자'가 필요합니다.\n" + ex.Message, ex);
        }
    }

    private static void StartZipInstall(string zip)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');
        var exe = Environment.ProcessPath ?? Path.Combine(appDir, "MailClient.exe");

        // 실행 폴더에 쓸 수 있는지 미리 확인 (Program Files 등이면 스크립트가 덮어쓰지 못함)
        try
        {
            var probe = Path.Combine(appDir, $".update-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new UpdateException($"실행 폴더에 쓸 수 없어 업데이트할 수 없습니다.\n{appDir}\n\n릴리스 페이지에서 직접 받아 설치하세요.", ex);
        }

        var work = Path.GetDirectoryName(zip)!;
        var staging = Path.Combine(work, "files");
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            ZipFile.ExtractToDirectory(zip, staging);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new UpdateException("업데이트 파일의 압축을 풀지 못했습니다.\n" + ex.Message, ex);
        }
        if (!File.Exists(Path.Combine(staging, Path.GetFileName(exe))))
            throw new UpdateException($"업데이트 파일에 {Path.GetFileName(exe)} 이(가) 없습니다.");

        var script = Path.Combine(work, "apply-update.ps1");
        File.WriteAllText(script, ApplyScript, new System.Text.UTF8Encoding(true));   // Windows PowerShell 5 는 BOM 이 있어야 UTF-8 로 읽음

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = work,
        };
        foreach (var arg in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                     "-ProcessId", Environment.ProcessId.ToString(), "-Source", staging, "-Target", appDir,
                     "-Exe", exe, "-LogFile", AppPaths.LogFile,
                 })
            psi.ArgumentList.Add(arg);
        Process.Start(psi);
        Log.Write($"Update script started: {script} -> {appDir}");
    }

    /// <summary>프로그램이 끝나기를 기다렸다가 새 파일로 덮어쓰고 다시 실행</summary>
    private const string ApplyScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Exe, [string]$LogFile)
        function Write-UpdateLog([string]$m) {
            try { Add-Content -LiteralPath $LogFile -Value ("[{0:yyyy-MM-dd HH:mm:ss}] Update: {1}" -f (Get-Date), $m) -Encoding UTF8 } catch { }
        }
        try { Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction Stop } catch { }
        if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
            Write-UpdateLog "program did not exit; update cancelled"
            exit 1
        }
        # 잠긴 파일이 풀릴 때까지 다시 시도 (/R /W), 코드 8 이상이 실패
        robocopy $Source $Target /E /R:20 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        $code = $LASTEXITCODE
        if ($code -ge 8) { Write-UpdateLog "copy failed (robocopy $code)" } else { Write-UpdateLog "installed to $Target" }
        Start-Process -FilePath $Exe
        # 성공하면 받은 zip 과 풀어 둔 파일이 있는 작업 폴더를 지움 (이 스크립트도 그 안에 있지만 이미 읽혀 실행 중)
        if ($code -lt 8) { Remove-Item -LiteralPath (Split-Path $Source -Parent) -Recurse -Force -ErrorAction SilentlyContinue }
        """;

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}

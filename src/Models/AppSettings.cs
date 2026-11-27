namespace MailClient.Models;

public class JunkSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>항상 정상 메일로 처리 (예외) - 이메일 주소</summary>
    public List<string> SafeSenders { get; set; } = [];
    /// <summary>항상 정상 메일로 처리 (예외) - 도메인 (예: company.com)</summary>
    public List<string> SafeDomains { get; set; } = [];

    /// <summary>항상 스팸으로 처리 - 이메일 주소</summary>
    public List<string> BlockedSenders { get; set; } = [];
    /// <summary>항상 스팸으로 처리 - 도메인</summary>
    public List<string> BlockedDomains { get; set; } = [];

    /// <summary>제목/본문 스팸 키워드</summary>
    public List<string> SpamKeywords { get; set; } =
    [
        "(광고)", "[광고]", "무료 체험", "대출", "카지노", "바카라", "당첨", "수익 보장",
        "viagra", "casino", "lottery", "winner", "free money", "act now", "click here",
        "limited time offer", "100% free", "risk-free", "urgent response"
    ];

    /// <summary>스팸 판정 임계 점수 (낮을수록 엄격)</summary>
    public int Threshold { get; set; } = 5;
}

/// <summary>
/// 받은 메일 규칙: <see cref="Sender"/> 에게서 온 메일을 <see cref="SaveDirectory"/> 에 .eml 파일로 저장.
/// 메일은 받은 편지함(또는 정크 판정 시 Junk Email)에도 그대로 들어갑니다.
/// </summary>
public class MailRule
{
    public bool Enabled { get; set; } = true;
    /// <summary>보낸 사람(From) 이메일 주소 (대소문자 무시)</summary>
    public string Sender { get; set; } = "";
    /// <summary>.eml 을 저장할 폴더 (환경 변수 사용 가능)</summary>
    public string SaveDirectory { get; set; } = "";
}

public class AppSettings
{
    /// <summary>새 메일 확인 주기 (분)</summary>
    public int CheckIntervalMinutes { get; set; } = 5;
    public bool CheckOnStartup { get; set; } = true;
    public bool ShowNotification { get; set; } = true;
    public bool ConfirmPermanentDelete { get; set; } = true;
    /// <summary>HTML 메일이 가운데 정렬로 배치돼 있어도 왼쪽 정렬로 표시</summary>
    public bool HtmlLeftAlign { get; set; } = true;

    /// <summary>메일 저장소(SQLite) 파일 경로. 비어 있으면 기본 위치(%AppData%\MailClient\mail.db).
    /// PC 마다 다른 값이므로 설정 내보내기에는 포함하지 않음</summary>
    public string DatabasePath { get; set; } = "";

    public List<MailAccount> Accounts { get; set; } = [];
    public JunkSettings Junk { get; set; } = new();
    /// <summary>받은 메일 규칙. 지금은 설정 창에서 첫 번째 규칙 하나만 편집합니다.
    /// 저장 폴더가 PC 마다 다르므로 설정 내보내기에는 포함하지 않음</summary>
    public List<MailRule> Rules { get; set; } = [];

    // 창 상태
    /// <summary>창 크기 (96 DPI 기준 논리 단위 - 화면 배율이 달라도 같은 크기로 보이도록)</summary>
    public int WindowLogicalWidth { get; set; } = 1280;
    public int WindowLogicalHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }
    /// <summary>왼쪽 트리 폭 (논리 단위)</summary>
    public int TreeLogicalWidth { get; set; } = 240;
    /// <summary>오른쪽 아래 미리 보기 창 표시 여부</summary>
    public bool ShowPreviewPane { get; set; } = true;
    /// <summary>트리에서 접어 둔 노드: 계정 ID, 보관함은 "archive:" + 하위 폴더 ID (최상위는 "archive:").
    /// 새 노드는 펼친 상태로 시작하도록 접은 쪽을 기억</summary>
    public List<string> CollapsedAccounts { get; set; } = [];
}

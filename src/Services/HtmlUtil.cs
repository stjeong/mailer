using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace MailClient.Services;

public static partial class HtmlUtil
{
    /// <summary>HTML 을 대략적인 일반 텍스트로 변환</summary>
    public static string HtmlToText(string html)
    {
        var s = HeadRegex().Replace(html, "");
        s = ScriptStyleRegex().Replace(s, "");
        s = BrRegex().Replace(s, "\n");
        s = BlockEndRegex().Replace(s, "\n");
        s = TagRegex().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = MultiBlankRegex().Replace(s, "\n\n");
        return s.Replace("\r\n", "\n").Replace("\n", Environment.NewLine).Trim();
    }

    /// <summary>메시지를 WebBrowser 표시용 HTML 로 변환 (cid: 인라인 이미지 포함, 스크립트 제거)</summary>
    /// <param name="leftAlign">HTML 메일이 바깥 틀을 가운데 배치해도 왼쪽 정렬로 표시</param>
    public static string BuildDisplayHtml(MimeMessage message, bool leftAlign = false)
    {
        string body;
        bool isHtml = message.HtmlBody != null;
        if (isHtml)
        {
            body = message.HtmlBody!;
            body = ScriptStyleTagOnlyScriptRegex().Replace(body, "");
            body = EventAttrRegex().Replace(body, " ");
            body = ReplaceCidImages(message, body);
            if (leftAlign) body = MakeLeftAligned(body);
        }
        else
        {
            var text = message.TextBody ?? "";
            body = "<pre style=\"white-space:pre-wrap;word-wrap:break-word;font-family:'Malgun Gothic',Segoe UI,sans-serif;font-size:10pt;margin:0\">" +
                   Linkify(WebUtility.HtmlEncode(text)) + "</pre>";
        }

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">");
        sb.Append("<style>body{font-family:'Malgun Gothic','Segoe UI',sans-serif;font-size:10pt;margin:8px;}img{max-width:100%;}</style>");
        sb.Append("</head><body>");
        sb.Append(body);
        // 메일 자체의 <style> 보다 뒤에 두어 우선 적용
        if (isHtml && leftAlign) sb.Append(LeftAlignCss);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    // ---------- 왼쪽 정렬 ----------
    // HTML 메일(특히 뉴스레터)은 고정 폭 틀을 <center>, align="center", margin: 0 auto 로 가운데 배치합니다.
    // 블록(틀) 배치만 왼쪽으로 바꾸고, 셀 안의 글자 정렬(버튼 글자 등)은 그대로 둡니다.

    private const string LeftAlignCss =
        "<style>" +
        "td[align=center]>table,th[align=center]>table,td[align=center]>div,th[align=center]>div," +
        "div[align=center]>table,div[align=center]>div" +
        "{margin-left:0 !important;margin-right:auto !important;}" +
        "</style>";

    private static string MakeLeftAligned(string html)
    {
        // <center> → <div> (안쪽 글자 정렬 상속을 끊음)
        html = CenterOpenRegex().Replace(html, "<div");
        html = CenterCloseRegex().Replace(html, "</div>");
        // <table|div align="center"> → left
        html = BlockAlignCenterRegex().Replace(html, m => m.Value.Replace(m.Groups["attr"].Value, "align=\"left\""));
        // 표만 담은 셀의 align="center" → left (셀 바로 안의 표가 가운데로 배치되는 것을 막음)
        html = CellCenteringTableRegex().Replace(html, m => m.Value.Replace(m.Groups["attr"].Value, "align=\"left\""));
        // margin: 0 auto / margin-left:auto 등 (style 속성과 <style> 블록 모두)
        html = MarginRegex().Replace(html, m => m.Groups["name"].Value + AutoWordRegex().Replace(m.Groups["value"].Value, "0"));
        return html;
    }

    [GeneratedRegex(@"<center\b", RegexOptions.IgnoreCase)]
    private static partial Regex CenterOpenRegex();

    [GeneratedRegex(@"</center\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CenterCloseRegex();

    [GeneratedRegex(@"<(table|div)\b[^>]*?(?<attr>\balign\s*=\s*[""']?center[""']?)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockAlignCenterRegex();

    [GeneratedRegex(@"<t[dh]\b[^>]*?(?<attr>\balign\s*=\s*[""']?center[""']?)[^>]*>(?=\s*(<!--[\s\S]*?-->\s*)*<(table|div|center)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex CellCenteringTableRegex();

    [GeneratedRegex(@"(?<name>margin(-left|-right)?\s*:\s*)(?<value>[^;""'}<>]*)", RegexOptions.IgnoreCase)]
    private static partial Regex MarginRegex();

    [GeneratedRegex(@"\bauto\b", RegexOptions.IgnoreCase)]
    private static partial Regex AutoWordRegex();

    private static string ReplaceCidImages(MimeMessage message, string html)
    {
        var parts = message.BodyParts.OfType<MimePart>()
            .Where(p => !string.IsNullOrEmpty(p.ContentId))
            .ToList();
        if (parts.Count == 0) return html;

        foreach (var part in parts)
        {
            try
            {
                using var ms = new MemoryStream();
                if (part.Content == null) continue;
                part.Content.DecodeTo(ms);
                var dataUri = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(ms.ToArray())}";
                html = html.Replace("cid:" + part.ContentId, dataUri, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Error("cid image", ex);
            }
        }
        return html;
    }

    private static string Linkify(string encodedText) =>
        UrlRegex().Replace(encodedText, m => $"<a href=\"{m.Value}\">{m.Value}</a>");

    [GeneratedRegex(@"<head[\s\S]*?</head>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadRegex();

    [GeneratedRegex(@"<(script|style)[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<script[\s\S]*?</script\s*>|<script[^>]*/>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleTagOnlyScriptRegex();

    [GeneratedRegex(@"\son[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttrRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();

    [GeneratedRegex(@"</(p|div|tr|li|h[1-6]|table|blockquote)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"(\s*\n){3,}")]
    private static partial Regex MultiBlankRegex();

    [GeneratedRegex(@"https?://[^\s<>&""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}

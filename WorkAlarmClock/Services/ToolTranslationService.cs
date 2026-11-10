using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WorkAlarmClock.Services;

public static class ToolTranslationService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(7) };
    public static async Task<(string Text, string Provider)> TranslateAsync(string input, string target)
    {
        if (Encoding.UTF8.GetByteCount(input) > 500)
            throw new ArgumentException("一次最多翻译 500 字节，请缩短描述后重试。");
        string source = target == "en" ? "zh-CN" : "en";
        string reason;
        try
        {
            string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(input) + "&langpair=" + Uri.EscapeDataString(source + "|" + target);
            using var response = await Client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.GetProperty("responseStatus").ToString() != "200")
                throw new InvalidOperationException(json.RootElement.GetProperty("responseDetails").GetString());
            string text = WebUtility.HtmlDecode(json.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "");
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("翻译服务返回空结果");
            return (text, "MyMemory");
        }
        catch (Exception ex) { reason = ex is TaskCanceledException ? "主服务超时" : ex.Message; }
        try
        {
            using var response = await Client.GetAsync("https://translate.googleapis.com/translate_a/single?client=gtx&sl=" + source + "&tl=" + target + "&dt=t&q=" + Uri.EscapeDataString(input));
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string text = string.Concat(json.RootElement[0].EnumerateArray().Select(part => part[0].GetString()));
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("备用服务返回空结果");
            return (text, "Google 备用服务");
        }
        catch { throw new InvalidOperationException("在线翻译暂不可用：" + reason + "。备用服务也未连接，请稍后重试；英文命名转换仍可离线使用。"); }
    }
}

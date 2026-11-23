using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace PublishStudio.Core;

/// <summary>
/// 查 GitHub Releases 上的最新版本，用来在「关于」窗口里提示有没有更新。
/// </summary>
/// <remarks>
/// 只读 GitHub 公开接口、不需要 token。未认证调用按 IP 限每小时 60 次，手动点几下用不完。
/// 网络不通、仓库改名、被限流、返回的不是 JSON（比如公司网络劫持）都当「查不到」处理，
/// 不让「关于」窗口因为网络问题卡住或弹错误框。
/// </remarks>
public static class UpdateChecker
{
    /// <summary>latest 接口会自动跳过草稿和预发布版本，拿到的就是最新正式版。</summary>
    private const string LatestApi = "https://api.github.com/repos/XCHLX/PublishStudio/releases/latest";

    /// <summary>发布列表页。没查到具体版本时，点「关于」里的版本号就打开这里。</summary>
    public const string ReleasesUrl = "https://github.com/XCHLX/PublishStudio/releases";

    /// <summary>共享 HttpClient（静态复用，避免反复建连接）。8 秒够慢网，又不至于让按钮转太久。</summary>
    private static readonly HttpClient Http = CreateClient();

    /// <summary>建一个带默认请求头的客户端。</summary>
    /// <returns>配置好的 <see cref="HttpClient"/>。</returns>
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        // GitHub 接口强制要求 User-Agent，缺了直接返回 403。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PublishStudio-UpdateChecker");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return client;
    }

    /// <summary>查询最新发布版本。</summary>
    /// <param name="current">当前版本，形如 <c>0.0.6</c>（不带前缀 V）。</param>
    /// <returns>成功时返回最新版本信息；网络失败、状态码异常、解析不了都返回 <c>null</c>。</returns>
    public static async Task<UpdateResult?> CheckAsync(string current)
    {
        try
        {
            using var response = await Http.GetAsync(LatestApi).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 = 仓库不可见或还没有正式发布；403 = 被限流。
                Debug.WriteLine($"[UpdateChecker] HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var latest = Text(doc.RootElement, "tag_name").TrimStart('v', 'V');
            var url = Text(doc.RootElement, "html_url");

            if (latest.Length == 0) return null;

            return new UpdateResult(latest, url, IsNewer(latest, current));
        }
        catch (Exception ex)
        {
            // 超时、DNS 失败、被中间设备改写返回体都在这里收口。
            Debug.WriteLine($"[UpdateChecker] 检查失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>读一个字符串字段，字段不存在或不是字符串时给空串。</summary>
    /// <param name="root">JSON 根节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>字段值，取不到时为空串。</returns>
    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>判断最新版本是否比当前版本新。</summary>
    /// <param name="latest">线上最新版本号。</param>
    /// <param name="current">当前运行版本号。</param>
    /// <returns>两个都能解析且 latest 更大时为 <c>true</c>。</returns>
    private static bool IsNewer(string latest, string current)
    {
        var left = Parse(latest);
        var right = Parse(current);

        return left is not null && right is not null && left > right;
    }

    /// <summary>把 <c>0.0.6</c> / <c>0.0.6.0</c> / <c>0.0.6-beta</c> 统一成四段版本号再比较。</summary>
    /// <param name="text">版本文本，可带 <c>v</c> 前缀之外的后缀。</param>
    /// <returns>补齐成四段的版本号；解析失败时为 <c>null</c>。</returns>
    private static Version? Parse(string text)
    {
        // 去掉 -beta / +build 这类后缀，只留数字部分。
        var core = text.Split('-', '+')[0].Trim();
        if (!Version.TryParse(core, out var version)) return null;

        // 补齐成四段，避免 0.0.6 与 0.0.6.0 被当成不同版本而误报「有新版本」。
        return new Version(version.Major, version.Minor,
            Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    }
}

/// <summary>一次版本检查的结果。</summary>
/// <param name="Latest">最新版本号，已去掉前缀 V，例如 <c>0.0.6</c>。</param>
/// <param name="ReleaseUrl">该版本在 GitHub 上的发布页地址。</param>
/// <param name="HasUpdate">最新版本是否比当前运行版本新。</param>
public sealed record UpdateResult(string Latest, string ReleaseUrl, bool HasUpdate);

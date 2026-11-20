using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PublishStudio.Core;

/// <summary>
/// 内务府（NWF）接口封装。参考自同公司的 ProjectManager 项目，
/// 目前用到三个：登录校验、取发布信息（GetIisManager）、发布完成回调登记。
/// 注意：接口声明的 Content-Type 是 application/json-patch+json，写错服务端直接 415。
/// </summary>
public sealed class NwfApi
{
    /// <summary>接口要求的 Content-Type。</summary>
    private const string JsonContentType = "application/json-patch+json";

    /// <summary>全局共享的 HttpClient（静态复用，避免端口耗尽）。超时 30 秒，别让界面无限等。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>接口根地址，结尾的 '/' 已在构造时去掉。</summary>
    public string BaseUrl { get; }

    /// <summary>用「设置 → 设置发布配置」里的接口地址构造。</summary>
    /// <param name="baseUrl">形如 http://your-server:8099 的根地址。</param>
    public NwfApi(string baseUrl) => BaseUrl = (baseUrl ?? "").Trim().TrimEnd('/');

    /// <summary>
    /// 取用户的授权菜单，返回其中 WebManage 类条目的数量（0 表示无权限）。
    /// 登录就是靠它判定的：数大于 0 才算有权限。
    /// </summary>
    /// <param name="userId">内务府账号名。</param>
    public async Task<int> GetWebManageCountAsync(string userId)
    {
        var node = await PostAsync("/api/NwfAuth/GetUserNwfAuthMenu", new { userid = userId })
            .ConfigureAwait(false);
        if (node?["result"] is not JsonArray menu) return 0;

        var count = 0;
        foreach (var item in menu)
        {
            var component = item?["component"]?.GetValue<string>() ?? "";
            if (component.Contains("WebManage", StringComparison.OrdinalIgnoreCase)) count++;
        }
        return count;
    }

    /// <summary>按发布 ID 取发布信息，成功时返回 result 节点。</summary>
    /// <param name="releaseId">内务府发布 ID（项目里的 NwfReleaseId）。</param>
    /// <returns>发布信息节点（可反序列化成 <see cref="Models.NwfRelease"/>）；失败时返回 null。</returns>
    public async Task<JsonNode?> GetIisManagerAsync(string releaseId)
    {
        var node = await PostAsync("/api/ProjectManage/GetIisManager", new { id = releaseId })
            .ConfigureAwait(false);
        return node?["result"];
    }

    /// <summary>登记一次发布记录（触发内务府去拉 OSS 上的包）。</summary>
    /// <param name="ipAddress">目标服务器 IP，取发布信息里的 IpAddress。</param>
    /// <param name="releaseTime">发布时间，格式 yyyy-MM-dd HH:mm:ss。</param>
    /// <param name="releaseId">内务府发布 ID。</param>
    /// <param name="downloadLink">OSS 上的包下载地址。</param>
    /// <param name="founder">发起人（当前登录账号）。</param>
    /// <param name="notes">备注，通常是 "机器名--时间发布"。</param>
    /// <param name="isDefaultConfig">是否使用默认配置，默认 true。</param>
    /// <returns>服务端返回了可解析的 JSON 即视为成功。</returns>
    public async Task<bool> AddIisManagerItemAsync(
        string ipAddress,
        string releaseTime,
        string releaseId,
        string downloadLink,
        string? founder,
        string notes,
        bool isDefaultConfig = true)
    {
        var node = await PostAsync("/api/ProjectManage/AddIisManagerItem", new
        {
            ipaddress = ipAddress,
            releaseTime,
            pid = releaseId,
            downloadLink,
            founder,
            notes,
            isDefaultConfig,
        }).ConfigureAwait(false);

        return node is not null;
    }

    /// <summary>
    /// 统一的 POST：序列化 payload → 发请求 → 响应体解析成 JsonNode。
    /// 响应不是合法 JSON（例如网关返回 HTML 错误页）时返回 null，由调用方按「取不到结果」处理。
    /// </summary>
    /// <param name="relativeUrl">相对于 <see cref="BaseUrl"/> 的接口路径，例如 <c>/api/ProjectManage/GetIisManager</c>。</param>
    /// <param name="payload">请求体对象，匿名对象即可，字段名要与接口约定一致。</param>
    /// <returns>响应体解析出的 JSON 节点；响应不是合法 JSON 时返回 null。</returns>
    private async Task<JsonNode?> PostAsync(string relativeUrl, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, JsonContentType);
        using var response = await Http.PostAsync(BaseUrl + relativeUrl, content).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

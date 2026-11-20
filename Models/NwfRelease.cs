using System.Text.Json.Serialization;

namespace PublishStudio.Models;

/// <summary>
/// 内务府发布信息（/api/ProjectManage/GetIisManager 返回的 result）。
/// 只声明发版用得到的字段，其余字段接口里还有（fileName、webHook 等），用不上就不接。
/// </summary>
public class NwfRelease
{
    /// <summary>发布记录 ID，与 <see cref="Project.NwfReleaseId"/> 同一个值。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>发布名称（如「猎粉_wechatApi」）。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>目标服务器 IP，回调内务府时作为参数传回去。</summary>
    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    /// <summary>IIS 站点名，决定包在 OSS 上的存放路径：NWF/Project/{IisSiteName}/{时间戳}.zip。</summary>
    [JsonPropertyName("iisSiteName")]
    public string? IisSiteName { get; set; }

    /// <summary>
    /// 是否正式环境。两处依赖它：列表的「发布环境」列显示「正式 / 测试」；
    /// 发布打包前为 true 时弹「⚠️ 发布确认（危险操作）」二次确认。
    /// 用可空类型：接口没返回这个字段、或返回 null 时不会反序列化失败，一律按测试环境（false）处理。
    /// </summary>
    [JsonPropertyName("isProduce")]
    public bool? IsProduce { get; set; }
}

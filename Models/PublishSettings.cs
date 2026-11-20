namespace PublishStudio.Models;

/// <summary>
/// 全局发布配置（一份，不是每个项目一份）。序列化到
/// %LOCALAPPDATA%\PublishStudio\settings.json，字段名即表单上的可编辑项。
/// </summary>
public class PublishSettings
{
    /// <summary>后端接口地址，例如 https://api.example.com。</summary>
    public string ApiUrl { get; set; } = "";

    /// <summary>前端网页地址，例如 https://www.example.com。</summary>
    public string WebUrl { get; set; } = "";

    /// <summary>对象存储的 AccessKeyId。</summary>
    public string OssKey { get; set; } = "";

    /// <summary>对象存储的 AccessKeySecret。</summary>
    public string OssSecret { get; set; } = "";

    /// <summary>
    /// 临时编译目录（%TEMP%\PublishStudio\build）里保留几天。0 表示「不保留」历史目录。
    /// 超过这个天数的目录会在程序启动时、以及每次发布打包成功后删掉。
    /// </summary>
    public int KeepBuildDays { get; set; } = DefaultKeepDays;

    /// <summary>默认保留天数。</summary>
    public const int DefaultKeepDays = 3;

    /// <summary>
    /// 双击项目列表时执行什么，取值见 <see cref="DoubleClickActions"/>。
    /// 默认「运行」：双击就把项目跑起来最符合直觉。
    /// </summary>
    public string DoubleClickAction { get; set; } = DoubleClickActions.Run;

    /// <summary>
    /// 登录成功的内务府账号名。由登录窗口写入，配置表单里不显示；
    /// 为空表示未登录。发版场景以后要当 founder 上报，所以存下来而不是只放内存。
    /// </summary>
    public string UserName { get; set; } = "";

    /// <summary>
    /// 列表上次的排序列，存的是 <see cref="Project"/> 的属性名（如 <c>Name</c>）。
    /// 空表示还没排过，按项目名称升序。
    /// </summary>
    public string SortColumn { get; set; } = "";

    /// <summary>上次的排序方向：false 升序，true 降序。</summary>
    public bool SortDescending { get; set; }

    // ---------- 主窗口位置与大小（0 表示还没存过） ----------

    /// <summary>主窗口左边界。</summary>
    public double WindowLeft { get; set; }

    /// <summary>主窗口上边界。</summary>
    public double WindowTop { get; set; }

    /// <summary>主窗口宽度。</summary>
    public double WindowWidth { get; set; }

    /// <summary>主窗口高度。</summary>
    public double WindowHeight { get; set; }

    /// <summary>上次是不是最大化状态。最大化时上面四个值存的是「还原后」的矩形，取消最大化不会变回一小块。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>复制一份，供设置对话框里改完再确认，取消时不污染原对象。</summary>
    /// <returns>字段完全一致的新实例。</returns>
    public PublishSettings Clone() => new()
    {
        ApiUrl = ApiUrl,
        WebUrl = WebUrl,
        OssKey = OssKey,
        OssSecret = OssSecret,
        UserName = UserName,
        KeepBuildDays = KeepBuildDays,
        DoubleClickAction = DoubleClickAction,
        SortColumn = SortColumn,
        SortDescending = SortDescending,
        WindowLeft = WindowLeft,
        WindowTop = WindowTop,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        WindowMaximized = WindowMaximized,
    };

    /// <summary>把另一个实例的字段拷回来（设置对话框确认时用）。</summary>
    /// <param name="other">用户在对话框里改好的那份副本。</param>
    public void CopyFrom(PublishSettings other)
    {
        ApiUrl = other.ApiUrl;
        WebUrl = other.WebUrl;
        OssKey = other.OssKey;
        OssSecret = other.OssSecret;
        UserName = other.UserName;
        KeepBuildDays = other.KeepBuildDays;
        DoubleClickAction = other.DoubleClickAction;
        SortColumn = other.SortColumn;
        SortDescending = other.SortDescending;
        WindowLeft = other.WindowLeft;
        WindowTop = other.WindowTop;
        WindowWidth = other.WindowWidth;
        WindowHeight = other.WindowHeight;
        WindowMaximized = other.WindowMaximized;
    }
}

/// <summary>
/// 双击项目列表可以触发的动作。存的就是列表下拉里的中文文案，
/// 这样 settings.json 打开来也能直接读懂，不用查枚举值。
/// </summary>
public static class DoubleClickActions
{
    /// <summary>在系统控制台里跑开发命令（dotnet watch run / npm run dev）。</summary>
    public const string Run = "运行";

    /// <summary>完整发布：编译 → 打包 → 上传 OSS → 调内务府接口。</summary>
    public const string Publish = "发布";

    /// <summary>只编译（和右键「仅编译」、F5 一样）。</summary>
    public const string Build = "编译";

    /// <summary>设置里下拉选项的顺序。</summary>
    public static readonly string[] All = { Run, Publish, Build };
}

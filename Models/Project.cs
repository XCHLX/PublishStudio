using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using PublishStudio.Core;

namespace PublishStudio.Models;

/// <summary>项目类型常量，决定默认构建命令与产物目录的推断方式。</summary>
public static class ProjectTypes
{
    /// <summary>.NET WebAPI / 控制台等项目，走 dotnet publish。</summary>
    public const string Api = "Api";

    /// <summary>Node.js 项目，走 npm run build 之类的脚本，产物在 dist。</summary>
    public const string Node = "Node.js";

    /// <summary>完全自定义命令，产物目录必须手填。</summary>
    public const string Custom = "Custom";

    /// <summary>下拉框里的全部类型，顺序即显示顺序。</summary>
    public static readonly string[] All = { Api, Node, Custom };
}

/// <summary>「环境」取值。Api 类型会用它切 appsettings 并透传 -p:EnvironmentName。</summary>
public static class Environments
{
    /// <summary>不切换环境：保留产物目录里原有的 appsettings.json。</summary>
    public const string None = "None";

    /// <summary>下拉框里的全部环境，顺序即显示顺序。</summary>
    public static readonly string[] All = { None, "Development", "Production", "Staging" };
}

/// <summary>
/// 一个可发布的项目。序列化到 %LOCALAPPDATA%\PublishStudio\projects.json，
/// 字段名即界面上的可编辑项，没有隐藏状态。
/// </summary>
public class Project : INotifyPropertyChanged
{
    /// <summary>本地唯一标识。任务互斥、托盘菜单项都靠它，不依赖可重名的 Name。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名。添加时默认取文件夹名。</summary>
    public string Name { get; set; } = "";

    private string _groupName = "";

    /// <summary>
    /// 自定义分组名，空表示未分组。列表的「分组」列、筛选栏的分组下拉、托盘的一级菜单都读它。
    /// 不做单独的「分组管理」，分组名就是项目上的一个字符串 —— 在项目对话框里直接输入新名字
    /// 就是建了新分组，最后一个属于它的项目改走后它自然消失。
    /// </summary>
    public string GroupName
    {
        get => _groupName;
        set
        {
            var name = value ?? "";
            if (_groupName == name) return;

            _groupName = name;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GroupText));
            OnPropertyChanged(nameof(GroupColorHex));
        }
    }

    /// <summary>列表里显示的分组文字：没分组显示灰色破折号，排序时仍然按空串算（永远沉底）。</summary>
    [JsonIgnore]
    public string GroupText => _groupName.Trim().Length == 0 ? "—" : _groupName.Trim();

    /// <summary>分组色点的颜色，形如 <c>#15803D</c>；未分组是固定灰，列表照样画一个灰点。</summary>
    [JsonIgnore]
    public string GroupColorHex => GroupPalette.For(_groupName);

    /// <summary>项目根目录（或 .csproj / package.json 所在目录）。</summary>
    public string Path { get; set; } = "";

    /// <summary>项目类型，取值见 <see cref="ProjectTypes"/>。</summary>
    public string Type { get; set; } = ProjectTypes.Api;

    /// <summary>
    /// 构建命令。留空则按类型生成默认命令（Api 走 dotnet publish，Node 走 npm run build）。
    /// 填了就完全用填的，程序不插手。
    /// </summary>
    public string BuildCommand { get; set; } = "";

    /// <summary>
    /// 产物目录。留空则自动推断：Api 用临时编译目录，Node 用项目下的 dist。
    /// 支持 {work} / {folder} / {stamp} 占位符和 %TEMP% 这类环境变量。
    /// </summary>
    public string OutputDir { get; set; } = "";

    /// <summary>
    /// 新建项目时「产物目录」预填的默认值。
    /// 用它而不是留空，是为了每次发布各自落一个临时目录，历史包不会互相覆盖，
    /// 也不会把 publish-zip 堆进项目目录（Node.js 填了产物目录会把 dist 同步过来再打包）。
    /// </summary>
    public const string DefaultOutputDir = @"%TEMP%\PublishStudio\build\{folder}-{stamp}";

    /// <summary>发布后置命令，一行一条，按顺序在项目目录执行。失败即中断。</summary>
    public string PostCommands { get; set; } = "";

    /// <summary>
    /// 本地运行命令。留空则按类型用默认命令（Api 走 dotnet watch run，Node 走 npm run dev）。
    /// 只在「运行」动作里用到：在一个独立控制台窗口里执行，产物不打包、不上传。
    /// </summary>
    public string RunCommand { get; set; } = "";

    /// <summary>环境名，见 <see cref="Environments"/>。仅 Api 类型生效。</summary>
    public string Environment { get; set; } = Environments.None;

    /// <summary>
    /// Node 版本（仅 Node.js 生效），可以写 <c>18</c> / <c>18.20</c> / <c>18.20.8</c>。
    /// 留空就用系统 PATH 里的 node。构建和「运行」时会把 nvm 里对应版本的目录插到子进程
    /// PATH 最前面，只影响这一次执行，不动系统的全局 node。
    /// </summary>
    public string NodeVersion { get; set; } = "";

    /// <summary>
    /// 发布前是否先执行一次 git pull（只对「发布」生效，「编译」和「运行」都不碰 git）。
    /// 拉取失败会中断发布 —— 宁可停一下，也不要用旧代码打包发上线。
    /// </summary>
    public bool GitPullBeforeRelease { get; set; }

    /// <summary>
    /// 内务府发布 ID。只有「发布打包」用到：拿它取发布信息（OSS 路径里的站点名、目标服务器 IP），
    /// 包传完后再用它回调登记发布记录。留空就只能编译，不能发版。
    /// </summary>
    public string NwfReleaseId { get; set; } = "";

    private NwfRelease? _release;

    /// <summary>
    /// 内务府发布信息（发布 ID 对应的名称、站点、目标 IP、是否正式环境）。
    /// 保存项目时顺手查一次存下来，列表的「发布环境」列和发布前的二次确认都读它；查不到就是 null。
    /// </summary>
    public NwfRelease? NwfRelease
    {
        get => _release;
        set
        {
            _release = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReleaseName));
            OnPropertyChanged(nameof(ReleaseEnvironment));
        }
    }

    /// <summary>列表的「发布名称」列：内务府那边的名称（如「猎粉_CahtSignalR」），没查到时为空。</summary>
    [JsonIgnore]
    public string ReleaseName => NwfRelease?.Name ?? "";

    /// <summary>列表的「发布环境」列：正式 / 测试 / 空（还没查到发布信息）。isProduce 缺失按测试算。</summary>
    [JsonIgnore]
    public string ReleaseEnvironment => NwfRelease is null ? "" : NwfRelease.IsProduce == true ? "正式" : "测试";

    // ---------- 运行时状态，不落盘 ----------

    private string _status = "";
    private int _elapsedSeconds;

    /// <summary>列表里那一列状态：空 / 编译中 / 发布中 / 成功 / 失败 / 已取消。</summary>
    [JsonIgnore]
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;

            // 新任务开始时把上一轮的耗时清掉，否则「发布中」后面还挂着上次的 83s。
            if (value is "编译中" or "发布中") ElapsedSeconds = 0;

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>上一次任务跑了多少秒，任务结束时写入。0 表示没有可显示的耗时（比如正在跑或从没跑过）。</summary>
    [JsonIgnore]
    public int ElapsedSeconds
    {
        get => _elapsedSeconds;
        set
        {
            if (_elapsedSeconds == value) return;
            _elapsedSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>
    /// 列表「状态」列真正显示的文字：跑完的结果后面带耗时，形如「成功 · 83s」。
    /// 正在跑（编译中 / 发布中）和清空状态时不带耗时。
    /// </summary>
    [JsonIgnore]
    public string StatusText
    {
        get
        {
            if (_elapsedSeconds <= 0 || _status is not ("成功" or "失败" or "已取消")) return _status;
            return $"{_status} · {_elapsedSeconds}s";
        }
    }

    // ---------- 发布记录，落盘 ----------

    private DateTime? _lastReleaseAt;

    /// <summary>
    /// 上一次「发布」（编译 + 打包 + 上传 OSS + 回调）成功的时间，只编译不算。
    /// null 表示这台机器还没发过这个项目。列表的「上次发布时间」列读它。
    /// </summary>
    public DateTime? LastReleaseAt
    {
        get => _lastReleaseAt;
        set
        {
            if (_lastReleaseAt == value) return;
            _lastReleaseAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastReleaseText));
            OnPropertyChanged(nameof(LastReleaseFullText));
        }
    }

    /// <summary>列表「上次发布时间」列显示的文字；没发过时为空（空串会沉到列表最底下）。</summary>
    [JsonIgnore]
    public string LastReleaseText => LastReleaseAt?.ToString("yyyy-MM-dd HH:mm") ?? "";

    /// <summary>鼠标悬停时显示的完整时间（带秒）。</summary>
    [JsonIgnore]
    public string LastReleaseFullText => LastReleaseAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "还没发布过";

    /// <summary>是否正在跑任务。托盘菜单和按钮的可用性靠它判断。</summary>
    [JsonIgnore]
    public bool IsBusy => Status is "编译中" or "发布中";

    /// <summary>属性变更通知，WPF 绑定靠它刷新列表里的状态、发布名称等列。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>触发属性变更通知，界面据此刷新绑定的列。</summary>
    /// <param name="name">变更的属性名；默认由编译器按调用方成员名自动填入。</param>
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>按类型给出默认构建命令，界面上作为占位提示和预填值。</summary>
    /// <param name="type">项目类型，取值见 <see cref="ProjectTypes"/>。</param>
    /// <returns>默认构建命令；自定义类型没有默认值，返回空串。</returns>
    public static string DefaultBuildCommand(string type) => type switch
    {
        ProjectTypes.Node => "npm run build",
        ProjectTypes.Api => "dotnet publish -c Release -o \"{output}\"（实际会带上 csproj 名）",
        _ => "",
    };

    /// <summary>按类型给出默认运行命令。「运行」动作留空时用它。</summary>
    /// <param name="type">项目类型，取值见 <see cref="ProjectTypes"/>。</param>
    /// <returns>默认运行命令；自定义类型没有默认值，返回空串。</returns>
    public static string DefaultRunCommand(string type) => type switch
    {
        ProjectTypes.Node => "npm run dev",
        ProjectTypes.Api => "dotnet watch run",
        _ => "",
    };

    /// <summary>实际要执行的运行命令：填了用填的，没填按类型取默认。</summary>
    [JsonIgnore]
    public string EffectiveRunCommand =>
        string.IsNullOrWhiteSpace(RunCommand) ? DefaultRunCommand(Type) : RunCommand.Trim();

    /// <summary>复制一份，供编辑对话框里改完再确认，取消时不污染原对象。</summary>
    /// <returns>字段完全一致的新实例（含缓存的发布信息，运行时状态不带）。</returns>
    public Project Clone() => new()
    {
        Id = Id,
        Name = Name,
        GroupName = GroupName,
        Path = Path,
        Type = Type,
        BuildCommand = BuildCommand,
        OutputDir = OutputDir,
        PostCommands = PostCommands,
        RunCommand = RunCommand,
        Environment = Environment,
        NodeVersion = NodeVersion,
        GitPullBeforeRelease = GitPullBeforeRelease,
        NwfReleaseId = NwfReleaseId,
        NwfRelease = NwfRelease,
        LastReleaseAt = LastReleaseAt,
    };

    /// <summary>把另一个实例的可编辑字段拷回来（编辑确认时用）。Id 与运行时状态不动。</summary>
    /// <param name="other">用户在对话框里改好的那份副本。</param>
    public void CopyFrom(Project other)
    {
        Name = other.Name;
        GroupName = other.GroupName;
        Path = other.Path;
        Type = other.Type;
        BuildCommand = other.BuildCommand;
        OutputDir = other.OutputDir;
        PostCommands = other.PostCommands;
        RunCommand = other.RunCommand;
        Environment = other.Environment;
        NodeVersion = other.NodeVersion;
        GitPullBeforeRelease = other.GitPullBeforeRelease;
        NwfReleaseId = other.NwfReleaseId;
        NwfRelease = other.NwfRelease;
        LastReleaseAt = other.LastReleaseAt;
    }
}

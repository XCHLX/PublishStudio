using PublishStudio.Models;

namespace PublishStudio.Core;

/// <summary>
/// 预检结论的一条记录。<see cref="Level"/> 取值见 <see cref="Preflight"/> 里的三个常量：
/// ok 通过、warn 提醒（不挡发布）、fail 有问题（继续也大概率失败）。
/// </summary>
/// <param name="Label">检查项名称，显示在左边那列。</param>
/// <param name="Level">ok / warn / fail。</param>
/// <param name="Detail">这一项的实际情况或问题说明。</param>
public sealed record PreflightItem(string Label, string Level, string Detail)
{
    /// <summary>左边那个小图标，按结论换。</summary>
    public string Icon => Level switch
    {
        Preflight.Ok => "✓",
        Preflight.Warn => "!",
        _ => "✕",
    };
}

/// <summary>
/// 发布前的本地体检：把「跑一半才报错」的那些条件提前挨个问一遍。
/// 只看本机能立刻判断的东西（目录、文件、配置项），不发网络请求，所以是毫秒级、可以每次发布前都跑。
/// 结论只做提示，不替用户做决定 —— 拦下来的按钮仍然是「仍然发布」。
/// </summary>
public static class Preflight
{
    /// <summary>通过。</summary>
    public const string Ok = "ok";

    /// <summary>能跑，但建议看一眼（不挡发布）。</summary>
    public const string Warn = "warn";

    /// <summary>缺东西，继续也大概率失败。</summary>
    public const string Fail = "fail";

    /// <summary>
    /// 跑一遍检查。顺序就是对话框里从上到下的顺序：
    /// 目录 → 构建命令 → 产物目录 → Node 版本 → 发布 ID → 发布环境 → 接口 → OSS → 登录。
    /// </summary>
    /// <param name="project">要发布的项目。</param>
    /// <param name="settings">全局发布配置（接口地址、OSS 凭据、登录账号）。</param>
    /// <returns>按顺序排好的检查结果，界面直接绑它。</returns>
    public static List<PreflightItem> Check(Project project, PublishSettings settings)
    {
        var items = new List<PreflightItem>();

        var workDir = AppStore.ResolveFolder(project.Path);
        var folder = workDir.Length > 0 ? new DirectoryInfo(workDir).Name : "";
        var dirExists = workDir.Length > 0 && Directory.Exists(workDir);

        // ---------- 1. 项目目录 ----------
        if (workDir.Length == 0)
            items.Add(new("项目目录", Fail, "没填项目目录"));
        else if (!dirExists)
            items.Add(new("项目目录", Fail, $"不存在：{workDir}"));
        else
            items.Add(new("项目目录", Ok, workDir));

        // ---------- 2. 构建命令 ----------
        items.Add(CheckBuildCommand(project, workDir, dirExists));

        // ---------- 3. 产物目录 ----------
        items.Add(CheckOutputDir(project, workDir, folder));

        // ---------- 4. Node 版本（只对 Node 项目且填了版本时检查） ----------
        if (project.Type == ProjectTypes.Node && !string.IsNullOrWhiteSpace(project.NodeVersion))
        {
            items.Add(NodeRuntime.TryResolve(project.NodeVersion, out var nodePath, out var error)
                ? new("Node 版本", Ok, $"{System.IO.Path.GetFileName(nodePath)}（{nodePath}）")
                : new("Node 版本", Fail, error));
        }

        // ---------- 5. 内务府发布 ID ----------
        items.Add(string.IsNullOrWhiteSpace(project.NwfReleaseId)
            ? new("发布 ID", Fail, "没填，只能编译不能发版（右键「编辑…」补上）")
            : new("发布 ID", Ok, project.NwfReleaseId));

        // ---------- 6. 发布环境 ----------
        if (project.NwfRelease is not { } release)
        {
            items.Add(new("发布环境", Warn, "本地还没有发布信息，发布会先查一次（网络不通就会失败）"));
        }
        else
        {
            var environment = release.IsProduce == true ? "正式 💥" : "测试";
            items.Add(new("发布环境", release.IsProduce == true ? Warn : Ok,
                $"{environment}　站点 {release.IisSiteName ?? "None"}　IP {release.IpAddress}"));
        }

        // ---------- 7. 自动 git pull（项目设置里勾了才查） ----------
        if (project.GitPullBeforeRelease) items.Add(CheckGitPull(workDir, dirExists));

        // ---------- 8~10. 全局配置 ----------
        items.Add(string.IsNullOrWhiteSpace(settings.ApiUrl)
            ? new("接口地址", Fail, "没配置，去「设置 → 发布配置」填")
            : new("接口地址", Ok, Secret.Mask(settings.ApiUrl)));

        items.Add(string.IsNullOrWhiteSpace(settings.OssKey) || string.IsNullOrWhiteSpace(settings.OssSecret)
            ? new("OSS 凭据", Fail, "OSS Key / 密钥没填全，去「设置 → 发布配置」填")
            : new("OSS 凭据", Ok, "Key 和密钥都已填写"));

        items.Add(string.IsNullOrWhiteSpace(settings.UserName)
            ? new("登录账号", Fail, "还没登录，点右上角「登录」")
            : new("登录账号", Ok, settings.UserName));

        return items;
    }

    /// <summary>
    /// 构建命令这一项：填了就算过（内容对不对只能跑起来才知道），
    /// 没填就看类型的默认命令能不能用 —— Api 靠 .csproj、Node 靠 package.json。
    /// </summary>
    /// <param name="project">要检查的项目。</param>
    /// <param name="workDir">项目目录。</param>
    /// <param name="dirExists">项目目录是否存在，不存在就不再往下查文件。</param>
    /// <returns>一条检查结果。</returns>
    private static PreflightItem CheckBuildCommand(Project project, string workDir, bool dirExists)
    {
        var custom = project.BuildCommand?.Trim() ?? "";
        if (custom.Length > 0) return new("构建命令", Ok, custom);

        if (!dirExists) return new("构建命令", Warn, "项目目录不存在，暂时判断不了默认命令是否可用");

        switch (project.Type)
        {
            case ProjectTypes.Api:
                var count = CountCsproj(workDir);
                return count switch
                {
                    0 => new("构建命令", Fail, "目录下没有 .csproj，默认的 dotnet publish 找不到要发布的项目"),
                    > 1 => new("构建命令", Warn, $"目录下有 {count} 个 .csproj，默认命令可能挑错，建议把构建命令写全"),
                    _ => new("构建命令", Ok, "未填，用默认的 dotnet publish（.csproj 唯一）"),
                };

            case ProjectTypes.Node:
                return File.Exists(System.IO.Path.Combine(workDir, "package.json"))
                    ? new("构建命令", Ok, "未填，用默认的 npm run build")
                    : new("构建命令", Fail, "目录下没有 package.json，npm run build 会失败");

            default:
                return new("构建命令", Fail, "自定义类型必须自己填构建命令");
        }
    }

    /// <summary>
    /// 产物目录这一项：能解析出路径就算过，但要拦住「填成项目目录」这种打包完会清掉源码的配置。
    /// 判法与 <see cref="Publisher"/> 保持一致，免得预检说没事、跑起来又报错。
    /// </summary>
    /// <param name="project">要检查的项目。</param>
    /// <param name="workDir">项目目录。</param>
    /// <param name="folder">项目目录的文件夹名，组成默认临时目录用。</param>
    /// <returns>一条检查结果。</returns>
    private static PreflightItem CheckOutputDir(Project project, string workDir, string folder)
    {
        var raw = project.OutputDir?.Trim() ?? "";

        // 没填按类型推断：Api 落临时目录（每次一个新的），Node 用项目下的 dist，自定义必须填。
        if (raw.Length == 0)
        {
            return project.Type switch
            {
                ProjectTypes.Api => new("产物目录", Ok, $"{System.IO.Path.Combine(Publisher.TempRoot, "build", $"{folder}-<时间戳>")}（自动）"),
                ProjectTypes.Node => new("产物目录", Ok, $"{System.IO.Path.Combine(workDir, "dist")}（自动）"),
                _ => new("产物目录", Fail, "自定义类型必须填产物目录，否则不知道打包哪个目录"),
            };
        }

        // 和 Publisher 一样：先展开 %TEMP%，再替换占位符。{stamp} 在预检里没有真实时间戳，用文字代替。
        var resolved = Environment.ExpandEnvironmentVariables(raw)
            .Replace("{work}", workDir, StringComparison.OrdinalIgnoreCase)
            .Replace("{folder}", folder, StringComparison.OrdinalIgnoreCase)
            .Replace("{stamp}", "<时间戳>", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('\\', '/');

        if (resolved.Length == 0) return new("产物目录", Fail, "只填了占位符，解析出来是空的");

        if (workDir.Length == 0 || resolved.Contains('<')) return new("产物目录", Ok, resolved);

        // 产物目录落在项目目录里面时，打包完的清理会把源码一起删掉，必须先拦住。
        string target;
        string work;
        try
        {
            work = System.IO.Path.GetFullPath(workDir).TrimEnd('\\') + '\\';
            target = System.IO.Path.GetFullPath(resolved).TrimEnd('\\') + '\\';
        }
        catch (Exception)
        {
            // 路径里有非法字符之类，交给 Publisher 去报，这里只提醒一句。
            return new("产物目录", Warn, $"路径看着有问题：{resolved}");
        }

        if (target == work)
            return new("产物目录", Fail, $"不能是项目目录本身：{resolved}");

        if (project.Type == ProjectTypes.Node && target.StartsWith(work, StringComparison.OrdinalIgnoreCase))
            return new("产物目录", Fail, $"不能是项目目录的子目录：{resolved}");

        return new("产物目录", Ok, resolved);
    }

    /// <summary>
    /// 「发布前自动 git pull」这一项：找得到 git、目录是个 git 仓库，才算能拉。
    /// 勾了开关但这两条不成立时，发布会卡在第一步，所以先在这儿报出来。
    /// </summary>
    /// <param name="workDir">项目目录。</param>
    /// <param name="dirExists">项目目录是否存在。</param>
    /// <returns>一条检查结果。</returns>
    private static PreflightItem CheckGitPull(string workDir, bool dirExists)
    {
        if (GitPath.Find() is null)
        {
            return new("自动 git pull", Fail, "找不到 git.exe，发布会在第一步就失败（把 Git 加进 PATH，或关掉这个开关）");
        }

        if (!dirExists) return new("自动 git pull", Warn, "项目目录不存在，暂时判断不了是不是 git 仓库");

        // 项目常常是仓库里的一个子项目，所以从项目目录往上找 .git 才算数。
        var repoRoot = GitPath.FindRepositoryRoot(workDir);
        if (repoRoot is null)
            return new("自动 git pull", Fail, "项目目录和它的上级都不是 git 仓库，git pull 会失败（或关掉这个开关）");

        return string.Equals(repoRoot, workDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? new("自动 git pull", Ok, "已开启，发布前会先拉取最新代码")
            : new("自动 git pull", Ok, $"已开启，在子目录里拉取，仓库根：{repoRoot}");
    }

    /// <summary>数项目目录下有几个 .csproj（只看第一层），目录不存在时返回 0。</summary>
    /// <param name="workDir">项目目录。</param>
    /// <returns>.csproj 的个数；枚举失败按 0 算。</returns>
    private static int CountCsproj(string workDir)
    {
        try
        {
            return Directory.GetFiles(workDir, "*.csproj", SearchOption.TopDirectoryOnly).Length;
        }
        catch
        {
            return 0;
        }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PublishStudio.Models;

namespace PublishStudio.Core;

/// <summary>
/// 发布流水线。两条入口、同一套步骤：
///   BuildAsync    编译 → 切环境配置 → 写 version.txt → 打开产物目录
///   ReleaseAsync  编译 → 切环境配置 → 写 version.txt → 压缩 → 后置命令 → 上传 OSS → 回调内务府 → 清理产物目录
///
/// 步骤顺序固定，不用配置。要插自己的动作就填「后置命令」，一行一条。
/// </summary>
public static class Publisher
{
    /// <summary>临时编译产物的根目录（%TEMP%\PublishStudio）。</summary>
    public static string TempRoot { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PublishStudio");

    /// <summary>只编译，不打包，也不碰 OSS / 内务府。</summary>
    /// <param name="project">要编译的项目。</param>
    /// <param name="log">日志回调，流水线每一步都会往里写。</param>
    /// <param name="ct">取消令牌，用户点「停止」时中止并杀掉子进程。</param>
    /// <returns>全部步骤成功返回 true；任一步失败或取消返回 false。</returns>
    public static Task<bool> BuildAsync(Project project, Action<string> log, CancellationToken ct)
        => RunAsync(project, pack: false, settings: null, log, ct);

    /// <summary>
    /// 发布：编译 → 压缩 → 后置命令 → 上传 OSS → 回调内务府登记发布记录。
    /// 压不压缩不由用户选，一律压。
    /// </summary>
    /// <param name="project">要发布的项目，需要已填内务府发布 ID。</param>
    /// <param name="settings">全局发布配置（接口地址、OSS 凭据、登录账号、保留天数）。</param>
    /// <param name="log">日志回调，流水线每一步都会往里写。</param>
    /// <param name="ct">取消令牌，用户点「停止」时中止并杀掉子进程。</param>
    /// <returns>全部步骤成功返回 true；任一步失败或取消返回 false。</returns>
    public static Task<bool> ReleaseAsync(
        Project project, PublishSettings settings, Action<string> log, CancellationToken ct)
        => RunAsync(project, pack: true, settings, log, ct);

    /// <summary>
    /// 编译与发布共用的流水线本体，按 <paramref name="pack"/> 决定走到哪一步（发布才压缩、上传、回调、清理）。
    /// 所有异常都在这里兜住并写进日志，不往界面抛。
    /// </summary>
    /// <param name="project">要处理的项目。</param>
    /// <param name="pack">true 走完整发布；false 只编译。</param>
    /// <param name="settings">发布配置；只编译时可为 null。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true；失败、取消或配置不全返回 false。</returns>
    private static async Task<bool> RunAsync(
        Project project, bool pack, PublishSettings? settings, Action<string> log, CancellationToken ct)
    {
        var workDir = AppStore.ResolveFolder(project.Path);
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
        {
            log($"[错误] 项目目录不存在: {project.Path}");
            return false;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var folderName = new DirectoryInfo(workDir).Name;

        // 发版专用的两样东西，编译前就准备好：接口客户端 + 发布信息。
        // 提前取发布信息有两个好处：站点名决定 OSS 路径，ID 填错也能在编译前就报出来。
        NwfApi? api = null;
        NwfRelease? release = null;
        if (pack)
        {
            if (settings is null || !EnsureReleaseReady(project, settings, log)) return false;

            api = new NwfApi(settings.ApiUrl);
            try
            {
                release = (await api.GetIisManagerAsync(project.NwfReleaseId).ConfigureAwait(false))
                    ?.Deserialize<NwfRelease>();
            }
            catch (Exception ex)
            {
                log($"[错误] 取内务府发布信息失败: {ex.Message}");
                return false;
            }

            if (release is null)
            {
                log($"[错误] 取不到内务府发布信息（发布 ID: {project.NwfReleaseId}），请确认 ID 是否正确。");
                return false;
            }
            log($"[发版] {release.Name ?? folderName}　环境: {(release.IsProduce == true ? "正式 💥" : "测试")}　站点: {release.IisSiteName ?? "None"}　IP: {release.IpAddress}");
        }

        try
        {
            // ---------- 0. 可选的 git pull（只在「发布」时做，项目设置里勾了才跑） ----------
            if (pack && project.GitPullBeforeRelease
                && !await GitPullAsync(workDir, log, ct).ConfigureAwait(false)) return false;

            // ---------- 1. 定产物目录 ----------
            var outputDir = ResolveOutputDir(project, workDir, folderName, stamp, log);
            if (outputDir is null) return false;

            // ---------- 2. 跑构建命令 ----------
            var commandLine = ResolveBuildCommand(project, workDir, outputDir, log);
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                log("[错误] 构建命令为空。请到项目设置里填写，或改项目类型用默认命令。");
                return false;
            }

            // Node.js 项目可能指定了 nvm 版本：只把那个版本目录塞进这次子进程的 PATH，
            // 不调 nvm use（那是改全局 junction，要管理员权限，多项目并行还会互相踩）。
            if (!TryResolveNodePath(project, log, out var nodePath)) return false;

            log($"工作目录: {workDir}");
            log($"执行命令: {commandLine}");
            log("------------------------------------------------------------");

            var exitCode = await Task
                .Run(() => ProcessRunner.Run(commandLine, workDir, log, ct, nodePath), ct)
                .ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                log("[取消] 用户中止了任务");
                return false;
            }

            log("------------------------------------------------------------");
            if (exitCode != 0)
            {
                log($"[错误] 命令失败，退出码: {exitCode}");
                return false;
            }
            log("[完成] 构建命令执行成功");

            // ---------- 3. 产物目录校验 ----------
            // Node.js 的产物位置由项目自己的构建脚本决定（vite 就是 dist），填了别的目录它也不会往那写，
            // 所以这里先把 dist 同步过去，让「产物目录」真的生效。
            if (!StageNodeOutput(project, workDir, outputDir, log)) return false;

            if (!Directory.Exists(outputDir))
            {
                log($"[错误] 产物目录不存在: {outputDir}");
                log("       如果构建命令改过 -o / 输出路径，请到项目设置里把「产物目录」填对。");
                return false;
            }

            // ---------- 4. 环境配置与版本文件（仅 .NET） ----------
            if (project.Type == ProjectTypes.Api)
            {
                ApplyEnvironmentConfig(outputDir, project.Environment, log);
                WriteVersionFile(outputDir, folderName, project.Environment, log);
            }

            // ---------- 5. 压缩（固定落在产物目录下的 publish-zip，不进项目目录） ----------
            string? zipPath = null;

            zipPath = System.IO.Path.Combine(outputDir, "publish-zip", $"{folderName}-{stamp}.zip");
            log($"开始压缩: {outputDir} → {zipPath}");
            await Task.Run(() => ProcessRunner.ZipDirectory(outputDir, zipPath, log), ct).ConfigureAwait(false);
            if (File.Exists(zipPath))
            {
                var size = new FileInfo(zipPath).Length / 1024.0 / 1024.0;
                log($"[完成] 已生成压缩包: {zipPath}  ({size:F1} MB)");
            }
            else
            {
                log("[错误] 没有生成压缩包，产物目录是空的。请检查构建命令有没有真的产出文件、以及「产物目录」填得对不对。");
                return false;
            }

            // ---------- 6. 后置命令 ----------
            // 后置命令也可能调 npm / node，同样给同一份 PATH，保证版本一致。
            if (!await RunPostCommandsAsync(project, workDir, outputDir, log, ct, nodePath).ConfigureAwait(false)) return false;

            // ---------- 7. 上传 OSS + 回调内务府（只有发布才走） ----------
            if (pack && zipPath is not null && settings is not null && api is not null && release is not null)
            {
                if (!await PublishAsync(project, settings, api, release, zipPath, log, ct).ConfigureAwait(false))
                    return false;
            }

            // ---------- 8. 清理产物目录（只留 publish-zip 那一层） ----------
            if (zipPath is not null && File.Exists(zipPath)) CleanupOutput(outputDir, workDir, log);

            // ---------- 9. 清掉超过保留天数的历史临时目录（只发布时做，编译不动别人的） ----------
            if (pack) PurgeOldBuilds(settings?.KeepBuildDays ?? PublishSettings.DefaultKeepDays, log);

            // ---------- 10. 收尾 ----------
            var finalTarget = zipPath is not null ? System.IO.Path.GetDirectoryName(zipPath)! : outputDir;
            log(pack ? "[完成] 发布结束" : "[完成] 编译结束");
            log($"产物位置: {finalTarget}");

            if (!ct.IsCancellationRequested && pack == false) AppStore.OpenPath(finalTarget);
            return true;
        }
        catch (OperationCanceledException)
        {
            log("[取消] 用户中止了任务");
            return false;
        }
        catch (Exception ex)
        {
            log($"[错误] 执行异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 压缩成功、发版流程走完后，把产物目录里的编译产物删掉，只留 <c>publish-zip</c>
    /// （包在里面，是唯一还需要的东西），省掉几十上百 MB 的临时文件。
    ///
    /// 删的是「产物目录的直接子文件和子目录」，<c>publish-zip</c> 整个跳过 ——
    /// 所以同目录下的历史包不会被误删（Node.js 项目的 dist/publish-zip 会跨次累积）。
    /// </summary>
    /// <param name="outputDir">产物目录，其直接子文件和子目录会被删掉。</param>
    /// <param name="workDir">项目目录，用于兜底判断「产物目录是不是项目目录本身」。</param>
    /// <param name="log">日志回调，写清理结果或跳过原因。</param>
    private static void CleanupOutput(string outputDir, string workDir, Action<string> log)
    {
        const string keepDir = "publish-zip";

        // 兜底：产物目录要是被配成了项目目录（或它的上级），删下去就是把源码清了。
        var fullOutput = System.IO.Path.GetFullPath(outputDir).TrimEnd('\\') + '\\';
        var fullWork = System.IO.Path.GetFullPath(workDir).TrimEnd('\\') + '\\';
        if (fullWork.StartsWith(fullOutput, StringComparison.OrdinalIgnoreCase))
        {
            log($"[警告] 产物目录是项目目录本身或它的上级，跳过清理以免删掉源码: {outputDir}");
            return;
        }

        var removed = 0;
        long freed = 0;

        try
        {
            foreach (var file in Directory.GetFiles(outputDir))
            {
                var size = new FileInfo(file).Length;
                File.Delete(file);
                freed += size;
                removed++;
            }

            foreach (var dir in Directory.GetDirectories(outputDir))
            {
                if (string.Equals(System.IO.Path.GetFileName(dir), keepDir, StringComparison.OrdinalIgnoreCase)) continue;

                freed += DirectorySize(dir);
                Directory.Delete(dir, recursive: true);
                removed++;
            }
        }
        catch (Exception ex)
        {
            // 资源管理器里开着某个文件、权限不足都可能删不掉。清理失败不影响本次发布结果。
            log($"[警告] 清理产物目录没做完: {ex.Message}（已删 {removed} 项）");
            return;
        }

        log(removed == 0
            ? $"[清理] 产物目录没有多余文件，保留 {keepDir}"
            : $"[清理] 已删除 {removed} 项编译产物，释放 {freed / 1024.0 / 1024.0:F1} MB，保留 {keepDir}");
    }

    /// <summary>算目录总大小，失败就按 0 算（只用于日志里的「释放了多少」）。</summary>
    /// <param name="dir">要统计的目录。</param>
    /// <returns>目录下所有文件的字节数之和；枚举出错时返回 0。</returns>
    internal static long DirectorySize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 删掉 %TEMP%\PublishStudio\build 下超过保留天数的目录。
    /// 天数来自「设置 → 发布设置」里的「保留历史发布文件」，默认 3 天，0 表示不保留。
    /// </summary>
    /// <param name="keepDays">保留天数；0 表示「不保留」，只留最近 1 小时内的目录。</param>
    /// <param name="log">日志回调，删了东西才写一行，没删就静默。</param>
    private static void PurgeOldBuilds(int keepDays, Action<string> log)
    {
        var (removed, freed) = BuildHistory.Purge(keepDays);
        if (removed == 0) return;

        var freedText = $"{freed / 1024.0 / 1024.0:F1} MB";
        log(keepDays == 0
            ? $"[清理] 已删除 {removed} 个历史编译目录（设置为不保留），释放 {freedText}"
            : $"[清理] 已删除 {removed} 个超过 {keepDays} 天的历史编译目录，释放 {freedText}");
    }

    /// <summary>
    /// 「发布前自动 git pull」：在项目目录拉一次最新代码，拉不到就中断发布。
    ///
    /// 两处刻意的处理：
    /// 1. 关掉 git 的交互式账号密码提示（<c>GIT_TERMINAL_PROMPT=0</c>）——
    ///    否则仓库要凭据时 git 会一直等着输入，发布就卡在那儿不动，只能点「停止」。
    /// 2. 失败即中断，不继续用旧代码打包。要发的是线上包，宁可停下来让人看一眼。
    /// </summary>
    /// <param name="workDir">项目目录，git pull 在这里执行。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="ct">取消令牌，取消时杀掉 git 进程。</param>
    /// <returns>拉取成功（或用户取消后的 false）返回 true / false。</returns>
    private static async Task<bool> GitPullAsync(string workDir, Action<string> log, CancellationToken ct)
    {
        var git = GitPath.Find();
        if (git is null)
        {
            log("[错误] 没找到 git.exe，「发布前自动 git pull」无法执行。");
            log("       要么把 Git 加进系统 PATH，要么到项目设置里把这个开关关掉。");
            return false;
        }

        // 项目经常是仓库里的一个子项目（.git 在上层），git 会自己向上找仓库根。
        // 这里先确认一下，免得直接怼出一句 "not a git repository" 让人猜。
        var repoRoot = GitPath.FindRepositoryRoot(workDir);
        if (repoRoot is null)
        {
            log("[错误] 项目目录和它的上级都不是 git 仓库，没法执行「发布前自动 git pull」。");
            log("       要么把项目放进仓库，要么到项目设置里把这个开关关掉。");
            return false;
        }

        log("[git] 发布前拉取最新代码（项目设置里开启了「发布前自动 git pull」）");

        if (!string.Equals(repoRoot, workDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            log($"[git] 项目目录是仓库的子目录，实际在仓库根拉取: {repoRoot}");
        }
        log($"执行命令: \"{git}\" pull");

        var environment = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
        var exitCode = await Task
            .Run(() => ProcessRunner.Run(new[] { git, "pull" }, workDir, log, ct, environment: environment), ct)
            .ConfigureAwait(false);

        if (ct.IsCancellationRequested)
        {
            log("[取消] 用户中止了任务");
            return false;
        }

        if (exitCode != 0)
        {
            log($"[错误] git pull 失败，退出码: {exitCode}。发布已中断，不会用旧代码打包。");
            log("       常见原因：本地改动冲突、需要账号密码、当前目录不是 git 仓库。也可到项目设置里关掉这个开关。");
            return false;
        }

        log("[完成] 代码已更新到最新");
        return true;
    }

    /// <summary>
    /// 发版前的配置体检。缺什么就报什么，别让用户跑到上传那一步才发现密钥没填。
    /// </summary>
    /// <param name="project">要发布的项目，检查它有没有填内务府发布 ID。</param>
    /// <param name="settings">发布配置，检查接口地址、OSS 凭据、登录账号。</param>
    /// <param name="log">日志回调，缺哪项就往这里写一条错误。</param>
    /// <returns>配置齐全返回 true；缺任何一项返回 false。</returns>
    private static bool EnsureReleaseReady(Project project, PublishSettings settings, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiUrl))
        {
            log("[错误] 未配置内务府接口地址。请到「设置 → 设置发布配置」里填写。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.OssKey) || string.IsNullOrWhiteSpace(settings.OssSecret))
        {
            log("[错误] 未配置 OSS 凭据（OSS Key / OSS 密钥）。请到「设置 → 设置发布配置」里填写。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.UserName))
        {
            log("[错误] 还没登录。请先点主窗口右上角的「登录」。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(project.NwfReleaseId))
        {
            log("[错误] 这个项目没填内务府发布 ID。请右键项目 →「编辑…」补上。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 发版的后半段：把 zip 传到 OSS → 回调内务府登记发布记录。
    /// 和参考项目的一处差异：本地 zip 特意保留（它上传完就删），因为这边用户还要拿本地包手工分发。
    /// </summary>
    /// <param name="project">当前项目，回调时用它的发布 ID。</param>
    /// <param name="settings">发布配置，取 OSS 凭据和登录账号。</param>
    /// <param name="api">内务府接口客户端。</param>
    /// <param name="release">发布信息，取站点名（决定 OSS 路径）和目标服务器 IP。</param>
    /// <param name="zipPath">本地待上传的压缩包路径。</param>
    /// <param name="log">日志回调，上传进度和每一步结果都往这里写。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>上传与回调都成功返回 true；任一步失败返回 false。</returns>
    private static async Task<bool> PublishAsync(
        Project project,
        PublishSettings settings,
        NwfApi api,
        NwfRelease release,
        string zipPath,
        Action<string> log,
        CancellationToken ct)
    {
        var siteName = string.IsNullOrWhiteSpace(release.IisSiteName) ? "None" : release.IisSiteName;
        var ossKey = $"NWF/Project/{siteName}/{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.zip";

        log("------------------------------------------------------------");
        log($"开始上传 OSS: {OssUploader.Bucket}/{ossKey}");

        try
        {
            var uploader = new OssUploader(settings.OssKey, settings.OssSecret);
            await Task.Run(
                () => uploader.Upload(ossKey, zipPath, (percent, sent, total) => log(
                    $"上传进度: {percent:F1}%  ({sent / 1024.0 / 1024.0:F1}/{total / 1024.0 / 1024.0:F1} MB)")),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log($"[错误] OSS 上传失败: {ex.Message}");
            return false;
        }

        log("[完成] OSS 上传成功");

        var downloadLink = $"https://{OssUploader.Bucket}.{OssUploader.Endpoint}/{ossKey}";
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        log("回调内务府登记发布记录…");
        try
        {
            var ok = await api.AddIisManagerItemAsync(
                ipAddress: release.IpAddress ?? "",
                releaseTime: now,
                releaseId: project.NwfReleaseId,
                downloadLink: downloadLink,
                founder: settings.UserName,
                notes: $"{Environment.MachineName}--{now}发布").ConfigureAwait(false);

            if (!ok)
            {
                log("[错误] 内务府没返回有效结果，发布记录可能没登记上。");
                return false;
            }
        }
        catch (Exception ex)
        {
            log($"[错误] 内务府回调失败: {ex.Message}");
            return false;
        }

        log("[完成] 已登记内务府发布记录");
        log($"下载地址: {downloadLink}");
        return true;
    }

    /// <summary>
    /// 算出产物目录。填了「产物目录」就用填的，支持 {work} / {folder} / {stamp} 占位符
    /// 和 %TEMP% 这类环境变量；否则按类型推断：Api 用临时目录，Node 用项目下的 dist。
    /// </summary>
    /// <param name="project">当前项目，读它的类型和「产物目录」配置。</param>
    /// <param name="workDir">项目目录，占位符 {work} 替换成它。</param>
    /// <param name="folderName">项目目录的文件夹名，占位符 {folder} 替换成它。</param>
    /// <param name="stamp">本次运行的时间戳（yyyyMMdd-HHmmss），占位符 {stamp} 替换成它。</param>
    /// <param name="log">日志回调，配置有问题时写错误。</param>
    /// <returns>产物的绝对目录；配置解析不出目录或创建失败时返回 null。</returns>
    private static string? ResolveOutputDir(Project project, string workDir, string folderName, string stamp, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(project.OutputDir))
        {
            // 先展开 %TEMP% 这类环境变量，再替换占位符 —— 反过来的话，
            // 项目目录名里万一有 % 会被 ExpandEnvironmentVariables 当成变量吃掉。
            var dir = Environment.ExpandEnvironmentVariables(project.OutputDir.Trim())
                .Replace("{work}", workDir, StringComparison.OrdinalIgnoreCase)
                .Replace("{folder}", folderName, StringComparison.OrdinalIgnoreCase)
                .Replace("{stamp}", stamp, StringComparison.OrdinalIgnoreCase)
                .TrimEnd('\\', '/');

            if (dir.Length == 0)
            {
                log("[错误] 「产物目录」里只填了占位符，解析出来是空的。请检查项目设置。");
                return null;
            }

            WarnUnknownPlaceholders(dir, "产物目录", "{work}、{folder}、{stamp}", log);
            return dir;
        }

        switch (project.Type)
        {
            case ProjectTypes.Node:
                return System.IO.Path.Combine(workDir, "dist");

            case ProjectTypes.Api:
                {
                    var dir = System.IO.Path.Combine(TempRoot, "build", $"{folderName}-{stamp}");
                    try
                    {
                        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                        Directory.CreateDirectory(dir);
                    }
                    catch (Exception ex)
                    {
                        log($"[错误] 无法创建临时产物目录 {dir}: {ex.Message}");
                        return null;
                    }
                    return dir;
                }

            default:
                log("[错误] 自定义类型必须填写「产物目录」，否则不知道要打包哪个目录。");
                return null;
        }
    }

    /// <summary>
    /// Node.js 特例：构建产物固定落在项目下的 dist（vite / webpack 都由项目自己的配置决定），
    /// 手填的「产物目录」只被当成打包源头的话，那个目录会一直是空的。
    /// 所以这里在构建成功后把 dist 的内容同步过去，让这个配置真的起作用。
    /// 没填「产物目录」、或填的就是 dist 时什么都不做。
    /// </summary>
    /// <param name="project">当前项目，只对 Node.js 类型且填了「产物目录」时生效。</param>
    /// <param name="workDir">项目目录，dist 固定在这里。</param>
    /// <param name="outputDir">解析出的产物目录，dist 的内容会被复制过去。</param>
    /// <param name="log">日志回调，说明同步结果或拦下的原因。</param>
    /// <returns>可以继续打包返回 true；产物目录配成了源码目录、或复制失败返回 false。</returns>
    private static bool StageNodeOutput(Project project, string workDir, string outputDir, Action<string> log)
    {
        if (project.Type != ProjectTypes.Node) return true;
        if (string.IsNullOrWhiteSpace(project.OutputDir)) return true;

        var dist = System.IO.Path.Combine(workDir, "dist");
        var source = System.IO.Path.GetFullPath(dist).TrimEnd('\\');
        var target = System.IO.Path.GetFullPath(outputDir).TrimEnd('\\');

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return true;

        if (!Directory.Exists(dist))
        {
            log($"[提示] 没找到 {dist}，直接按你填的产物目录 {target} 打包。");
            return true;
        }

        // 产物目录填成项目目录或它的子目录时，同步就是往源码里灌文件，直接拦掉。
        var work = System.IO.Path.GetFullPath(workDir).TrimEnd('\\') + '\\';
        if ((target + '\\').StartsWith(work, StringComparison.OrdinalIgnoreCase))
        {
            log($"[错误] 产物目录不能是项目目录或它的子目录: {outputDir}");
            return false;
        }

        try
        {
            CopyDirectory(dist, target);
            log($"[产物] 已把 {dist} 同步到 {target}");
            return true;
        }
        catch (Exception ex)
        {
            log($"[错误] 同步产物到指定目录失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>递归复制目录内容，同名文件覆盖（不删除目标里原有的其它文件）。</summary>
    /// <param name="source">源目录。</param>
    /// <param name="target">目标目录，不存在会自动创建。</param>
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, System.IO.Path.Combine(target, System.IO.Path.GetFileName(dir)));
    }

    /// <summary>
    /// 解析项目指定的 Node 版本，得到要插到 PATH 最前面的 nvm 版本目录。
    ///
    /// 只对 Node.js 项目生效；没填版本、或不是 Node 项目时直接成功（<paramref name="nodePath"/> 为 null，
    /// 表示用系统默认的 node）。填了却找不到对应版本时打一条错误日志并返回 false，
    /// 让调用方在**编译开始前**就停下，别白跑一次构建。
    /// </summary>
    /// <param name="project">当前项目，读类型和「Node 版本」。</param>
    /// <param name="log">日志回调，成功时打一行实际用的版本，失败时打原因。</param>
    /// <param name="nodePath">解析出的版本目录；不需要换版本时为 null。</param>
    /// <returns>可以继续构建返回 true。</returns>
    private static bool TryResolveNodePath(Project project, Action<string> log, out string? nodePath)
    {
        nodePath = null;

        if (project.Type != ProjectTypes.Node) return true;
        if (string.IsNullOrWhiteSpace(project.NodeVersion)) return true;

        if (!NodeRuntime.TryResolve(project.NodeVersion, out var directory, out var error))
        {
            log($"[错误] {error}");
            return false;
        }

        nodePath = directory;
        log($"[Node] 使用 {System.IO.Path.GetFileName(directory)}（{directory}）");
        return true;
    }

    /// <summary>
    /// 生成构建命令。用户填了就用用户的（把 {output} 换成产物目录），
    /// 没填就按类型给默认命令。
    /// </summary>
    /// <param name="project">当前项目，读它的类型和自定义构建命令。</param>
    /// <param name="workDir">项目目录，拼默认 dotnet publish 时找 .csproj 用。</param>
    /// <param name="outputDir">产物目录，替换命令里的 {output} 占位符。</param>
    /// <param name="log">日志回调，拼默认命令时提示目录里有多个 .csproj 之类的情况。</param>
    /// <returns>可执行的命令行；自定义类型又没填命令时返回空串。</returns>
    private static string ResolveBuildCommand(Project project, string workDir, string outputDir, Action<string> log)
    {
        var custom = project.BuildCommand?.Trim();
        if (!string.IsNullOrEmpty(custom))
        {
            var command = custom.Replace("{output}", outputDir, StringComparison.OrdinalIgnoreCase);
            WarnUnknownPlaceholders(command, "构建命令", "{output}", log);
            return command;
        }

        return project.Type switch
        {
            ProjectTypes.Node => "npm run build",

            ProjectTypes.Api => BuildDefaultDotnetPublish(workDir, outputDir, project.Environment, log),

            _ => "",
        };
    }

    /// <summary>
    /// 拼默认的 dotnet publish 命令。
    /// 目录下有且只有一个 .csproj 时显式带上，避免多项目目录下 dotnet 挑错或报 NETSDK1194。
    ///
    /// 默认按**自包含**发布（<c>-r win-x64 --self-contained=true</c>）：目标机不用装 .NET 运行时，
    /// 代价是产物大（几十 MB）、首次发布要联网拉一次 runtime pack、上传 OSS 也慢一些。
    /// 想要框架依赖的小包就把命令改成不带这两个参数，服务器装一次 Hosting Bundle。
    ///
    /// <c>--self-contained</c> 必须带值，这里用等号形式：它是「空格分隔可选值」的参数，
    /// 只写 <c>--self-contained</c> 的话，MSBuild 会把紧随其后的 <c>-p:EnvironmentName=xxx</c>
    /// 当成它的值，直接报 MSB4030「不是有效的 System.Boolean」。
    /// </summary>
    /// <param name="workDir">项目目录，用来找唯一的 .csproj。</param>
    /// <param name="outputDir">产物目录，作为 <c>-o</c> 的值。</param>
    /// <param name="environment">环境名，非 None 时追加 <c>-p:EnvironmentName=</c>。</param>
    /// <param name="log">日志回调，目录里有多个 .csproj 时提示用户自己写全命令。</param>
    /// <returns>拼好的 dotnet publish 命令行。</returns>
    private static string BuildDefaultDotnetPublish(string workDir, string outputDir, string environment, Action<string> log)
    {
        var parts = new List<string> { "dotnet", "publish" };

        var projects = Directory.GetFiles(workDir, "*.csproj", SearchOption.TopDirectoryOnly);
        if (projects.Length == 1)
        {
            parts.Add($"\"{System.IO.Path.GetFileName(projects[0])}\"");
        }
        else if (projects.Length > 1)
        {
            log($"[提示] 目录里有 {projects.Length} 个 .csproj，请到项目设置里把构建命令写全（指定要发布哪一个）。");
        }

        parts.Add("-c");
        parts.Add("Release");
        parts.Add("-r");
        parts.Add("win-x64");
        parts.Add("--self-contained=true");

        if (!string.IsNullOrWhiteSpace(environment) && environment != Environments.None)
        {
            parts.Add($"-p:EnvironmentName={environment}");
        }

        parts.Add("-o");
        parts.Add($"\"{outputDir}\"");

        return string.Join(' ', parts);
    }

    /// <summary>
    /// 把 appsettings.{环境}.json 复制成 appsettings.json，并删掉其余环境文件。
    /// 环境为 None 或文件不存在时跳过。只对 .NET 项目有意义。
    /// </summary>
    /// <param name="outputDir">产物目录，从这里找并改写 appsettings 文件。</param>
    /// <param name="environment">目标环境名（Development / Production / Staging 等）。</param>
    /// <param name="log">日志回调，说明切换结果或没找到对应文件。</param>
    private static void ApplyEnvironmentConfig(string outputDir, string environment, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(environment) || environment == Environments.None) return;

        var source = System.IO.Path.Combine(outputDir, $"appsettings.{environment}.json");
        if (!File.Exists(source))
        {
            log($"[提示] 未找到 appsettings.{environment}.json，跳过环境切换");
            return;
        }

        File.Copy(source, System.IO.Path.Combine(outputDir, "appsettings.json"), overwrite: true);

        foreach (var file in Directory.GetFiles(outputDir, "appsettings.*.json"))
        {
            var name = System.IO.Path.GetFileName(file);
            if (name.Equals($"appsettings.{environment}.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)) continue;
            File.Delete(file);
        }

        log($"[环境] 已切换为 {environment}");
    }

    /// <summary>在产物目录写 version.txt（UTF-8 无 BOM），方便线上排查这个包是哪台机器什么时候打的。</summary>
    /// <param name="outputDir">产物目录，文件写在这里。</param>
    /// <param name="folderName">项目文件夹名，拼进 createDate 字段。</param>
    /// <param name="environment">环境名，原样写进 EnvironmentName 字段。</param>
    /// <param name="log">日志回调，写失败只提示不中断流程。</param>
    private static void WriteVersionFile(string outputDir, string folderName, string environment, Action<string> log)
    {
        var createDate = $"{folderName}{DateTime.Now:yyyy-MM-dd-HHmmss}";
        var payload = new
        {
            createDate,
            EnvironmentName = environment,
            authorIP = LocalIPv4(),
            author = $"{Environment.UserName}-{Environment.MachineName}",
        };

        try
        {
            File.WriteAllText(
                System.IO.Path.Combine(outputDir, "version.txt"),
                JsonSerializer.Serialize(payload),
                new UTF8Encoding(false));
            log($"[信息] 已写入 version.txt ({createDate})");
        }
        catch (Exception ex)
        {
            log($"[提示] 写 version.txt 失败: {ex.Message}");
        }
    }

    /// <summary>占位符的写法：花括号里一个字母开头的名字，例如 {output}。</summary>
    private static readonly Regex PlaceholderPattern = new(@"\{[A-Za-z_][A-Za-z0-9_]*\}");

    /// <summary>
    /// 替换完还剩下花括号的，说明写了个不存在的占位符（比如把 {output} 写成 {outpub}）。
    /// 这种不会被替换掉，会原样交给系统执行，最后只表现为「找不到文件 / 退出码非 0」，
    /// 所以提前写一条提示，别让人去猜。
    /// </summary>
    /// <param name="text">已经替换过占位符的文本。</param>
    /// <param name="where">出错的位置名，用在提示里（后置命令 / 构建命令 / 产物目录）。</param>
    /// <param name="allowed">这个位置支持哪些占位符，直接写成给人看的文案。</param>
    /// <param name="log">日志回调。</param>
    private static void WarnUnknownPlaceholders(string text, string where, string allowed, Action<string> log)
    {
        var found = PlaceholderPattern.Matches(text)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (found.Count == 0) return;

        log($"[提示] {where}里的 {string.Join("、", found)} 不是占位符，会原样执行。可用的是 {allowed}。");
    }

    /// <summary>逐行执行后置命令，空行和 # 开头的行跳过。任一条失败即中断并返回 false。</summary>
    /// <param name="project">当前项目，读它的「后置命令」多行文本。</param>
    /// <param name="workDir">项目目录，命令在这里执行，也是 {work} 占位符的值。</param>
    /// <param name="outputDir">产物目录，替换命令里的 {output} 占位符。</param>
    /// <param name="log">日志回调，每条命令执行前先写一行。</param>
    /// <param name="ct">取消令牌，取消后不再执行后续命令。</param>
    /// <param name="nodePath">指定 node 版本时要插到 PATH 最前面的目录；null 表示用系统默认。</param>
    /// <returns>全部命令成功（或没有命令）返回 true；任一条失败或被取消返回 false。</returns>
    private static async Task<bool> RunPostCommandsAsync(
        Project project,
        string workDir,
        string outputDir,
        Action<string> log,
        CancellationToken ct,
        string? nodePath = null)
    {
        if (string.IsNullOrWhiteSpace(project.PostCommands)) return true;

        var lines = project.PostCommands
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        foreach (var line in lines)
        {
            if (ct.IsCancellationRequested) return false;

            var command = line
                .Replace("{work}", workDir, StringComparison.OrdinalIgnoreCase)
                .Replace("{folder}", new DirectoryInfo(workDir).Name, StringComparison.OrdinalIgnoreCase)
                .Replace("{output}", outputDir, StringComparison.OrdinalIgnoreCase)
                .Replace("{stamp}", DateTime.Now.ToString("yyyyMMdd-HHmmss"), StringComparison.OrdinalIgnoreCase);

            WarnUnknownPlaceholders(command, "后置命令", "{work}、{folder}、{output}、{stamp}", log);
            log($"[后置] {command}");
            var code = await Task
                .Run(() => ProcessRunner.Run(command, workDir, log, ct, nodePath), ct)
                .ConfigureAwait(false);

            if (code == 0) continue;
            log($"[错误] 后置命令失败，退出码: {code}");
            return false;
        }

        return true;
    }

    /// <summary>取本机第一个 IPv4 地址，写进 version.txt。取不到返回空串。</summary>
    /// <returns>本机 IPv4 地址；解析失败时返回空串。</returns>
    private static string LocalIPv4()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                       .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                       ?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }
}

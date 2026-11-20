using System.IO;
using System.Text.RegularExpressions;

namespace PublishStudio.Core;

/// <summary>
/// 按版本号找到 nvm-windows 装好的那个 node 目录，用来「只给这次构建换版本」。
///
/// 刻意不调用 <c>nvm use</c>：nvm-windows 的 use 是改全局 junction（还要管理员权限），
/// 多项目并行发布时会把别人正在跑的 npm 切到错误版本上去。这里只把版本目录塞进
/// 子进程的 PATH 最前面，进程结束就失效，不污染系统。
/// </summary>
public static class NodeRuntime
{
    /// <summary>nvm 版本目录的命名，形如 v18.20.8。</summary>
    private static readonly Regex FolderPattern = new(@"^v(\d+)\.(\d+)\.(\d+)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// 找 nvm 的根目录。顺序：NVM_HOME 环境变量 → %APPDATA%\nvm\settings.txt 里的 root → %APPDATA%\nvm。
    /// </summary>
    /// <returns>存在的根目录；没装 nvm 返回 null。</returns>
    public static string? Root()
    {
        var fromEnv = Environment.GetEnvironmentVariable("NVM_HOME");
        if (IsFolder(fromEnv)) return fromEnv!.Trim();

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm");

        // settings.txt 是 nvm-windows 自己的配置，root: 那一行才是版本真正存放的位置。
        var settings = Path.Combine(appData, "settings.txt");
        try
        {
            if (File.Exists(settings))
            {
                foreach (var line in File.ReadAllLines(settings))
                {
                    var text = line.Trim();
                    if (!text.StartsWith("root:", StringComparison.OrdinalIgnoreCase)) continue;

                    var value = text[5..].Trim();
                    if (IsFolder(value)) return value;
                }
            }
        }
        catch
        {
            // 读不了 settings.txt 就当没有，继续看默认目录。
        }

        return IsFolder(appData) ? appData : null;
    }

    /// <summary>已安装的全部 node 版本（不带 v 前缀），按版本号从新到旧。</summary>
    /// <returns>版本号列表；没装 nvm 或一个版本都没有时返回空数组。</returns>
    public static IReadOnlyList<string> Versions()
    {
        var root = Root();
        if (root is null) return Array.Empty<string>();

        try
        {
            return Directory.GetDirectories(root)
                .Select(folder => new DirectoryInfo(folder))
                .Where(dir => FolderPattern.IsMatch(dir.Name) && File.Exists(Path.Combine(dir.FullName, "node.exe")))
                .OrderByDescending(dir => ParseName(dir.Name))
                .Select(dir => dir.Name.TrimStart('v', 'V'))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 把用户填的版本换成目录全路径。<c>18</c> / <c>18.20</c> / <c>18.20.8</c> 都能用，
    /// 只写主版本或「主.次」时取符合条件的最高版本。
    /// </summary>
    /// <param name="version">用户填的版本；为空表示用系统默认的 node（此时返回成功且目录为 null）。</param>
    /// <param name="directory">解析出的版本目录；留空或失败时为 null。</param>
    /// <param name="error">失败原因，可以直接打到日志里给用户看。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryResolve(string? version, out string? directory, out string error)
    {
        directory = null;
        error = "";

        var wanted = (version ?? "").Trim().TrimStart('v', 'V');
        if (wanted.Length == 0) return true;

        var root = Root();
        if (root is null)
        {
            error = "找不到 nvm-windows（NVM_HOME 没设置，%APPDATA%\\nvm 也不存在）。"
                  + "把「Node 版本」留空就用系统 PATH 里的 node。";
            return false;
        }

        var segments = wanted.Split('.');
        if (segments.Length is < 1 or > 3 || segments.Any(part => !int.TryParse(part, out _)))
        {
            error = $"Node 版本「{version}」看不懂，请填 18 / 18.20 / 18.20.8 这种形式。";
            return false;
        }

        var numbers = segments.Select(int.Parse).ToArray();

        try
        {
            var hit = Directory.GetDirectories(root)
                .Where(folder => FolderPattern.IsMatch(Path.GetFileName(folder)))
                .Where(folder => File.Exists(Path.Combine(folder, "node.exe")))
                .Select(folder => (Folder: folder, Parts: ParseName(Path.GetFileName(folder))))
                .Where(item => item.Parts.Major == numbers[0])
                .Where(item => numbers.Length < 2 || item.Parts.Minor == numbers[1])
                .Where(item => numbers.Length < 3 || item.Parts.Patch == numbers[2])
                .OrderByDescending(item => item.Parts)
                .ToList();

            if (hit.Count > 0)
            {
                directory = hit[0].Folder;
                return true;
            }
        }
        catch (Exception ex)
        {
            error = $"读取 nvm 目录 {root} 失败：{ex.Message}";
            return false;
        }

        var installed = Versions();
        error = installed.Count == 0
            ? $"nvm 目录 {root} 里一个 node 版本都没有，先执行 nvm install {wanted}。"
            : $"nvm 里没有 Node v{wanted}。已安装：{string.Join("、", installed)}（{root}）";
        return false;
    }

    /// <summary>版本目录名拆成可比较的三段数字，用于排序和匹配。</summary>
    /// <param name="folderName">目录名，形如 <c>v18.20.8</c>。</param>
    /// <returns>主 / 次 / 修订三个版本号。</returns>
    private static (int Major, int Minor, int Patch) ParseName(string folderName)
    {
        var match = FolderPattern.Match(folderName);
        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
    }

    /// <summary>目录是否真实存在。</summary>
    /// <param name="folder">待检查的路径，可为 null。</param>
    /// <returns>存在返回 true。</returns>
    private static bool IsFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            return Directory.Exists(folder.Trim());
        }
        catch
        {
            return false;
        }
    }
}

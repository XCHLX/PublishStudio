using System.Globalization;
using System.Text.RegularExpressions;

namespace PublishStudio.Core;

/// <summary>
/// 临时编译目录（%TEMP%\PublishStudio\build）里历史目录的清理。
/// 目录名形如「{项目目录名}-{yyyyMMdd-HHmmss}」，年龄按名字里的时间戳算；
/// 名字解析不出来（手建的、早期版本留下的）就退回用目录的最后写入时间。
/// </summary>
public static class BuildHistory
{
    /// <summary>存放所有临时编译产物的目录。</summary>
    public static string BuildRoot => Path.Combine(Publisher.TempRoot, "build");

    /// <summary>目录名末尾的时间戳，例如「my-api-20261116-113000」里的「20261116-113000」。</summary>
    private static readonly Regex StampSuffix = new(@"-(?<stamp>\d{8}-\d{6})$", RegexOptions.Compiled);

    /// <summary>
    /// 「不保留」时的宽限：创建不到 1 小时的目录不删。
    /// 一是刚发布完的那个目录还能让你拿本地包，二是别的项目可能正在这个目录里编译。
    /// </summary>
    private const int ActiveGraceHours = 1;

    /// <summary>
    /// 删掉创建时间早于 keepDays 天的目录，返回（删掉的目录数, 释放的字节数）。
    /// keepDays = 0 表示「不保留」，只留最近 1 小时内的目录；
    /// 目录被占用、权限不足都只跳过，不抛异常 —— 这不是关键路径，失败了下次再来。
    /// </summary>
    /// <param name="keepDays">保留天数；0 表示只保留最近 1 小时内创建的目录。</param>
    /// <returns>Removed 为删掉的目录数，Freed 为释放的字节数。</returns>
    public static (int Removed, long Freed) Purge(int keepDays)
    {
        if (keepDays < 0 || !Directory.Exists(BuildRoot)) return (0, 0);

        var deadline = keepDays == 0
            ? DateTime.Now.AddHours(-ActiveGraceHours)
            : DateTime.Now.AddDays(-keepDays);

        string[] dirs;
        try { dirs = Directory.GetDirectories(BuildRoot); }
        catch { return (0, 0); }

        var removed = 0;
        long freed = 0;

        foreach (var dir in dirs)
        {
            // 算不出年龄的一律留着，宁可不删。
            if (CreatedAt(dir) is not { } created || created > deadline) continue;

            try
            {
                var size = Publisher.DirectorySize(dir);
                Directory.Delete(dir, recursive: true);
                removed++;
                freed += size;
            }
            catch
            {
                // 比如目录里有文件正被占用（另一次发布在写），留着。
            }
        }

        return (removed, freed);
    }

    /// <summary>目录的创建时间：优先解析名字里的时间戳，解析不了用目录的最后写入时间。</summary>
    /// <param name="dir">要判断的目录全路径。</param>
    /// <returns>目录年龄的参考时间；两个来源都取不到时返回 null（调用方按「算不出年龄」保留）。</returns>
    private static DateTime? CreatedAt(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));

        var match = StampSuffix.Match(name);
        if (match.Success &&
            DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
        {
            return stamp;
        }

        try
        {
            var time = Directory.GetLastWriteTime(dir);
            return time == DateTime.MinValue ? null : time;
        }
        catch
        {
            return null;
        }
    }
}

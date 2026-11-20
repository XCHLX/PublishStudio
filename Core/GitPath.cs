namespace PublishStudio.Core;

/// <summary>
/// 找 git.exe。本机不一定把 Git 加进 PATH（有的机器只能在 Git Bash 里用），
/// 所以先扫 PATH，再试几个 Git for Windows 的默认安装位置。
/// </summary>
public static class GitPath
{
    /// <summary>找到的 git 可执行文件全路径；没装、或装在非常规位置时返回 null。</summary>
    /// <returns>git.exe / git.cmd 的绝对路径，找不到为 null。</returns>
    public static string? Find()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            foreach (var name in new[] { "git.exe", "git.cmd" })
            {
                var candidate = System.IO.Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // PATH 里没有：按 Git for Windows 的默认安装位置再找一遍。
        var candidates = new[]
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\cmd\git.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Git\cmd\git.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\cmd\git.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 从某个目录往上找 git 仓库根。项目经常是仓库里的一个子项目
    /// （比如 <c>E:\Code\scrm\...\Api.CahtSignalR</c> 而 .git 在 <c>E:\Code\scrm</c>），
    /// 只在项目目录里找 .git 会把它误判成「不是 git 仓库」。
    /// </summary>
    /// <param name="directory">起始目录，一般是项目目录。</param>
    /// <returns>仓库根目录（就是 .git 所在的那一级）；一直到盘根都没找到返回 null。</returns>
    public static string? FindRepositoryRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            // 普通仓库的 .git 是目录，worktree / submodule 里是文件，两种都得认。
            var dotGit = System.IO.Path.Combine(current.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit)) return current.FullName;

            current = current.Parent;
        }

        return null;
    }
}

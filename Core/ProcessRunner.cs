using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PublishStudio.Core;

/// <summary>
/// 子进程执行与目录压缩。所有外部命令（dotnet / npm / 自定义脚本）都从这里出去，
/// 是唯一需要小心处理编码、缓冲区和进程树的地方。
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// 解码子进程输出用的严格 UTF-8：遇到非法字节直接抛，用来判断这一行到底是不是 UTF-8。
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>系统 ANSI 代码页（中文 Windows 上是 GBK / 936），UTF-8 解不通时用它兜底。</summary>
    private static readonly Encoding SystemAnsi;

    /// <summary>
    /// 终端颜色等 ANSI 转义序列。npm / git 这类工具在管道里也可能照发，
    /// WPF 的文本框不解析，会显示成 ^[[32m 之类的怪字符。
    /// </summary>
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    /// <summary>注册代码页提供器，并记下系统 ANSI 代码页（中文 Windows 上是 936）。</summary>
    static ProcessRunner()
    {
        // .NET Core 默认只带 UTF-8 / UTF-16，取系统 ANSI 代码页前必须先注册。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        SystemAnsi = Encoding.GetEncoding(0);
    }

    /// <summary>
    /// 在一个独立的系统控制台窗口里跑命令，不接管输出 —— 日志直接滚在窗口里，用户能随手敲命令、Ctrl+C 停。
    /// 用于「运行」项目（dotnet watch run / npm run dev 这种常驻进程）。
    ///
    /// 走 cmd /k：命令结束（或崩了）窗口不会闪退，错误信息还留着能看。
    /// </summary>
    /// <param name="commandLine">要执行的命令行，原样交给 cmd。</param>
    /// <param name="workingDir">控制台的工作目录。</param>
    /// <param name="pathPrefix">要插到 PATH 最前面的目录（指定 node 版本时用）；为 null 就不动 PATH。</param>
    public static void RunInConsole(string commandLine, string workingDir, string? pathPrefix = null)
    {
        // 指定了 node 版本就先改这个窗口的 PATH，再跑命令：只在窗口内生效，不碰系统环境变量。
        var prefix = string.IsNullOrWhiteSpace(pathPrefix)
            ? ""
            : $"set \"PATH={pathPrefix.Trim().TrimEnd(';')};%PATH%\" && ";

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // 单独给路径加引号，带空格的目录也能 cd 进去。
            Arguments = $"/k cd /d \"{workingDir}\" && {prefix}{commandLine}",
            WorkingDirectory = workingDir,
            UseShellExecute = true,   // 必须为 true，否则不会开新窗口
        });
    }

    /// <summary>
    /// 执行外部命令，逐行回调输出。返回退出码；被取消时返回 -1。
    ///
    /// 阻塞式 API，调用方必须放在 Task.Run 里，否则会冻住界面。
    /// 输出回调可能来自后台线程，调用方自行保证线程安全。
    /// </summary>
    /// <param name="command">命令 + 参数。第 0 项是可执行名，其余逐个进 ArgumentList，带空格的参数不用自己加引号。</param>
    /// <param name="workingDir">子进程工作目录。</param>
    /// <param name="onOutput">逐行输出回调，可为 null。</param>
    /// <param name="cancellationToken">取消时强杀整个进程树。</param>
    /// <param name="pathPrefix">要插到子进程 PATH 最前面的目录（指定 node 版本时用）；为 null 就不动 PATH。</param>
    /// <param name="environment">额外塞给子进程的环境变量，为 null 就不动。用来关掉 git 的交互式账号密码提示。</param>
    public static int Run(
        IReadOnlyList<string> command,
        string workingDir,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default,
        string? pathPrefix = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (command.Count == 0) throw new ArgumentException("命令不能为空", nameof(command));

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 刻意不设 StandardOutputEncoding：下面自己读原始字节流，逐行判断是 UTF-8 还是 GBK。
        };

        // PATH 只改子进程的这一份，父进程和系统环境变量都不动。
        // 解析可执行文件也必须用同一份 PATH，否则会拿父进程的 PATH 找到另一个版本的 npm.cmd。
        var searchPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!string.IsNullOrWhiteSpace(pathPrefix))
        {
            searchPath = pathPrefix.Trim().TrimEnd(';') + ";" + searchPath;
            startInfo.Environment["PATH"] = searchPath;
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        }

        if (TryResolveExecutable(command[0], searchPath, out var executable))
        {
            startInfo.FileName = executable;
            for (var i = 1; i < command.Count; i++) startInfo.ArgumentList.Add(command[i]);
        }
        else
        {
            // PATH 里找不到，多半是 copy / xcopy / rd 这类 cmd 内置命令，交给 cmd 解释。
            // 不再预先 chcp 65001：让它按系统代码页（中文 = 936）正常输出，解码那一步会自己识别编码。
            var line = string.Join(' ', command.Select(QuoteIfNeeded));
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(line);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // 不用 BeginOutputReadLine：它按固定编码一次性解码，GBK 的输出会整片变成乱码。
        // 这里直接读原始字节流，按行判断编码。两路都必须读，否则缓冲区满了（约 4KB）
        // 子进程会卡死，表现就是「发布卡在某一行不动」。
        var stdout = process.StandardOutput.BaseStream;
        var stderr = process.StandardError.BaseStream;
        var pumps = new[]
        {
            Task.Run(() => PumpStream(stdout, onOutput)),
            Task.Run(() => PumpStream(stderr, onOutput)),
        };

        while (!process.WaitForExit(200))
        {
            if (!cancellationToken.IsCancellationRequested) continue;
            KillTree(process);
            // 进程被杀后管道会关闭，读线程自己就退了；这里等一下免得日志尾巴被丢掉。
            Task.WaitAll(pumps, 2000);
            return -1;
        }

        // 再等一次（无超时），确保异步输出流冲刷完毕，否则最后几行日志会丢。
        process.WaitForExit();
        Task.WaitAll(pumps);
        return process.ExitCode;
    }

    /// <summary>
    /// 把一路输出按 '\n' 切行，每行判断编码后回调。回调也在这个线程上，调用方要保证线程安全。
    /// </summary>
    /// <param name="stream">子进程的 stdout 或 stderr 原始字节流。</param>
    /// <param name="onOutput">每凑满一行的回调，可为 null。</param>
    private static void PumpStream(Stream stream, Action<string>? onOutput)
    {
        var buffer = new MemoryStream(512);
        var chunk = new byte[8192];

        try
        {
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var value = chunk[i];
                    if (value != (byte)'\n')
                    {
                        buffer.WriteByte(value);
                        continue;
                    }

                    Emit(buffer, onOutput);
                    buffer.SetLength(0);
                }
            }

            if (buffer.Length > 0) Emit(buffer, onOutput);
        }
        catch (Exception)
        {
            // 进程被强杀时管道会提前关闭，这里静默收场。
        }
    }

    /// <summary>
    /// 把缓冲的一行解码后回调：先按严格 UTF-8 解，解不通说明是 GBK，改用系统 ANSI 代码页。
    /// 顺带去掉 CRLF 里的 '\r' 和行首 BOM，否则界面上会多出怪字符。
    /// </summary>
    /// <param name="buffer">存着一行原始字节的缓冲区（不含 '\n'）。</param>
    /// <param name="onOutput">解码后的行回调，可为 null。</param>
    private static void Emit(MemoryStream buffer, Action<string>? onOutput)
    {
        var length = (int)buffer.Length;
        var bytes = buffer.GetBuffer();

        string line;
        try
        {
            line = StrictUtf8.GetString(bytes, 0, length);
        }
        catch (DecoderFallbackException)
        {
            line = SystemAnsi.GetString(bytes, 0, length);
        }

        line = line.TrimEnd('\r');
        if (line.StartsWith('\uFEFF')) line = line[1..];
        if (line.Contains('\u001b')) line = AnsiEscape.Replace(line, "");

        onOutput?.Invoke(line);
    }

    /// <summary>把整行命令拆成参数后执行，是接收参数数组那个 <c>Run</c> 的便捷重载。</summary>
    /// <param name="commandLine">整行命令，内部按引号规则拆分。</param>
    /// <param name="workingDir">子进程工作目录。</param>
    /// <param name="onOutput">逐行输出回调，可为 null。</param>
    /// <param name="cancellationToken">取消时强杀整个进程树。</param>
    /// <param name="pathPrefix">要插到子进程 PATH 最前面的目录（指定 node 版本时用）；为 null 就不动 PATH。</param>
    /// <param name="environment">额外塞给子进程的环境变量，为 null 就不动。</param>
    /// <returns>子进程退出码；被取消时返回 -1。</returns>
    public static int Run(string commandLine, string workingDir, Action<string>? onOutput = null,
        CancellationToken cancellationToken = default, string? pathPrefix = null,
        IReadOnlyDictionary<string, string>? environment = null)
        => Run(SplitCommand(commandLine), workingDir, onOutput, cancellationToken, pathPrefix, environment);

    /// <summary>强制结束整个进程树，避免 dotnet / npm / node 子进程残留。</summary>
    /// <param name="process">要杀掉的进程；已经退出时直接忽略。</param>
    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能刚好自己退出，忽略。
        }
    }

    /// <summary>
    /// 把可执行名解析成绝对路径。Windows 上 npm / yarn / pnpm 实际是 .cmd 文件，
    /// 直接 Process.Start("npm") 会抛「找不到文件」，必须先查 PATH 找到 npm.cmd。
    /// </summary>
    /// <param name="name">命令的第一个词，可能是绝对路径，也可能只是 <c>npm</c> 这样的名字。</param>
    /// <param name="pathVariable">用哪份 PATH 去找。指定了 node 版本时传的是「版本目录 + 原 PATH」，否则传系统 PATH。</param>
    /// <param name="resolved">解析出的可执行文件全路径；解析失败时原样回填入参。</param>
    /// <returns>找到可执行文件返回 true；找不到返回 false（调用方会退回交给 cmd 解释）。</returns>
    private static bool TryResolveExecutable(string name, string pathVariable, out string resolved)
    {
        resolved = name;
        if (string.IsNullOrWhiteSpace(name)) return false;

        if (name.Contains('\\') || name.Contains('/'))
        {
            var full = System.IO.Path.GetFullPath(name);
            resolved = full;
            return File.Exists(full);
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in pathVariable.Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var extension in extensions)
            {
                var candidate = System.IO.Path.Combine(dir.Trim('"'), name + extension);
                if (!File.Exists(candidate)) continue;
                resolved = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>参数里带空格就补引号，给 cmd /c 拼整行时用。</summary>
    /// <param name="value">单个参数原文。</param>
    /// <returns>需要时加上双引号的参数。</returns>
    private static string QuoteIfNeeded(string value)
        => value.Contains(' ') && !value.StartsWith('"') ? $"\"{value}\"" : value;

    /// <summary>
    /// 把一行命令按引号规则拆成参数数组，支持 "带空格的参数"。
    /// </summary>
    /// <param name="commandLine">要拆分的整行命令。</param>
    /// <returns>参数列表；引号本身不保留。</returns>
    public static List<string> SplitCommand(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length == 0) continue;
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// 把目录压成 zip，按 20% 粒度报进度。阻塞式，调用方须放在 Task.Run 里。
    /// </summary>
    /// <param name="sourceDir">源目录，其内容作为 zip 根。</param>
    /// <param name="zipPath">输出 zip 全路径，父目录自动创建，同名覆盖。</param>
    /// <param name="onLog">进度日志回调。</param>
    public static void ZipDirectory(string sourceDir, string zipPath, Action<string>? onLog = null)
    {
        var parent = System.IO.Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        if (File.Exists(zipPath)) File.Delete(zipPath);

        // 目标 zip 有可能就落在源目录里（比如把包输出到 publish 目录下）。
        // 必须把「目标文件」和「它所在的整个目录」一起排除 ——
        // 只排除文件本身的话，zip 目录里遗留的旧包会被递归打进来，包体逐次翻倍。
        var destination = System.IO.Path.GetFullPath(zipPath);
        var destinationDir = System.IO.Path.GetDirectoryName(destination) ?? destination;
        var sourceRoot = System.IO.Path.GetFullPath(sourceDir);

        var skipPrefix = string.Equals(destinationDir, sourceRoot, StringComparison.OrdinalIgnoreCase)
            ? ""
            : destinationDir + System.IO.Path.DirectorySeparatorChar;

        var files = Directory
            .GetFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Select(System.IO.Path.GetFullPath)
            .Where(f => !string.Equals(f, destination, StringComparison.OrdinalIgnoreCase))
            .Where(f => skipPrefix.Length == 0 || !f.StartsWith(skipPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (files.Length == 0)
        {
            onLog?.Invoke("[提示] 产物目录是空的，跳过压缩");
            return;
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var lastBucket = -1;
        for (var i = 0; i < files.Length; i++)
        {
            // zip 规范要求分隔符是 '/'，GetRelativePath 给的是 '\'，
            // 不改的话包在 Linux 上解开会变成一堆带反斜杠的文件名。
            var entryName = System.IO.Path.GetRelativePath(sourceDir, files[i]).Replace('\\', '/');
            archive.CreateEntryFromFile(files[i], entryName, CompressionLevel.Optimal);

            var percent = (i + 1) * 100 / files.Length;
            var bucket = percent / 20;
            if (bucket == lastBucket) continue;
            lastBucket = bucket;
            onLog?.Invoke($"压缩进度: {percent}%");
        }
    }
}

using System.Text;

namespace PublishStudio.Core;

/// <summary>
/// 把一个日志窗口的输出同步落到文本文件，全部平铺在 %TEMP%\PublishStudio\Logs 下。
/// 文件名带启动时间、项目名和动作名，从名字就能对上是哪一次运行、成没成：
/// 「2026-11-17_143012_项目名_发布_运行中.log」→ 结束时改成「_成功」或「_失败」。
/// 所有日志一律只保留 1 天，超期的在程序下次启动时删掉，不分成功失败。
/// </summary>
public sealed class LogFile
{
    /// <summary>日志保留天数，超期的在程序下次启动时删掉。</summary>
    private const int KeepDays = 1;

    /// <summary>文件名里项目名 / 动作名最多取多少字符。名字太长会把整条路径顶到 260 上限。</summary>
    private const int MaxNameLength = 40;

    /// <summary>日志目录（%TEMP%\PublishStudio\Logs），成功失败都放这一层。</summary>
    public static string Folder { get; } = Path.Combine(Publisher.TempRoot, "Logs");

    private readonly object _gate = new();

    /// <summary>文件名主干，不含目录和「_运行中 / _成功 / _失败」后缀。</summary>
    private readonly string _stem;

    /// <summary>这次运行的开始时间，收尾时用来算用时。</summary>
    private readonly DateTime _startedAt = DateTime.Now;

    /// <summary>当前文件的全路径；写不了文件时保持 null。</summary>
    private string? _path;

    private StreamWriter? _writer;

    /// <summary>是否已经收尾（Close 只生效一次）。</summary>
    private bool _closed;

    /// <summary>本次日志文件的全路径；写不了文件时为 null。</summary>
    public string? FilePath => _path;

    /// <summary>
    /// 在 Logs 目录里建一个「_运行中」的日志文件并写入首行。目录建不出来、磁盘写不了都只当没日志，不影响任务本身。
    /// </summary>
    /// <param name="project">项目名，进文件名。</param>
    /// <param name="action">动作名（编译 / 发布 / 某个快捷命令），进文件名。</param>
    public LogFile(string project, string action)
    {
        var now = DateTime.Now;
        _stem = $"{now:yyyy-MM-dd_HHmmss}_{Clip(project)}_{Clip(action)}";

        try
        {
            Directory.CreateDirectory(Folder);

            _path = Path.Combine(Folder, _stem + "_运行中.log");
            var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            _writer.WriteLine($"[{now:yyyy-MM-dd HH:mm:ss}] {action} - {project}");
            _writer.WriteLine(new string('-', 60));
        }
        catch
        {
            // 临时目录不可写时就不落盘了，界面上照样能看到日志。
            _writer = null;
            _path = null;
        }
    }

    /// <summary>写一行到文件。任意线程可调用，写失败静默跳过。</summary>
    /// <param name="line">要写入的一行文本，通常和界面上显示的那行完全一致。</param>
    public void Write(string line)
    {
        lock (_gate)
        {
            if (_writer is null) return;

            try
            {
                _writer.WriteLine(line);
            }
            catch
            {
                _writer = null;
            }
        }
    }

    /// <summary>
    /// 收尾：补一行结束时间，然后把「_运行中」改成「_成功」或者「_失败」，文件留在原地不动。
    /// 重复调用只生效一次。
    /// </summary>
    /// <param name="ok">任务是否成功，只影响文件名后缀（取消也算失败）。</param>
    public void Close(bool ok)
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;

            try
            {
                _writer?.WriteLine(new string('-', 60));
                _writer?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(ok ? "成功" : "失败")}，用时 {(DateTime.Now - _startedAt).TotalSeconds:F0} 秒");
            }
            catch
            {
                // 写尾巴失败无所谓，下面照样关文件。
            }

            try { _writer?.Dispose(); }
            catch { }

            _writer = null;

            if (_path is null) return;

            try
            {
                var target = Path.Combine(Folder, _stem + (ok ? "_成功.log" : "_失败.log"));

                if (File.Exists(target)) File.Delete(target);
                File.Move(_path, target);
                _path = target;
            }
            catch
            {
                // 文件被别的程序占着（比如正拿记事本开着）就留着「_运行中」，下次启动按天数清。
                _path = null;
            }
        }
    }

    /// <summary>
    /// 清掉 Logs 目录里最后写入时间早于 1 天的日志，成功失败一视同仁，也包含没来得及收尾的「_运行中」。
    /// 删不掉的（正被打开着）跳过，下次再来。
    /// </summary>
    public static void Cleanup()
    {
        string[] files;
        try
        {
            if (!Directory.Exists(Folder)) return;
            files = Directory.GetFiles(Folder, "*.log");
        }
        catch
        {
            return;
        }

        var deadline = DateTime.Now.AddDays(-KeepDays);

        foreach (var file in files)
        {
            try
            {
                if (File.GetLastWriteTime(file) < deadline) File.Delete(file);
            }
            catch
            {
                // 正被打开着的日志留着。
            }
        }
    }

    /// <summary>把项目名 / 动作名收拾成合法文件名：去掉非法字符，压缩空白，过长截断。</summary>
    /// <param name="name">原始文本（项目名或动作名）。</param>
    /// <returns>能直接拼进文件名的片段；空字符串时返回「未命名」。</returns>
    private static string Clip(string name)
    {
        var cleaned = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            cleaned.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 || ch == ' ' ? '_' : ch);
        }

        var text = cleaned.ToString().Trim('_', '.');
        if (text.Length == 0) return "未命名";
        return text.Length <= MaxNameLength ? text : text[..MaxNameLength];
    }
}

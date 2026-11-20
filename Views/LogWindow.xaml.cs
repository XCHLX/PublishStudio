using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PublishStudio.Core;

namespace PublishStudio.Views;

/// <summary>
/// 一个任务一个窗口，实时显示子进程输出。输出可能来自后台线程，
/// 所以先进缓冲区，再由 DispatcherTimer 每 100ms 批量刷一次 —— 一条一条更新界面会被 dotnet publish 的输出拖死。
/// 同一份内容同时写进 %TEMP%\PublishStudio\Logs 下的文本文件（文件名按成功 / 失败打后缀）。
/// </summary>
public partial class LogWindow : Window
{
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cancellation;
    private readonly LogFile _logFile;

    private bool _finished;

    /// <summary>任务已跑掉的时长，调用方拿去做结束提示。</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>本次运行落盘的日志文件全路径；临时目录不可写时为 null。</summary>
    public string? LogPath => _logFile.FilePath;

    /// <param name="title">项目名，显示在标题栏。</param>
    /// <param name="actionName">动作名（编译 / 发布）。</param>
    /// <param name="cancellation">取消源，由调用方持有并在任务结束时释放。</param>
    public LogWindow(string title, string actionName, CancellationTokenSource cancellation)
    {
        InitializeComponent();
        _cancellation = cancellation;
        _logFile = new LogFile(title, actionName);

        Title = $"{actionName} - {title}";
        StatusText.Text = $"{actionName}中…";

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _flushTimer.Tick += (_, _) => FlushPending();
        _flushTimer.Start();
    }

    /// <summary>追加一行日志。任意线程可调用：界面进缓冲区等定时器批量刷，文件当场写。</summary>
    /// <param name="line">一行日志文本，内部按行缓存，由定时器批量刷到界面。</param>
    public void Append(string line)
    {
        lock (_gate) _pending.AppendLine(line);
        _logFile.Write(line);
    }

    /// <summary>任务结束。ok 为 false 时显示红色的「失败」，同时这份日志改名成「_失败」收尾。</summary>
    /// <param name="ok">任务是否成功，决定状态点颜色和日志文件的后缀。</param>
    /// <param name="message">状态行文案，例如「发布完成」「已取消」「失败（退出码 1）」。</param>
    public void Finish(bool ok, string message)
    {
        _finished = true;
        _flushTimer.Stop();
        FlushPending();

        _logFile.Close(ok);

        StatusText.Text = message;
        StatusDot.Fill = new SolidColorBrush(ok ? Color.FromRgb(0x10, 0x89, 0x3E) : Color.FromRgb(0xC5, 0x30, 0x30));
        StopButton.IsEnabled = false;
        ElapsedText.Text = $"用时 {_clock.Elapsed.TotalSeconds:F1} 秒";
    }

    /// <summary>把缓冲区里攒的日志一次性追加到文本框并滚到底；没有新内容时只更新计时（UI 线程调用）。</summary>
    private void FlushPending()
    {
        string chunk;
        lock (_gate)
        {
            if (_pending.Length == 0)
            {
                if (!_finished) ElapsedText.Text = $"已运行 {_clock.Elapsed.TotalSeconds:F0} 秒";
                return;
            }
            chunk = _pending.ToString();
            _pending.Clear();
        }

        LogBox.AppendText(chunk);
        LogBox.ScrollToEnd();
    }

    /// <summary>「停止」按钮：取消任务并禁用按钮，状态行改成「正在停止…」（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        StatusText.Text = "正在停止…";
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 任务刚好结束，取消源已释放，忽略。
        }
    }

    /// <summary>「复制」按钮：把全部日志放进剪贴板，成败写在状态行（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogBox.Text);
            StatusText.Text = "日志已复制到剪贴板";
        }
        catch
        {
            StatusText.Text = "复制失败";
        }
    }

    /// <summary>「关闭」按钮：关掉窗口（未结束时关窗会连带停止任务，事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 关窗时如果任务还没结束，先取消它，避免子进程继续跑却没人看日志。
    /// </summary>
    /// <param name="e">关闭事件参数（这里不取消关闭动作）。</param>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (_finished) return;

        // 关窗等于停止任务，否则子进程会继续跑但没人看日志。
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 同上。
        }
    }
}

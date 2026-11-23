using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PublishStudio.Core;

namespace PublishStudio.Views;

/// <summary>
/// 「帮助 → 关于」窗口：版本、作者、运行环境、数据目录这些排查问题时最常被问到的信息。
/// 版本号来自程序集上的 <see cref="AssemblyInformationalVersionAttribute"/>（csproj 里的 InformationalVersion），
/// 不在界面里另写一份，免得两处不一致。
/// </summary>
public partial class AboutWindow : Window
{
    private readonly List<AboutItem> _items;

    /// <summary>最新版本的发布页地址，点「去下载」时用；没查到时为空。</summary>
    private string _releaseUrl = "";

    /// <summary>正在检查的标记，挡住自动检查与手动点击同时发两个请求。</summary>
    private bool _checking;

    /// <summary>
    /// 收集信息并显示。项目数量、登录账号这些取自当前配置，所以要把 <see cref="AppStore"/> 传进来。
    /// 窗口显示后自动查一次最新版本，查不到只影响底部那行提示。
    /// </summary>
    /// <param name="store">全局项目清单与发布配置。</param>
    public AboutWindow(AppStore store)
    {
        InitializeComponent();

        _items = BuildItems(store);

        var version = AppVersion();
        VersionText.Text = $"V{version}";
        InfoList.ItemsSource = _items;

        Loaded += async (_, _) => await CheckAsync();
    }

    /// <summary>拼出所有要显示的信息行；顺序就是界面上的顺序。</summary>
    /// <param name="store">全局项目清单与发布配置。</param>
    /// <returns>标签 + 值的信息行集合。</returns>
    private static List<AboutItem> BuildItems(AppStore store)
    {
        var total = store.Projects.Count;
        var production = store.Projects.Count(p => p.NwfRelease?.IsProduce == true);

        var user = store.Settings.UserName.Trim();

        return new List<AboutItem>
        {
            new("版本", $"V{AppVersion()}"),
            new("作者", "陈"),
            new("系统", $"{RuntimeInformation.OSDescription.Trim()} · {(Environment.Is64BitProcess ? "64 位" : "32 位")}"),
            new("运行环境", $"{RuntimeInformation.FrameworkDescription} · x64 自包含"),
            new("项目数量", total == 0
                ? "还没有项目"
                : $"共 {total} 个（正式 {production} · 测试 {total - production}）"),
            new("登录账号", user.Length == 0 ? "未登录" : user),
            new("接口地址", Secret.Mask(store.Settings.ApiUrl)),
            new("配置目录", AppStore.DataDir),
            new("日志目录", LogFile.Folder),
            new("临时目录", Publisher.TempRoot),
        };
    }

    /// <summary>
    /// 取版本号的显示文本：读程序集里的 InformationalVersion，去掉可能被追加的「+提交哈希」。
    /// 读不到时退回 csproj 里的初始值，保证界面上永远有版本号。
    /// </summary>
    /// <returns>形如 <c>0.4.0</c> 的版本文本（不含前缀 V）。</returns>
    private static string AppVersion()
    {
        var raw = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(raw)) return "0.0.0";

        var plus = raw.IndexOf('+');
        return plus > 0 ? raw[..plus] : raw;
    }

    /// <summary>
    /// 查询 GitHub 上的最新版本并更新底部状态行。检查期间禁用按钮，避免连点发出多个请求。
    /// 任何失败都只改这一行文字，不弹框、不影响窗口其它内容。
    /// </summary>
    private async Task CheckAsync()
    {
        if (_checking) return;

        _checking = true;
        CheckButton.IsEnabled = false;

        // 每次检查先回到默认外观，避免上一次的红色残留。
        var primary = (Brush)FindResource("PrimaryBrush");
        VersionText.Foreground = primary;
        VersionText.ToolTip = "打开 GitHub 发布页";
        SetStatus("正在检查更新…", null);

        try
        {
            var result = await UpdateChecker.CheckAsync(AppVersion());

            if (result is null)
            {
                SetStatus("无法检查更新（网络不可用或被 GitHub 限流）", null);
            }
            else if (result.HasUpdate)
            {
                // 有新版本：版本号和提示行一起转红，右边跟一个「去下载」。
                _releaseUrl = result.ReleaseUrl;
                VersionText.Foreground = (Brush)FindResource("DangerBrush");
                VersionText.ToolTip = $"打开 V{result.Latest} 的发布页";
                SetStatus($"发现新版本 V{result.Latest}", "去下载", "DangerBrush");
            }
            else
            {
                SetStatus($"已是最新版本（V{result.Latest}）", null, "SuccessBrush");
            }
        }
        catch (Exception ex)
        {
            // 兜底：这里跑在 async void 的事件里，漏出去的异常会让整个程序崩掉。
            Debug.WriteLine($"[AboutWindow] 检查更新异常: {ex.Message}");
            SetStatus("无法检查更新（网络不可用或被 GitHub 限流）", null);
        }
        finally
        {
            _checking = false;
            CheckButton.IsEnabled = true;
        }
    }

    /// <summary>更新底部状态行，并决定要不要显示「去下载」。</summary>
    /// <param name="text">状态文字。</param>
    /// <param name="linkText">链接文字；为 <c>null</c> 时隐藏链接。</param>
    /// <param name="brushKey">文字颜色，取 Theme.xaml 里的画刷键。文字和链接用同一个颜色（有新版时是红色）。</param>
    private void SetStatus(string text, string? linkText, string brushKey = "MutedBrush")
    {
        var brush = (Brush)FindResource(brushKey);

        UpdateText.Text = text;
        UpdateText.Foreground = brush;
        UpdateLink.Foreground = brush;

        if (linkText is null)
        {
            UpdateLink.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateLink.Text = linkText;
        UpdateLink.Visibility = Visibility.Visible;
    }

    /// <summary>「检查更新」按钮：手动重查一次（自动检查失败后可以点它重试）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    /// <summary>用默认浏览器打开发布页：查到新版本就打开那个版本的页面，否则打开 Releases 列表。</summary>
    private void OpenReleasePage()
    {
        var url = _releaseUrl.Length > 0 ? _releaseUrl : UpdateChecker.ReleasesUrl;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AboutWindow] 打开发布页失败: {ex.Message}");
        }
    }

    /// <summary>「去下载」链接。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void UpdateLink_Click(object sender, MouseButtonEventArgs e) => OpenReleasePage();

    /// <summary>点顶部版本号，效果同「去下载」。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void VersionText_Click(object sender, MouseButtonEventArgs e) => OpenReleasePage();

    /// <summary>「复制信息」：把上面这些行拼成纯文本放进剪贴板，方便贴到群里反馈问题。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder("发布工具 关于信息");
        text.AppendLine();

        foreach (var item in _items) text.AppendLine($"{item.Label}：{item.Value}");

        try
        {
            Clipboard.SetText(text.ToString());
            CopyButton.Content = "已复制";
        }
        catch
        {
            // 剪贴板偶尔被别的进程占用。
            CopyButton.Content = "复制失败";
        }
    }

    /// <summary>「关闭」按钮（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>关于窗口里的一行信息：左侧标签、右侧值。给 ItemsControl 绑定用，属性必须是 public。</summary>
public sealed class AboutItem
{
    /// <param name="label">左侧标签，例如「版本」。</param>
    /// <param name="value">右侧的值，例如「V0.4.0」或某个目录路径。</param>
    public AboutItem(string label, string value)
    {
        Label = label;
        Value = value;
    }

    /// <summary>左侧标签。</summary>
    public string Label { get; }

    /// <summary>右侧的值。</summary>
    public string Value { get; }
}

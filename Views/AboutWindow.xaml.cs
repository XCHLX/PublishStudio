using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
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

    /// <summary>
    /// 收集信息并显示。项目数量、登录账号这些取自当前配置，所以要把 <see cref="AppStore"/> 传进来。
    /// </summary>
    /// <param name="store">全局项目清单与发布配置。</param>
    public AboutWindow(AppStore store)
    {
        InitializeComponent();

        _items = BuildItems(store);

        var version = AppVersion();
        VersionText.Text = $"V{version}";
        InfoList.ItemsSource = _items;
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
    /// <returns>形如 <c>0.04</c> 的版本文本（不含前缀 V）。</returns>
    private static string AppVersion()
    {
        var raw = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(raw)) return "0.04";

        var plus = raw.IndexOf('+');
        return plus > 0 ? raw[..plus] : raw;
    }

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
    /// <param name="value">右侧的值，例如「V0.04」或某个目录路径。</param>
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

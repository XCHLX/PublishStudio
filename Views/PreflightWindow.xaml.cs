using System.Windows;
using System.Windows.Media;
using PublishStudio.Core;

namespace PublishStudio.Views;

/// <summary>
/// 发布前的预检结果窗口：一项一行，通过的带绿勾、有问题的带红叉。
/// 它只负责展示和「继续 / 取消」，结论由 <see cref="Preflight"/> 算好传进来。
/// 有 fail 时按钮变「仍然发布」且默认按钮挪到「取消」上，误按回车不会直接发版。
/// </summary>
public partial class PreflightWindow : Window
{
    /// <param name="projectName">项目名，显示在标题和结论里。</param>
    /// <param name="items">检查结果，顺序即显示顺序。</param>
    private PreflightWindow(string projectName, IReadOnlyList<PreflightItem> items)
    {
        InitializeComponent();

        Title = $"发布前预检 - {projectName}";
        ItemList.ItemsSource = items;

        var fails = items.Count(item => item.Level == Preflight.Fail);
        var warns = items.Count(item => item.Level == Preflight.Warn);

        if (fails == 0)
        {
            SummaryText.Text = warns == 0
                ? $"「{projectName}」配置齐全，可以发布。"
                : $"「{projectName}」可以发布，有 {warns} 项提醒值得看一眼。";
            SummaryText.Foreground = (Brush)FindResource(warns == 0 ? "SuccessBrush" : "TextBrush");

            ContinueButton.Content = "继续发布";
            ContinueButton.IsDefault = true;
            return;
        }

        SummaryText.Text = $"「{projectName}」有 {fails} 项没通过，继续发布很可能中途失败。";
        SummaryText.Foreground = (Brush)FindResource("DangerBrush");

        ContinueButton.Content = "仍然发布";
        CancelButton.IsDefault = true;
        CancelButton.Focus();
    }

    /// <summary>弹预检窗口。返回 true 表示用户点了「继续发布」。</summary>
    /// <param name="owner">主窗口；它藏在托盘里时改成屏幕居中，否则跟着 Owner 一起看不见。</param>
    /// <param name="projectName">项目名，用在标题和结论里。</param>
    /// <param name="items">检查结果。</param>
    /// <returns>继续发布返回 true；取消返回 false。</returns>
    public static bool Confirm(Window owner, string projectName, IReadOnlyList<PreflightItem> items)
    {
        var window = new PreflightWindow(projectName, items);

        if (owner.IsVisible) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return window.ShowDialog() == true;
    }

    /// <summary>「继续发布」（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

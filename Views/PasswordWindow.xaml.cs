using System.Windows;
using PublishStudio.Core;

namespace PublishStudio.Views;

/// <summary>
/// 备份口令输入窗口。一个窗口两种用法：
/// 导出时输两遍（<see cref="Ask"/> 的 confirm 传 true），导入时输一遍。
/// 口令只在这一个窗口里存着，用完即弃，不写任何地方。
/// </summary>
public partial class PasswordWindow : Window
{
    /// <summary>true 表示导出（要确认两遍并校验长度），false 表示导入（只输一遍）。</summary>
    private readonly bool _confirm;

    /// <summary>用户输入的口令，只在 DialogResult 为 true 时有效。</summary>
    public string Password { get; private set; } = "";

    /// <param name="headline">标题那一行，例如「设置备份口令」。</param>
    /// <param name="description">下面的说明文字。</param>
    /// <param name="confirm">是否需要「再输一次」。</param>
    private PasswordWindow(string headline, string description, bool confirm)
    {
        InitializeComponent();

        _confirm = confirm;
        HeadlineText.Text = headline;
        DescText.Text = description;

        if (!confirm)
        {
            ConfirmLabel.Visibility = Visibility.Collapsed;
            PasswordBox2.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) => PasswordBox1.Focus();
    }

    /// <summary>弹窗口问口令。</summary>
    /// <param name="owner">父窗口；它藏在托盘里时改成屏幕居中，否则跟着 Owner 一起看不见。</param>
    /// <param name="headline">标题那一行。</param>
    /// <param name="description">说明文字。</param>
    /// <param name="confirm">是否需要「再输一次」。</param>
    /// <returns>口令；用户取消时返回 null。</returns>
    public static string? Ask(Window owner, string headline, string description, bool confirm)
    {
        var window = new PasswordWindow(headline, description, confirm);

        if (owner.IsVisible) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return window.ShowDialog() == true ? window.Password : null;
    }

    /// <summary>「确定」：校验后把口令交给调用方（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox1.Password;

        if (password.Length == 0)
        {
            Fail("请输入口令。");
            return;
        }

        if (_confirm)
        {
            if (password.Length < ConfigFile.MinimumPasswordLength)
            {
                Fail($"口令至少 {ConfigFile.MinimumPasswordLength} 位，太短容易被人试出来。");
                return;
            }

            if (password != PasswordBox2.Password)
            {
                Fail("两次输入的口令不一致。");
                PasswordBox2.Clear();
                PasswordBox2.Focus();
                return;
            }
        }

        Password = password;
        DialogResult = true;
    }

    /// <summary>把一行红字显示出来，并把焦点放回第一个输入框。</summary>
    /// <param name="message">要提示的话。</param>
    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        PasswordBox1.Focus();
        PasswordBox1.SelectAll();
    }

    /// <summary>「取消」（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

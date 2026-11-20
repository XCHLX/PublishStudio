using System.Windows;
using System.Windows.Media;
using PublishStudio.Core;

namespace PublishStudio.Views;

/// <summary>
/// 登录窗口。用内务府账号名调 GetUserNwfAuthMenu，按 WebManage 条目数判定权限，
/// 逻辑与 E:\Code\ProjectManager\Forms\LoginForm.cs 一致。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly NwfApi _api;

    /// <summary>登录成功的账号名。DialogResult 为 true 时才有值。</summary>
    public string? LoggedInUser { get; private set; }

    /// <summary>打开登录窗口，并把上次的账号名预填进输入框。</summary>
    /// <param name="api">内务府接口客户端（根地址来自发布配置）。</param>
    /// <param name="currentUser">上次登录的账号名，用于预填输入框，可为空。</param>
    public LoginWindow(NwfApi api, string? currentUser)
    {
        InitializeComponent();

        _api = api;
        UserBox.Text = currentUser ?? "";

        Loaded += (_, _) =>
        {
            UserBox.Focus();
            UserBox.SelectAll();
        };
    }

    /// <summary>「登录」按钮入口，转调 <see cref="LoginAsync"/>（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private async void Login_Click(object sender, RoutedEventArgs e) => await LoginAsync();

    /// <summary>
    /// 校验账号权限：取不到 WebManage 菜单就算没权限。校验期间禁用按钮，避免重复点。
    /// </summary>
    private async Task LoginAsync()
    {
        var userName = UserBox.Text.Trim();
        if (userName.Length == 0)
        {
            ShowError("请输入账号名称");
            return;
        }

        LoginButton.IsEnabled = false;
        ShowHint("正在校验…");

        try
        {
            var count = await _api.GetWebManageCountAsync(userName);

            // 校验期间用户可能已经把窗口关了，这时再设 DialogResult 会抛异常。
            if (!IsLoaded) return;

            if (count <= 0)
            {
                ShowError("该账号没有 WebManage 权限");
                return;
            }

            LoggedInUser = userName;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            if (IsLoaded) ShowError($"校验失败：{ex.Message}");
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    /// <summary>状态行显示灰色提示文字（例如「正在校验…」）。</summary>
    /// <param name="text">要显示的提示文案。</param>
    private void ShowHint(string text)
    {
        MessageText.Foreground = (Brush)FindResource("MutedBrush");
        MessageText.Text = text;
    }

    /// <summary>状态行显示红色错误文字。</summary>
    /// <param name="text">错误原因，例如「该账号没有 WebManage 权限」。</param>
    private void ShowError(string text)
    {
        MessageText.Foreground = (Brush)FindResource("DangerBrush");
        MessageText.Text = text;
    }

    /// <summary>取消登录：直接关掉对话框，账号名不会写回配置（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

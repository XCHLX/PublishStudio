using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PublishStudio.Core;
using PublishStudio.Models;

namespace PublishStudio.Views;

/// <summary>
/// 发布配置对话框。和项目对话框一样：进来的是副本，点「保存」才把值写回去。
/// </summary>
public partial class SettingsDialog : Window
{
    /// <summary>「保留历史发布文件」的一个下拉项。Days = 0 表示不保留。</summary>
    /// <param name="Days">保留天数，0 表示不保留。</param>
    /// <param name="Text">下拉里显示的文字，例如「3 天」。</param>
    private sealed record KeepDayItem(int Days, string Text)
    {
        /// <summary>下拉里显示的文字（如「3 天」「不保留」），<see cref="ComboBox"/> 直接用它的 ToString。</summary>
        /// <returns>给人看的天数文案。</returns>
        public override string ToString() => Text;
    }

    private static readonly KeepDayItem[] KeepDayItems =
    {
        new(0, "不保留"),
        new(1, "1 天"),
        new(3, "3 天"),
        new(7, "7 天"),
        new(15, "15 天"),
        new(30, "30 天"),
    };

    /// <summary>保存后的配置。DialogResult 为 true 时有效。</summary>
    public PublishSettings Result { get; }

    /// <summary>
    /// 打开对话框：把传入配置的副本填进表单（凭据默认掩码），并选中已有的保留天数与双击动作。
    /// </summary>
    /// <param name="current">当前生效的发布配置，内部会 <c>Clone</c> 一份再改。</param>
    public SettingsDialog(PublishSettings current)
    {
        InitializeComponent();

        Result = current.Clone();

        ApiUrlBox.Password = Result.ApiUrl;
        WebUrlBox.Password = Result.WebUrl;
        OssKeyBox.Password = Result.OssKey;
        OssSecretBox.Password = Result.OssSecret;

        KeepDaysBox.ItemsSource = KeepDayItems;
        KeepDaysBox.SelectedItem =
            KeepDayItems.FirstOrDefault(item => item.Days == Result.KeepBuildDays)
            ?? KeepDayItems.First(item => item.Days == PublishSettings.DefaultKeepDays);

        DoubleClickBox.ItemsSource = DoubleClickActions.All;
        DoubleClickBox.SelectedItem = DoubleClickActions.All.Contains(Result.DoubleClickAction)
            ? Result.DoubleClickAction
            : DoubleClickActions.Run;
    }

    /// <summary>保存：把表单值写回 <see cref="Result"/>，然后关闭对话框（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Collect();
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 把表单上当前的值读进 <see cref="Result"/>。保存和导出都先走这一步，
    /// 免得用户改完直接点「导出备份」导出去的是改之前的旧值。
    /// </summary>
    private void Collect()
    {
        Result.ApiUrl = Read(ApiUrlBox, ApiUrlPlainBox).Trim();
        Result.WebUrl = Read(WebUrlBox, WebUrlPlainBox).Trim();
        Result.OssKey = Read(OssKeyBox, OssKeyPlainBox).Trim();
        Result.OssSecret = Read(OssSecretBox, OssSecretPlainBox).Trim();
        Result.KeepBuildDays = (KeepDaysBox.SelectedItem as KeepDayItem)?.Days ?? PublishSettings.DefaultKeepDays;
        Result.DoubleClickAction = DoubleClickBox.SelectedItem as string ?? DoubleClickActions.Run;
    }

    /// <summary>
    /// 「导出备份…」：存成带口令的 .psbak。导出的是这 6 项配置，不含项目清单、
    /// 窗口位置和排序列；因为 settings.json 里的密文换了电脑解不开，走备份文件才带得走。
    /// </summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        Collect();

        var dialog = new SaveFileDialog
        {
            Title = "导出发布配置备份",
            Filter = $"配置备份 (*{ConfigFile.Extension})|*{ConfigFile.Extension}",
            DefaultExt = ConfigFile.Extension,
            AddExtension = true,
            FileName = $"PublishStudio-配置-{DateTime.Now:yyyyMMdd-HHmm}{ConfigFile.Extension}",
        };
        if (dialog.ShowDialog(this) != true) return;

        var password = PasswordWindow.Ask(this, "设置备份口令",
            $"备份里含着 OSS Key / 密钥，用这个口令加密。至少 {ConfigFile.MinimumPasswordLength} 位，" +
            "口令忘了这份备份就再也打不开了。", confirm: true);
        if (password is null) return;

        try
        {
            ConfigFile.Export(dialog.FileName, Result, password);
            MessageBox.Show(this,
                $"已导出到：\n{dialog.FileName}\n\n换电脑时点「导入备份…」选这个文件、输入同一个口令即可恢复。",
                "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 「导入备份…」：选文件 → 输口令 → 解出 6 项 → 确认覆盖 → 填进表单并当作「保存」关闭对话框，
    /// 剩下的写盘由主窗口 <c>OpenPublishSettings</c> 走原来的保存流程。
    /// 项目不在这条链路里动；窗口位置、排序列保持本机原样。
    /// </summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入发布配置备份",
            Filter = $"配置备份 (*{ConfigFile.Extension})|*{ConfigFile.Extension}|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var password = PasswordWindow.Ask(this, "输入备份口令",
            "输入导出这份备份时设置的口令。", confirm: false);
        if (password is null) return;

        BackupPayload payload;
        try
        {
            payload = ConfigFile.Import(dialog.FileName, password);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                ex is InvalidDataException ? ex.Message : $"读取备份失败：{ex.Message}",
                "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var summary =
            $"接口地址：{Secret.Mask(payload.ApiUrl)}\n" +
            $"网页地址：{Secret.Mask(payload.WebUrl)}\n" +
            $"OSS Key：{Secret.Mask(payload.OssKey)}\n" +
            $"OSS 密钥：{Secret.Mask(payload.OssSecret)}\n" +
            $"保留历史发布文件：{(payload.KeepBuildDays == 0 ? "不保留" : $"{payload.KeepBuildDays} 天")}\n" +
            $"双击项目列表：{payload.DoubleClickAction}\n\n" +
            "这会覆盖当前这 6 项配置（项目列表、窗口位置、排序不受影响），确定导入吗？";

        if (MessageBox.Show(this, summary, "确认导入", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        Result.ApiUrl = payload.ApiUrl;
        Result.WebUrl = payload.WebUrl;
        Result.OssKey = payload.OssKey;
        Result.OssSecret = payload.OssSecret;
        Result.KeepBuildDays = payload.KeepBuildDays;
        Result.DoubleClickAction = payload.DoubleClickAction;

        // 直接当「保存」关掉：用户已经确认过要覆盖，再让他多点一次保存只是多一步。
        DialogResult = true;
        Close();
    }

    /// <summary>切换「接口地址」的掩码 / 明文显示（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ToggleApiUrl_Click(object sender, RoutedEventArgs e)
        => Toggle(ApiUrlBox, ApiUrlPlainBox, ApiUrlToggle);

    /// <summary>切换「网页地址」的掩码 / 明文显示（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ToggleWebUrl_Click(object sender, RoutedEventArgs e)
        => Toggle(WebUrlBox, WebUrlPlainBox, WebUrlToggle);

    /// <summary>切换「OSS Key」的掩码 / 明文显示（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ToggleOssKey_Click(object sender, RoutedEventArgs e)
        => Toggle(OssKeyBox, OssKeyPlainBox, OssKeyToggle);

    /// <summary>切换「OSS 密钥」的掩码 / 明文显示（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ToggleOssSecret_Click(object sender, RoutedEventArgs e)
        => Toggle(OssSecretBox, OssSecretPlainBox, OssSecretToggle);

    /// <summary>
    /// 在「掩码框」和「明文框」之间切换。两个控件叠在同一格里，同一时刻只显示一个，
    /// 切换时把值搬过去，所以无论停在哪一边，读哪个都对。
    /// </summary>
    /// <param name="maskBox">掩码控件（<see cref="PasswordBox"/>）。</param>
    /// <param name="plainBox">叠在同一格里的明文控件（<see cref="TextBox"/>）。</param>
    /// <param name="toggle">右侧按钮，文案在「显示 / 隐藏」之间切换。</param>
    private static void Toggle(PasswordBox maskBox, TextBox plainBox, Button toggle)
    {
        if (plainBox.Visibility == Visibility.Visible)
        {
            // 正在显示明文 → 收回去，并把可能改过的内容同步回密码框。
            maskBox.Password = plainBox.Text;
            maskBox.Visibility = Visibility.Visible;
            plainBox.Visibility = Visibility.Collapsed;
            toggle.Content = "显示";
            maskBox.Focus();
            return;
        }

        plainBox.Text = maskBox.Password;
        plainBox.Visibility = Visibility.Visible;
        maskBox.Visibility = Visibility.Collapsed;
        toggle.Content = "隐藏";
        plainBox.Focus();
        plainBox.CaretIndex = plainBox.Text.Length;
    }

    /// <summary>取当前真正显示的那个控件的值：明文模式下用户可能改过内容还没点「隐藏」。</summary>
    /// <param name="maskBox">掩码控件。</param>
    /// <param name="plainBox">明文控件。</param>
    /// <returns>当前可见控件里的文本。</returns>
    private static string Read(PasswordBox maskBox, TextBox plainBox)
        => plainBox.Visibility == Visibility.Visible ? plainBox.Text : maskBox.Password;

    /// <summary>取消：直接关掉对话框，不改动原配置（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace PublishStudio.Views;

/// <summary>
/// 内嵌浏览器窗口：用 WebView2（Chromium 内核）打开发布记录页，
/// 不用每次都跳到系统浏览器。刻意做得很轻：只有网页本身，没有地址栏、刷新、标签页。
/// </summary>
public partial class WebPageWindow : Window
{
    private readonly string _url;

    /// <summary>
    /// 指向同一个用户数据目录的环境只能建一次。
    /// 每开一个窗口都 CreateAsync 一遍的话，第二个（关掉窗口再开、或连点两次菜单）
    /// 会拿不到环境 / 拿到一个白屏的控件 —— 这是 WebView2 的硬性约定。
    /// </summary>
    private static CoreWebView2Environment? _environment;
    private static readonly SemaphoreSlim EnvironmentLock = new(1, 1);

    /// <summary>
    /// 打开内嵌浏览器窗口；窗口加载完成后自动导航到 <paramref name="url"/>，关闭时释放内核。
    /// </summary>
    /// <param name="url">要打开的完整地址（发布记录深链）。</param>
    /// <param name="title">窗口标题，例如「发布记录 - 某项目（测试）」。</param>
    public WebPageWindow(string url, string title)
    {
        InitializeComponent();

        _url = url;
        Title = title;

        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => ReleaseBrowser();
    }

    /// <summary>进程内复用同一个 Environment；第一次真正建，之后直接返回。</summary>
    private static async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_environment is not null) return _environment;

        await EnvironmentLock.WaitAsync();
        try
        {
            return _environment ??= await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataFolder);
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    /// <summary>
    /// 首次调用会拉起 WebView2 的用户数据目录和后台进程，所以是 async 的；
    /// 本机没装 WebView2 运行时的话这里会抛，兜底显示提示面板而不是让窗口白着。
    /// </summary>
    private async Task LoadAsync()
    {
        ShowLoading();

        try
        {
            // 默认的用户数据目录在 exe 旁边，放到只读目录（如 Program Files）就会起不来；
            // 这里统一挪到 %LOCALAPPDATA%\PublishStudio\WebView2，和别的东西放一起。
            var env = await GetEnvironmentAsync();
            await Browser.EnsureCoreWebView2Async(env);

            if (Browser.CoreWebView2 is { } core)
            {
                core.Settings.IsStatusBarEnabled = false;
                core.NavigationCompleted -= OnNavigationCompleted;   // 防重复挂
                core.NavigationCompleted += OnNavigationCompleted;
            }

            Browser.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            ShowStatus($"浏览器内核初始化失败：{ex.Message}\n\n"
                       + "多半是本机没装 WebView2 运行时（Windows 11 一般自带，"
                       + "可从微软官网下载「Evergreen Runtime」安装后重试）。");
        }
    }

    /// <summary>WebView2 的缓存/用户数据放这里，别写在 exe 目录下。</summary>
    private static string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PublishStudio", "WebView2");

    /// <summary>导航结束时的收尾：失败就显示提示面板，成功则等正文出现后再撤掉「加载中」。</summary>
    /// <param name="sender">事件源（未使用）。</param>
    /// <param name="e">导航结果，<c>IsSuccess</c> 为 false 时带错误码。</param>
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowStatus($"页面打不开（{e.WebErrorStatus}）：\n{Browser.Source}\n\n"
                       + "地址是否写错、或者当前网络连不上？可以关掉这个窗口重开试试。");
            return;
        }

        // 导航完成只代表 HTML 到了，这个站点是 Vue 单页应用，
        // 表格还要等接口回来才画出来。不等一下直接切过去，用户看到的仍是白屏，
        // 等于“加载中”提示白做了。这里最多等 2 秒，等到有正文就立刻切换。
        await WaitForContentAsync();
        HideStatus();
    }

    /// <summary>轮询等页面出现正文，最多约 2 秒；超时也照样切换，不卡住用户。</summary>
    private async Task WaitForContentAsync()
    {
        if (Browser.CoreWebView2 is not { } core) return;

        for (var i = 0; i < 8; i++)
        {
            var raw = await core.ExecuteScriptAsync(
                "document.body ? document.body.innerText.trim().length : 0");

            if (int.TryParse(raw, out var length) && length > 20) return;
            await Task.Delay(250);
        }
    }

    /// <summary>页面还没出来时显示的那一行字（同时把浏览器控件藏起来，见 <see cref="ShowStatus"/> 的说明）。</summary>
    private void ShowLoading()
    {
        StatusText.Text = "正在加载发布记录…";
        Browser.Visibility = Visibility.Hidden;
        StatusPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 显示错误/提示面板。
    /// 注意必须先把 WebView2 收起来：它是独立的 HWND，永远画在 WPF 内容之上，
    /// 不然这层提示被它挡在下面，用户只会看到一片白，以为“打不开”。
    /// </summary>
    /// <param name="text">要显示在面板上的文字（错误原因、缺失运行库提示等）。</param>
    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        Browser.Visibility = Visibility.Hidden;
        StatusPanel.Visibility = Visibility.Visible;
    }

    /// <summary>撤掉提示面板，把浏览器控件显示出来（页面已经可以看了）。</summary>
    private void HideStatus()
    {
        StatusPanel.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;
    }

    /// <summary>不显式释放的话，WebView2 的后台进程和用户数据目录句柄会一直挂着。</summary>
    private void ReleaseBrowser()
    {
        try
        {
            Browser.Dispose();
        }
        catch
        {
            // 窗口关闭阶段出异常无所谓，忽略。
        }
    }
}

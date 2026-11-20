using System.Threading;
using System.Windows;
using PublishStudio.Core;
using PublishStudio.Views;

namespace PublishStudio;

public partial class App : Application
{
    /// <summary>单实例互斥量。托盘程序多开会出两个图标，这里挡住第二个。</summary>
    private Mutex? _singleInstance;

    /// <summary>全局唯一的项目清单，界面和托盘共用同一份。</summary>
    public static AppStore Store { get; } = new();

    /// <summary>
    /// 程序入口：先抢单实例互斥量，再读项目清单和发布配置，最后显示主窗口，
    /// 并在后台按「保留历史发布文件」的天数清一次过期的临时编译目录，顺带清掉超期的运行日志。
    /// </summary>
    /// <param name="e">启动参数（当前未使用）。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\PublishStudio.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("发布工具已经在运行了，请从右下角托盘图标打开。", "发布工具",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        Store.Load();
        Store.LoadSettings();
        var window = new MainWindow(Store);
        MainWindow = window;
        window.Show();

        // 启动时顺手清一次过期的临时编译目录。放后台跑，别耽误窗口显示；
        // 天数取「设置 → 发布设置 → 保留历史发布文件」，默认 3 天。
        // 同一个后台任务里再清一遍超期的运行日志（Logs 下一律保留 1 天）。
        Task.Run(() =>
        {
            BuildHistory.Purge(Store.Settings.KeepBuildDays);
            LogFile.Cleanup();
        });
    }

    /// <summary>程序退出：释放单实例互斥量，让下次能正常启动。</summary>
    /// <param name="e">退出参数（当前未使用）。</param>
    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

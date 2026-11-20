using System.Drawing;
using System.Windows.Forms;
using PublishStudio.Models;

namespace PublishStudio.Core;

/// <summary>
/// 托盘菜单里能对单个项目做的动作。和列表「选中项目右键菜单」一一对应，
/// 托盘那边只是把同一个动作换个入口点出来。
/// </summary>
public enum TrayAction
{
    /// <summary>运行：在系统控制台里跑开发命令。</summary>
    Run,

    /// <summary>发布：完整流水线（编译 + 打包 + 上传 + 回调）。</summary>
    Publish,

    /// <summary>编译：只 dotnet publish / npm build，不打包不上传。</summary>
    Build,

    /// <summary>查询发布信息（内务府）。</summary>
    QueryRelease,

    /// <summary>打开内嵌浏览器看发布记录页。</summary>
    OpenReleasePage,

    /// <summary>在项目目录执行 git pull。</summary>
    GitPull,

    /// <summary>用 VS Code 打开项目目录。</summary>
    OpenInVsCode,

    /// <summary>用资源管理器打开项目目录。</summary>
    OpenFolder,

    /// <summary>把项目目录复制到剪贴板。</summary>
    CopyPath,

    /// <summary>编辑项目配置。</summary>
    Edit,

    /// <summary>从项目列表移除（只删配置，不动磁盘）。</summary>
    Remove,
}

/// <summary>
/// 系统托盘的封装。这个文件里只有 WinForms，不引用任何 WPF 类型，
/// 免得 Application / MessageBox / Application 这些同名类型到处冲突。
/// 所有事件都在 UI 线程上触发（NotifyIcon 依附当前线程的消息循环）。
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private ContextMenuStrip? _menu;

    /// <summary>双击托盘图标，或菜单「显示主窗口」。</summary>
    public event Action? ShowWindowRequested;

    /// <summary>菜单「最小化」：把主窗口收回到托盘。</summary>
    public event Action? MinimizeRequested;

    /// <summary>菜单「退出」。</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// 对某个项目执行一个动作。一级菜单是项目的「自定义分组」，二级是项目本身
    /// （展开就是完整动作列表）。
    /// </summary>
    public event Action<Project, TrayAction>? ActionRequested;

    /// <summary>创建托盘图标并挂上双击事件；右键菜单随后由 <see cref="RebuildMenu"/> 建。</summary>
    public TrayManager()
    {
        _icon = AppIcon.Create();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "发布工具",
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke();
    }

    /// <summary>第一次最小化到托盘时给个气泡提示，免得用户以为程序退出了。</summary>
    public void ShowHiddenHint()
    {
        _notifyIcon.BalloonTipTitle = "发布工具仍在运行";
        _notifyIcon.BalloonTipText = "程序已最小化到托盘，双击图标可重新打开。";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3000);
    }

    /// <summary>
    /// 右下角系统气泡。任务成功后日志窗口会自动关掉，结果得有地方说一声。
    /// 走 NotifyIcon 的 BalloonTip，不受窗口是否显示影响；系统里关了通知就看不到，属正常。
    /// </summary>
    /// <param name="title">气泡标题，例如「发布成功」「发布失败」。</param>
    /// <param name="text">气泡正文，一般写项目名和耗时。</param>
    /// <param name="ok">成功用信息图标，失败用错误图标。</param>
    public void ShowResult(string title, string text, bool ok)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = ok ? ToolTipIcon.Info : ToolTipIcon.Error;
        _notifyIcon.ShowBalloonTip(5000);
    }

    /// <summary>
    /// 重建右键菜单。菜单是「当前项目列表」的快照，列表或状态一变就得重建，
    /// 旧菜单显式 Dispose，否则每刷新一次漏一批 GDI 句柄。
    ///
    /// 结构：一级「自定义分组」→ 二级「项目」→ 三级「和列表右键菜单完全一致的动作」。
    /// 分组标题带颜色（和列表里的色点同一个取色规则），未分组的固定排最后、用灰色。
    /// </summary>
    /// <param name="projects">当前要显示的项目，已按列表的排序列排好，组内保持这个顺序。</param>
    public void RebuildMenu(IEnumerable<Project> projects)
    {
        var menu = new ContextMenuStrip
        {
            Font = new Font("Microsoft YaHei UI", 9F),
            ShowImageMargin = false,
        };

        var list = projects as IList<Project> ?? projects.ToList();

        // 分组名去重时忽略大小写，避免 SCRM / scrm 拆成两个组；组本身按拼音排，未分组垫底。
        var groups = list
            .GroupBy(project => project.GroupName.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key.Length == 0 ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.CurrentCulture);

        foreach (var group in groups) menu.Items.Add(BuildGroup(group.Key, group.ToList()));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("显示主窗口", null, (_, _) => ShowWindowRequested?.Invoke());
        menu.Items.Add("最小化", null, (_, _) => MinimizeRequested?.Invoke());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        var old = _menu;
        _menu = menu;
        _notifyIcon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    /// <summary>建一级菜单（一个自定义分组），标题后面带项目数，颜色跟着分组走。</summary>
    /// <param name="group">分组名；空串表示未分组。</param>
    /// <param name="projects">这一组的项目。</param>
    /// <returns>可以直接挂到菜单上的分组项。</returns>
    private ToolStripMenuItem BuildGroup(string group, List<Project> projects)
    {
        var title = group.Length == 0 ? "未分组" : group;

        var item = new ToolStripMenuItem($"{title} ({projects.Count})")
        {
            ForeColor = ColorTranslator.FromHtml(GroupPalette.For(group)),
        };

        if (projects.Count == 0)
        {
            item.DropDownItems.Add(new ToolStripMenuItem("（空）") { Enabled = false });
            return item;
        }

        foreach (var project in projects) item.DropDownItems.Add(BuildProject(project));
        return item;
    }

    /// <summary>
    /// 建二级菜单（一个项目），展开就是三级动作列表。
    /// 项目名后面跟一小段环境标记 —— 一级不再是测试/正式了，这个信息得有个地方露出来。
    /// </summary>
    /// <param name="project">这个菜单项代表的项目。</param>
    /// <returns>带三级子菜单的项目项。</returns>
    private ToolStripMenuItem BuildProject(Project project)
    {
        var env = project.NwfRelease is null
            ? ""
            : project.NwfRelease.IsProduce == true ? "  · 正式" : "  · 测试";

        var busy = project.IsBusy ? $"  ({project.Status})" : "";
        var item = new ToolStripMenuItem(project.Name + env + busy);

        item.DropDownItems.Add(Child("运行", project, TrayAction.Run));
        item.DropDownItems.Add(Child("发布", project, TrayAction.Publish));
        item.DropDownItems.Add(Child("编译", project, TrayAction.Build));
        item.DropDownItems.Add(new ToolStripSeparator());

        item.DropDownItems.Add(Child("git pull", project, TrayAction.GitPull));
        item.DropDownItems.Add(Child("用 VS Code 打开", project, TrayAction.OpenInVsCode));
        item.DropDownItems.Add(Child("打开目录", project, TrayAction.OpenFolder));
        item.DropDownItems.Add(Child("复制目录路径", project, TrayAction.CopyPath));
        item.DropDownItems.Add(new ToolStripSeparator());

        item.DropDownItems.Add(Child("查询发布信息", project, TrayAction.QueryRelease));
        item.DropDownItems.Add(Child("打开发布记录页面", project, TrayAction.OpenReleasePage));
        item.DropDownItems.Add(new ToolStripSeparator());

        item.DropDownItems.Add(Child("编辑…", project, TrayAction.Edit));
        item.DropDownItems.Add(Child("移除", project, TrayAction.Remove));
        return item;
    }

    /// <summary>建三级菜单里的一个动作项。</summary>
    /// <param name="text">菜单文字，和列表右键菜单保持一致。</param>
    /// <param name="project">要作用于哪个项目。</param>
    /// <param name="action">点下去执行的动作。</param>
    /// <returns>挂好事件的菜单项。</returns>
    private ToolStripMenuItem Child(string text, Project project, TrayAction action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => ActionRequested?.Invoke(project, action);
        return item;
    }

    /// <summary>
    /// 释放托盘图标、菜单和 GDI 资源。程序退出前必须调用，否则托盘里会留着幽灵图标。
    /// </summary>
    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip = null;
        _menu?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
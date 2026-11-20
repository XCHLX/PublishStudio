using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PublishStudio.Core;
using PublishStudio.Models;

namespace PublishStudio.Views;

public partial class MainWindow : Window
{
    /// <summary>发布环境筛选项：不筛。</summary>
    private const string ScopeAll = "全部";

    /// <summary>发布环境筛选项：只看非正式（<c>isProduce != true</c>，没查到发布信息的也归这里）。</summary>
    private const string ScopeTest = "测试";

    /// <summary>发布环境筛选项：只看正式（<c>isProduce == true</c>）。</summary>
    private const string ScopeProd = "正式";

    private static readonly string[] ReleaseScopes = { ScopeAll, ScopeTest, ScopeProd };

    /// <summary>分组筛选项：不筛。</summary>
    private const string GroupAll = "全部分组";

    /// <summary>分组筛选项：只看没填分组名的项目。用括号包起来，基本不会和真实分组名撞。</summary>
    private const string GroupNone = "（未分组）";

    /// <summary>分组下拉当前的候选项，用来判断要不要重建 ItemsSource（重建会闪一下、丢选中）。</summary>
    private List<string> _groupOptions = new() { GroupAll };

    private readonly AppStore _store;
    private readonly TrayManager _tray;
    private ICollectionView? _view;

    /// <summary>当前排序的列。点同一列表头时靠它判断该不该反向。</summary>
    private GridViewColumn? _sortColumn;

    /// <summary>当前排序方向，默认升序。</summary>
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    /// <summary>列 → 表头原始文字（不含 ▲ / ▼）。加箭头前先从这儿取原文，免得越加越长。</summary>
    private readonly Dictionary<GridViewColumn, string> _columnTitles = new();

    /// <summary>正在跑的任务：项目 Id → 取消源。同一个项目同时只允许一个任务。</summary>
    private readonly Dictionary<string, CancellationTokenSource> _running = new();

    /// <summary>true 表示走的是「托盘退出」，关闭窗口时不再拦。</summary>
    private bool _reallyExit;

    private bool _hiddenHintShown;

    /// <summary>
    /// 主窗口：接上筛选下拉、列表集合视图、托盘菜单与登录状态，最后默认选中第一行。
    /// </summary>
    /// <param name="store">全局项目清单与发布配置，由 <see cref="App.Store"/> 传入。</param>
    public MainWindow(AppStore store)
    {
        InitializeComponent();
        _store = store;

        TypeFilter.ItemsSource = new List<string> { "全部" }.Concat(ProjectTypes.All).ToList();
        TypeFilter.SelectedIndex = 0;

        // 发布环境筛选：默认只看测试，正式环境要手动切过去（少一次误发布的机会）。
        ReleaseFilter.ItemsSource = ReleaseScopes;
        ReleaseFilter.SelectedItem = ScopeTest;

        // 分组筛选：候选项由现有项目推导，先占一条「全部分组」，RefreshUi 里再补全。
        GroupFilter.ItemsSource = _groupOptions;
        GroupFilter.SelectedItem = GroupAll;

        _view = CollectionViewSource.GetDefaultView(_store.Projects);
        _view.Filter = FilterProject;
        ProjectList.ItemsSource = _view;

        // 表头点击排序。先把每列的原始文字记下来，后面加、去箭头都靠它还原。
        ProjectList.AddHandler(GridViewColumnHeader.ClickEvent,
            new RoutedEventHandler(ProjectList_ColumnHeaderClick));
        if (ProjectList.View is GridView grid)
        {
            foreach (var column in grid.Columns)
                if (column.Header is string title) _columnTitles[column] = title;
        }

        // 恢复上次的排序列和方向；没存过（或配置被手改坏了）就按项目名称升序。
        // 放在托盘创建之后才调：SortBy 会顺手重建一次托盘菜单。
        _tray = new TrayManager();
        _tray.ShowWindowRequested += ShowFromTray;
        _tray.MinimizeRequested += HideToTray;
        _tray.ExitRequested += ExitApp;
        _tray.ActionRequested += HandleTrayAction;

        SortBy(ColumnFor(_store.Settings.SortColumn) ?? NameColumn,
            _store.Settings.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending);

        _store.Projects.CollectionChanged += (_, _) => RefreshUi();

        RefreshUi();
        SyncLoginButton();
        RestoreWindowPlacement();

        // 默认选中第一行，省得用户先点一下才能用右键菜单。
        if (ProjectList.Items.Count > 0) ProjectList.SelectedIndex = 0;
    }

    // ==================== 窗口位置 ====================

    /// <summary>
    /// 还原上次退出时的窗口位置和大小（存在 settings.json 里）。
    /// 存的矩形必须还落在屏幕内才用 —— 换了显示器、拔掉副屏以后，
    /// 原来的坐标可能已经在屏幕外，照搬会导致窗口打开就找不着。
    /// 这种情况保持 XAML 里的默认大小和居中。
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var settings = _store.Settings;

        // 没存过（0）或比最小尺寸还小，当成没存。
        if (settings.WindowWidth < MinWidth || settings.WindowHeight < MinHeight) return;

        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenWidth = SystemParameters.VirtualScreenWidth;
        var screenHeight = SystemParameters.VirtualScreenHeight;

        // 比整个屏幕还大的尺寸收一下，否则标题栏会被顶出屏幕。
        Width = Math.Min(settings.WindowWidth, screenWidth);
        Height = Math.Min(settings.WindowHeight, screenHeight);

        // 判断标准：左边留一点、标题栏（上边 60px 内）必须露在屏幕里，不然鼠标抓不回来。
        var onScreen = settings.WindowLeft >= screenLeft - 8
                       && settings.WindowTop >= screenTop - 8
                       && settings.WindowLeft + 120 <= screenLeft + screenWidth
                       && settings.WindowTop + 60 <= screenTop + screenHeight;

        if (onScreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }

        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// 把当前窗口的位置和大小写进配置。最大化 / 最小化时用 <see cref="Window.RestoreBounds"/>，
    /// 存的是「还原后」的矩形 —— 否则最大化状态下存下来的是整屏尺寸，下次取消最大化就变不回去了。
    /// 隐藏到托盘时也会调这里，所以值没变就直接返回，不重复写盘。
    /// </summary>
    private void SaveWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        if (bounds.IsEmpty || bounds.Width < MinWidth || bounds.Height < MinHeight) return;

        var settings = _store.Settings;
        var maximized = WindowState == WindowState.Maximized;

        var unchanged = Math.Abs(settings.WindowLeft - bounds.Left) < 0.5
                        && Math.Abs(settings.WindowTop - bounds.Top) < 0.5
                        && Math.Abs(settings.WindowWidth - bounds.Width) < 0.5
                        && Math.Abs(settings.WindowHeight - bounds.Height) < 0.5
                        && settings.WindowMaximized == maximized;

        if (unchanged) return;

        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowMaximized = maximized;
        _store.SaveSettings();
    }

    // ==================== 列表与筛选 ====================

    /// <summary>
    /// 列表筛选：类型下拉 + 发布环境下拉 + 分组下拉 + 搜索关键字，四者都满足才显示。
    /// 发布环境按缓存里的 isProduce 判断，没查过发布信息的算「测试」。
    /// </summary>
    /// <param name="item">集合视图传进来的元素，非 <see cref="Project"/> 一律过滤掉。</param>
    /// <returns>该行应当显示返回 true，否则 false。</returns>
    private bool FilterProject(object item)
    {
        if (item is not Project project) return false;

        var type = TypeFilter?.SelectedItem as string ?? "全部";
        if (type != "全部" && project.Type != type) return false;

        // 正式 / 测试按发布信息里的 isProduce 判断；没查到发布信息的（含没填发布 ID 的）算测试。
        var scope = ReleaseFilter?.SelectedItem as string ?? ScopeTest;
        var isProduce = project.NwfRelease?.IsProduce == true;
        if (scope == ScopeProd && !isProduce) return false;
        if (scope == ScopeTest && isProduce) return false;

        // 分组：按名字比，大小写不敏感（避免 SCRM / scrm 分成两个组）。
        var group = GroupFilter?.SelectedItem as string ?? GroupAll;
        if (group != GroupAll)
        {
            var name = project.GroupName.Trim();
            var matched = group == GroupNone
                ? name.Length == 0
                : string.Equals(name, group, StringComparison.CurrentCultureIgnoreCase);

            if (!matched) return false;
        }

        var keyword = SearchBox?.Text?.Trim() ?? "";
        if (keyword.Length == 0) return true;

        return project.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || project.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || project.GroupName.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>搜索框内容变化后重刷筛选结果（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilter();

    /// <summary>类型下拉变化后重刷筛选结果（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void TypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshFilter();

    /// <summary>正式/测试下拉变化后重刷筛选结果（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ReleaseFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshFilter();

    /// <summary>分组下拉变化后重刷筛选结果（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void GroupFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshFilter();

    /// <summary>
    /// 重建「分组」下拉的候选项：现有项目里出现过的分组名，去重 + 拼音排序，
    /// 另外在末尾补一条「（未分组）」—— 只有真的存在没分组的项目时才出现。
    /// 候选项没变化就只动选中项，免得每次刷新都把下拉重置一遍。
    /// </summary>
    private void RefreshGroupFilter()
    {
        if (GroupFilter is null) return;

        var names = _store.Projects
            .Select(project => project.GroupName.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCulture)
            .ToList();

        var options = new List<string> { GroupAll };
        options.AddRange(names);
        if (_store.Projects.Any(project => project.GroupName.Trim().Length == 0)) options.Add(GroupNone);

        var current = GroupFilter.SelectedItem as string ?? GroupAll;

        if (!options.SequenceEqual(_groupOptions))
        {
            _groupOptions = options;
            GroupFilter.ItemsSource = options;   // 换数据源会把选中项清掉，下面补回来
        }

        // 当前选中的分组被改没了（最后一个项目改了分组），回落到「全部分组」。
        GroupFilter.SelectedItem = options.Contains(current) ? current : GroupAll;
    }

    /// <summary>搜索框 / 两个筛选下拉变动后只重刷列表和状态栏，不动托盘菜单。</summary>
    private void RefreshFilter()
    {
        _view?.Refresh();

        // 筛完之后剩下的项目不一样了，列宽按新内容重排一次。
        AutoFitColumns();

        UpdateListHint();
    }

    /// <summary>
    /// 点击表头排序：同一列再点一次反向（升 ↑ → 降 ↓），换列则从升序开始。
    /// 表头右侧拖列宽、点空白也会冒泡上来，这些不算点击。
    /// </summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数，<c>OriginalSource</c> 里是真正被点的那个表头。</param>
    private void ProjectList_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        if (header.Role == GridViewColumnHeaderRole.Padding) return;

        var column = header.Column;
        if (column is null || SortProperty(column) is null) return;

        var direction = column == _sortColumn && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        SortBy(column, direction);
    }

    /// <summary>列 → 排序用的属性名。返回 null 表示这列不参与排序（只有右侧的空白占位列）。</summary>
    /// <param name="column">被点的列。</param>
    /// <returns>可排序返回属性名，否则 null。</returns>
    private string? SortProperty(GridViewColumn column)
    {
        if (column == NameColumn) return nameof(Project.Name);
        if (column == ReleaseNameColumn) return nameof(Project.ReleaseName);
        if (column == GroupColumn) return nameof(Project.GroupName);
        if (column == ReleaseEnvColumn) return nameof(Project.ReleaseEnvironment);
        if (column == StatusColumn) return nameof(Project.Status);
        if (column == LastReleaseColumn) return nameof(Project.LastReleaseAt);
        if (column == TypeColumn) return nameof(Project.Type);
        return null;
    }

    /// <summary>属性名 → 列，用来还原上次的排序列。认不出来（配置被手改过）返回 null。</summary>
    /// <param name="property">存进 settings.json 的属性名。</param>
    /// <returns>对应的列；不认识的属性名返回 null。</returns>
    private GridViewColumn? ColumnFor(string property) => property switch
    {
        nameof(Project.Name) => NameColumn,
        nameof(Project.ReleaseName) => ReleaseNameColumn,
        nameof(Project.GroupName) => GroupColumn,
        nameof(Project.ReleaseEnvironment) => ReleaseEnvColumn,
        nameof(Project.Status) => StatusColumn,
        nameof(Project.LastReleaseAt) => LastReleaseColumn,
        nameof(Project.Type) => TypeColumn,
        _ => null,
    };

    /// <summary>按当前排序列取出一行用来比较的文字。托盘菜单排序用它，口径和列表一致。</summary>
    /// <param name="project">要取排序文字的项目。</param>
    /// <returns>当前排序列的值；还没排过时按项目名称。</returns>
    private string SortText(Project project) => _sortColumn switch
    {
        var column when column == NameColumn => project.Name,
        var column when column == ReleaseNameColumn => project.ReleaseName,
        var column when column == GroupColumn => project.GroupName,
        var column when column == ReleaseEnvColumn => project.ReleaseEnvironment,
        var column when column == StatusColumn => project.Status,
        var column when column == LastReleaseColumn => project.LastReleaseText,
        var column when column == TypeColumn => project.Type,
        _ => project.Name,
    };

    /// <summary>
    /// 列表和托盘共用的排序比较器：空串不参与排序，升序降序都沉到最底下；
    /// 其余按当前区域性比较（简中环境下中文是拼音序）。
    /// 同值时再按项目名称兜底，否则每次重排同值那几行的顺序都会跳。
    /// </summary>
    /// <returns>按当前 <see cref="_sortColumn"/> / <see cref="_sortDirection"/> 排好的比较器。</returns>
    private Comparer<Project> BuildComparer()
    {
        var sign = _sortDirection == ListSortDirection.Ascending ? 1 : -1;

        return Comparer<Project>.Create((a, b) =>
        {
            var left = SortText(a);
            var right = SortText(b);

            // 空串沉底：两个都空就按名称兜底，一个空就固定排后面（跟方向无关）。
            if (left.Length == 0 || right.Length == 0)
            {
                if (left.Length == 0 && right.Length == 0) return CompareName(a, b);
                return left.Length == 0 ? 1 : -1;
            }

            var result = string.Compare(left, right, StringComparison.CurrentCulture);
            return result != 0 ? sign * result : CompareName(a, b);
        });
    }

    /// <summary>同值兜底：按项目名称比较，保证排序结果稳定。</summary>
    /// <param name="a">左边的项目。</param>
    /// <param name="b">右边的项目。</param>
    /// <returns>负数表示 a 在前。</returns>
    private static int CompareName(Project a, Project b)
        => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);

    /// <summary>
    /// 按某列排序，并在表头文字后面标 ▲ / ▼。字符串按当前区域性比较，简中环境下中文是拼音序。
    /// 空串永远沉底（升序降序都一样）。只影响列表显示，不动 <see cref="AppStore.Projects"/> 里的存储顺序；
    /// 排序列会记进 settings.json，托盘菜单也跟着排一遍。
    /// </summary>
    /// <param name="column">要排序的列。</param>
    /// <param name="direction">升序或降序。</param>
    private void SortBy(GridViewColumn column, ListSortDirection direction)
    {
        var property = SortProperty(column);
        if (_view is null || property is null) return;

        _sortColumn = column;
        _sortDirection = direction;

        using (_view.DeferRefresh())
        {
            // 用 CustomSort 而不是 SortDescriptions：后者没法让空串固定沉底。
            _view.SortDescriptions.Clear();
            if (_view is ListCollectionView list) list.CustomSort = BuildComparer();
        }

        // 表头箭头：所有列先还原成原文字，再给当前列加箭头。
        foreach (var (col, title) in _columnTitles)
        {
            col.Header = col != column
                ? title
                : title + (direction == ListSortDirection.Ascending ? " ▲" : " ▼");
        }

        SaveSort(property);

        // 托盘的菜单是重建出来的快照，排序列变了它不会自己跟着变。
        RefreshTrayMenu();
    }

    /// <summary>把当前排序列和方向记进 settings.json，下次打开还原。没变化时不写文件。</summary>
    /// <param name="property">当前排序列的属性名。</param>
    private void SaveSort(string property)
    {
        var descending = _sortDirection == ListSortDirection.Descending;
        if (_store.Settings.SortColumn == property && _store.Settings.SortDescending == descending) return;

        _store.Settings.SortColumn = property;
        _store.Settings.SortDescending = descending;
        _store.SaveSettings();
    }

    /// <summary>
    /// 宽度固定的五列：「分组」88 + 「类型」80 + 「发布环境」90 + 「状态」104 + 「上次发布时间」130。
    /// </summary>
    private const double FixedColumnWidth = 88 + 80 + 90 + 104 + 130;

    /// <summary>单元格左右各 10 的内边距（<c>ListViewItem.Padding</c>），量文本时要补上。</summary>
    private const double CellPadding = 20;

    /// <summary>「项目名称」列宽的下限和上限，超过上限就靠省略号截断。</summary>
    private const double NameMin = 140;
    private const double NameMax = 320;

    /// <summary>「发布名称」列宽的下限和上限（中文名偏长，上限给宽一些）。</summary>
    private const double ReleaseMin = 140;
    private const double ReleaseMax = 420;

    /// <summary>单元格字号，与 <c>ListViewItem</c> 的样式保持一致，量文本要用同一个值。</summary>
    private const double CellFontSize = 13;

    /// <summary>表头字号，与 <c>GridViewColumnHeader</c> 的样式保持一致。</summary>
    private const double HeaderFontSize = 12;

    /// <summary>列表内部的 ScrollViewer，用来取真正能放内容的宽度（已扣掉竖向滚动条）。</summary>
    private ScrollViewer? _listScroll;

    /// <summary>
    /// 窗口尺寸变化时重排列宽（事件处理器）。列表的横向滚动条已禁用，
    /// 所以这两列必须吃掉所有余量，宁可文字被截断也不出横向滚动条。
    /// </summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ProjectList_SizeChanged(object sender, SizeChangedEventArgs e) => AutoFitColumns();

    /// <summary>
    /// 列表加载完成：从模板里捞出内部 ScrollViewer，订阅它的滚动事件，再按当前宽度排一次列宽。
    /// </summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ProjectList_Loaded(object sender, RoutedEventArgs e)
    {
        // ListView 的 ScrollViewer 藏在模板里，取到它才能知道内容区实际有多宽
        // （竖向滚动条出现时表头行会跟着变窄）。
        _listScroll = FindScrollViewer(ProjectList);
        if (_listScroll is not null) _listScroll.ScrollChanged += (_, _) => AutoFitColumns();

        AutoFitColumns();
    }

    /// <summary>
    /// 按内容重排「项目名称」「发布名称」两列：量出内容实际需要多宽（含表头），
    /// 夹在上下限之间就定下来，剩下的宽度交给末尾的空白占位列，表头依旧铺到右边界。
    ///
    /// 为什么不用 <c>Width="Auto"</c>：GridView 的自动列没有上限，遇到超长项目名会把后面
    /// 四列挤出可视区。这里自己量，上限之外交给省略号。
    /// 尺寸没变时直接返回，避免 ScrollChanged 自我触发成死循环。
    /// </summary>
    private void AutoFitColumns()
    {
        if (NameColumn is null || ReleaseNameColumn is null || FillerColumn is null) return;

        // ViewportWidth 已经扣掉了竖向滚动条，表头行用的就是它。
        // 再减 1 是取整余量：加起来刚好等于 viewport 时，个别 DPI 下会多出零点几像素而撑出滚动条。
        var available = (_listScroll?.ViewportWidth ?? ProjectList.ActualWidth) - FixedColumnWidth - 1;
        if (available <= 0) return;

        // 只量当前还在列表里的项目（搜索 / 筛选之后量的是筛完的结果）。
        var projects = ProjectList.Items.OfType<Project>().ToList();

        var name = Clamp(
            MeasureColumn(projects.Select(p => p.Name), "项目名称"),
            NameMin, NameMax);

        var release = Clamp(
            MeasureColumn(projects.Select(p => p.ReleaseName), "发布名称"),
            ReleaseMin, ReleaseMax);

        // 可用宽度连两个下限都放不下：只能按比例硬分，文字会被截掉，但保证不横向溢出。
        if (available <= NameMin + ReleaseMin)
        {
            name = Math.Round(available * 0.45);
            release = available - name;
        }
        else if (name + release > available)
        {
            // 内容比窗口宽：从宽的那列开始缩，缩到下限为止。
            var overflow = name + release - available;
            var shrinkable = Math.Min(overflow, name - NameMin);
            name -= shrinkable;
            release = Math.Max(ReleaseMin, available - name);
            name = available - release;
        }

        name = Math.Round(name);
        release = Math.Round(release);

        // 剩下的宽度交给末尾的空白占位列，表头才能一直铺到右边界；
        // 两列本身只按内容宽排，不会被窗口拉宽成大片空白。
        var filler = Math.Max(0, Math.Floor(available - name - release));

        // 值没变就别碰，否则会不停触发 ScrollChanged 变成死循环。
        if (Math.Abs(NameColumn.Width - name) < 0.5
            && Math.Abs(ReleaseNameColumn.Width - release) < 0.5
            && Math.Abs(FillerColumn.Width - filler) < 0.5) return;

        NameColumn.Width = name;
        ReleaseNameColumn.Width = release;
        FillerColumn.Width = filler;
    }

    /// <summary>
    /// 量一列需要多宽：取「表头文字」和「所有单元格文字」里最宽的那个，再加单元格内边距。
    /// 空值不参与比较，全部为空时按表头算。
    /// </summary>
    /// <param name="cells">这一列每个单元格的文字。</param>
    /// <param name="header">表头文字。</param>
    /// <returns>内容实际需要的宽度（不含上下限约束）。</returns>
    private double MeasureColumn(IEnumerable<string?> cells, string header)
    {
        var dpi = VisualTreeHelper.GetDpi(ProjectList).PixelsPerDip;

        var widest = MeasureText(header, HeaderFontSize, dpi);
        foreach (var text in cells)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            var width = MeasureText(text, CellFontSize, dpi);
            if (width > widest) widest = width;
        }

        return widest + CellPadding;
    }

    /// <summary>量一段文字渲染出来有多宽，字体与列表保持一致（微软雅黑）。</summary>
    /// <param name="text">要量的文字，不能为 null。</param>
    /// <param name="fontSize">字号。</param>
    /// <param name="pixelsPerDip">当前 DPI 缩放比，必须传真实值，否则 125% / 150% 下会量偏。</param>
    /// <returns>文字宽度，单位与列宽一致（设备无关像素）。</returns>
    private static double MeasureText(string text, double fontSize, double pixelsPerDip)
    {
        var typeface = new Typeface(
            new FontFamily("Microsoft YaHei UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var formatted = new FormattedText(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, fontSize, Brushes.Black, pixelsPerDip);

        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>把数值夹在上下限之间。</summary>
    /// <param name="value">原始值。</param>
    /// <param name="min">下限。</param>
    /// <param name="max">上限。</param>
    /// <returns>夹好的值。</returns>
    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;

    /// <summary>在可视树里递归找第一个 <see cref="ScrollViewer"/>，用来拿列表内容区的真实宽度。</summary>
    /// <param name="root">从哪里开始找，通常是 ListView 本身。</param>
    /// <returns>找到的 ScrollViewer；没有则返回 null。</returns>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }

        return null;
    }

    /// <summary>列表、空态提示、状态栏、分组筛选候选、托盘菜单一起刷新。任何数据变动都走这里。</summary>
    private void RefreshUi()
    {
        _view?.Refresh();

        // 项目名 / 发布名称 / 分组变了（编辑、刷新发布信息），列宽和分组下拉都要跟着重排。
        AutoFitColumns();
        RefreshGroupFilter();
        UpdateListHint();
        RefreshTrayMenu();
    }

    /// <summary>空态提示 + 状态栏计数。列表被筛空时也要说话，不然看着像项目丢了。</summary>
    private void UpdateListHint()
    {
        var total = _store.Projects.Count;
        var visible = _view?.Cast<object>().Count() ?? total;

        if (total == 0)
        {
            EmptyHint.Text = "还没有项目，按 Ctrl+N 或菜单「文件 → 新建项目」开始";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else if (visible == 0)
        {
            EmptyHint.Text = "当前搜索 / 筛选条件下没有项目";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }

        var running = _running.Count;
        StatusBarText.Text = $"共 {total} 个项目"
                             + (visible != total ? $" · 当前显示 {visible} 个" : "")
                             + (running > 0 ? $" · 正在运行 {running} 个任务" : "");
    }

    /// <summary>
    /// 把当前项目列表整份同步给托盘右键菜单（菜单是快照，数据一变就得重建）。
    /// 顺序跟列表的当前排序走，但**不受搜索 / 类型 / 环境筛选影响**——
    /// 托盘本来就是不看窗口也能发版的入口，项目被筛掉就发不出去了。
    /// </summary>
    private void RefreshTrayMenu()
    {
        var projects = _store.Projects.ToList();
        projects.Sort(BuildComparer());   // 和列表用同一个比较器，顺序才完全一致

        _tray.RebuildMenu(projects);
    }

    private Project? Selected => ProjectList.SelectedItem as Project;

    // ==================== 任务 ====================

    /// <summary>
    /// 起一个「编译」或「发布」任务：查重 → 发布前预检（只有发现问题才弹窗）→ 正式环境二次确认 → 开日志窗口 → 后台跑流水线 →
    /// 结束后更新状态、关窗口并弹托盘气泡。同一项目同时只允许一个任务。
    /// </summary>
    /// <param name="project">要处理的项目。</param>
    /// <param name="pack">true 走完整发布（上传 OSS + 回调内务府）；false 只编译。</param>
    private async void StartTask(Project project, bool pack)
    {
        if (_running.ContainsKey(project.Id))
        {
            var busyMessage = $"「{project.Name}」正在{project.Status}，等它结束再来。";
            if (IsVisible) MessageBox.Show(this, busyMessage, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            else MessageBox.Show(busyMessage, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 发布前先本地体检一遍：目录、构建命令、产物目录、发布 ID、接口与凭据。
        // 只在真有问题（fail）时才弹窗；只是提醒（warn）就静默放行，别每次都拦一下。
        if (pack)
        {
            var issues = Preflight.Check(project, _store.Settings);
            if (issues.Any(item => item.Level == Preflight.Fail)
                && !PreflightWindow.Confirm(this, project.Name, issues)) return;
        }

        // 正式环境是危险操作，先确认再动手。
        if (pack && !await ConfirmProductionAsync(project)) return;

        var actionName = pack ? "发布" : "编译";
        var cancellation = new CancellationTokenSource();
        var logWindow = new LogWindow(project.Name, actionName, cancellation);

        // 主窗口可能已经隐藏到托盘，这时候不能设 Owner，否则日志窗口也跟着看不见。
        if (IsVisible) logWindow.Owner = this;
        logWindow.Show();

        _running[project.Id] = cancellation;
        project.Status = pack ? "发布中" : "编译中";
        RefreshUi();

        _ = Task.Run(async () =>
        {
            var ok = false;
            try
            {
                ok = pack
                    ? await Publisher.ReleaseAsync(project, _store.Settings, logWindow.Append, cancellation.Token)
                    : await Publisher.BuildAsync(project, logWindow.Append, cancellation.Token);
            }
            catch (Exception ex)
            {
                logWindow.Append($"[错误] {ex.Message}");
            }

            Dispatcher.Invoke(() =>
            {
                var cancelled = cancellation.IsCancellationRequested;
                var seconds = (int)Math.Round(logWindow.Elapsed.TotalSeconds);

                project.Status = ok ? "成功" : cancelled ? "已取消" : "失败";
                project.ElapsedSeconds = seconds;   // 状态列显示成「成功 · 83s」

                // 只有真正发布成功才记「上次发布时间」，编译 / git pull 不算。
                if (ok && pack)
                {
                    project.LastReleaseAt = DateTime.Now;
                    _store.Save();
                }

                logWindow.Finish(ok, ok
                    ? $"{actionName}完成"
                    : cancelled ? "已取消" : $"{actionName}失败");

                _running.Remove(project.Id);
                cancellation.Dispose();
                RefreshUi();

                // 成功时日志里没有需要看的东西，窗口自动关掉，改在右下角弹提示；
                // 失败或取消一律留着，方便对着日志排查。两种结果都会弹气泡，
                // 主窗口在托盘里时也能知道任务结束了。
                if (ok)
                {
                    logWindow.Close();
                    _tray.ShowResult($"{actionName}成功",
                        $"「{project.Name}」{actionName}完成，用时 {logWindow.Elapsed.TotalSeconds:F0} 秒", ok: true);
                }
                else
                {
                    _tray.ShowResult(cancelled ? $"{actionName}已取消" : $"{actionName}失败",
                        cancelled
                            ? $"「{project.Name}」{actionName}被中止"
                            : $"「{project.Name}」{actionName}失败，点开日志窗口看原因",
                        ok: false);
                }
            });
        });
    }

    /// <summary>跑一条不经过发布流水线的命令（git pull 之类），同样占用项目锁、同样有日志窗口。</summary>
    /// <param name="project">命令在哪个项目目录里执行，也用它做任务互斥。</param>
    /// <param name="commandLine">要执行的完整命令行。</param>
    /// <param name="actionName">动作名，显示在日志窗口标题栏和项目状态列里。</param>
    private void RunQuickCommand(Project project, string commandLine, string actionName)
    {
        if (_running.ContainsKey(project.Id))
        {
            var busyMessage = $"「{project.Name}」正在{project.Status}，等它结束再来。";
            if (IsVisible) MessageBox.Show(this, busyMessage, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            else MessageBox.Show(busyMessage, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var workDir = AppStore.ResolveFolder(project.Path);
        if (!Directory.Exists(workDir))
        {
            var message = $"项目目录不存在：{workDir}";
            if (IsVisible) MessageBox.Show(this, message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            else MessageBox.Show(message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cancellation = new CancellationTokenSource();
        var logWindow = new LogWindow(project.Name, actionName, cancellation);
        if (IsVisible) logWindow.Owner = this;
        logWindow.Show();

        _running[project.Id] = cancellation;
        project.Status = actionName;
        RefreshUi();

        _ = Task.Run(() =>
        {
            var code = -1;
            try
            {
                logWindow.Append($"工作目录: {workDir}");
                logWindow.Append($"执行命令: {commandLine}");
                logWindow.Append("------------------------------------------------------------");
                code = ProcessRunner.Run(commandLine, workDir, logWindow.Append, cancellation.Token);
            }
            catch (Exception ex)
            {
                logWindow.Append($"[错误] {ex.Message}");
            }

            Dispatcher.Invoke(() =>
            {
                var cancelled = cancellation.IsCancellationRequested;
                var ok = code == 0;

                project.Status = ok ? "成功" : cancelled ? "已取消" : "失败";
                project.ElapsedSeconds = (int)Math.Round(logWindow.Elapsed.TotalSeconds);
                logWindow.Finish(ok, ok ? $"{actionName}完成" : cancelled ? "已取消" : $"失败（退出码 {code}）");

                _running.Remove(project.Id);
                cancellation.Dispose();
                RefreshUi();
            });
        });
    }

    // ==================== 菜单 ====================

    /// <summary>Ctrl+N 新建项目。用隧道事件，焦点落在搜索框里也能触发。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.N || Keyboard.Modifiers != ModifierKeys.Control) return;

        NewProject();
        e.Handled = true;
    }

    /// <summary>菜单「文件 → 新建项目」的入口（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void NewProject_Click(object sender, RoutedEventArgs e) => NewProject();

    /// <summary>弹出新建对话框，确认后把项目加进清单并保存。</summary>
    private void NewProject()
    {
        var dialog = new ProjectDialog(null, _store.Settings.ApiUrl, _store.Projects.Select(p => p.GroupName))
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true) return;

        _store.Projects.Add(dialog.Result);
        _store.Save();
        RefreshUi();
    }

    /// <summary>菜单「设置 → 设置发布配置」的入口（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void PublishSettings_Click(object sender, RoutedEventArgs e) => OpenPublishSettings();

    /// <summary>
    /// 弹出发布配置对话框，确认后写回全局配置并保存；
    /// 保留天数可能被改小了，顺手在后台按新值清一次历史目录。
    /// </summary>
    private void OpenPublishSettings()
    {
        var dialog = new SettingsDialog(_store.Settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _store.Settings.CopyFrom(dialog.Result);
        _store.SaveSettings();

        // 接口地址可能被改过（包括从备份导入），登录按钮的可用性跟着它变。
        SyncLoginButton();

        // 保留天数可能被改小了，立刻按新值清一次，不用等下次启动。
        var keepDays = _store.Settings.KeepBuildDays;
        Task.Run(() => BuildHistory.Purge(keepDays));
    }

    // ==================== 登录 ====================

    /// <summary>
    /// 登录按钮：先确认接口地址已配置，再弹登录窗口；校验通过后把账号名写进配置并刷新按钮文案。
    /// </summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Login_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureApiConfigured()) return;

        var dialog = new LoginWindow(new NwfApi(_store.Settings.ApiUrl), _store.Settings.UserName)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.LoggedInUser is null) return;

        _store.Settings.UserName = dialog.LoggedInUser;
        _store.SaveSettings();
        SyncLoginButton();
    }

    /// <summary>
    /// 登录按钮两种状态：未登录是主色实心的「登录」，登录后切成白底描边、直接显示账号名。
    /// </summary>
    private void SyncLoginButton()
    {
        var userName = _store.Settings.UserName;
        var loggedIn = !string.IsNullOrWhiteSpace(userName);

        LoginButton.Content = loggedIn ? userName : "登录";
        LoginButton.Style = (Style)FindResource(loggedIn ? "GhostButton" : "PrimaryButton");
    }

    /// <summary>依赖内务府接口的功能（登录等）执行前统一走这里：地址没配就弹框并把人送到配置界面。</summary>
    private bool EnsureApiConfigured()
    {
        if (!string.IsNullOrWhiteSpace(_store.Settings.ApiUrl)) return true;

        var go = MessageBox.Show(this,
            "还没有配置内务府接口地址，登录功能用不了。\n\n是否现在去「设置 → 设置发布配置」里填写？",
            "接口地址未配置", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (go != MessageBoxResult.Yes) return false;

        OpenPublishSettings();
        return !string.IsNullOrWhiteSpace(_store.Settings.ApiUrl);
    }

    // ==================== 发布信息 ====================

    /// <summary>
    /// 查一次内务府发布信息并存进项目（列表的「发布环境」列读的就是它）。
    /// silent=true 时失败不弹框，用于保存项目后和发布前的自动查询。
    /// </summary>
    /// <param name="project">要查的项目，用它的发布 ID 调接口。</param>
    /// <param name="silent">true 表示失败只返回 null、不弹提示框。</param>
    /// <returns>查到的发布信息；发布 ID 为空、接口地址没配、查询失败或查不到时返回 null。</returns>
    private async Task<NwfRelease?> RefreshReleaseAsync(Project project, bool silent)
    {
        if (string.IsNullOrWhiteSpace(project.NwfReleaseId))
        {
            // 发布 ID 被清掉了，旧的发布信息就没有意义了，顺手抹掉，免得列表还显示上次的正式/测试。
            if (project.NwfRelease is not null)
            {
                project.NwfRelease = null;
                _store.Save();
            }

            if (!silent) Notify("这个项目没填内务府发布 ID，查不到发布信息。右键「编辑…」补上。",
                "查询发布信息", MessageBoxImage.Warning);
            return null;
        }

        if (string.IsNullOrWhiteSpace(_store.Settings.ApiUrl))
        {
            if (!silent) Notify("还没有配置内务府接口地址，先在「设置 → 设置发布配置」里填写。",
                "查询发布信息", MessageBoxImage.Warning);
            return null;
        }

        NwfRelease? release;
        try
        {
            var node = await new NwfApi(_store.Settings.ApiUrl).GetIisManagerAsync(project.NwfReleaseId);
            release = node?.Deserialize<NwfRelease>();
        }
        catch (Exception ex)
        {
            if (!silent) Notify($"查询发布信息失败：{ex.Message}", "查询发布信息", MessageBoxImage.Warning);
            return null;
        }

        if (release is null)
        {
            if (!silent) Notify($"发布 ID「{project.NwfReleaseId}」查不到发布信息，请确认 ID 是否正确。",
                "查询发布信息", MessageBoxImage.Warning);
            return null;
        }

        project.NwfRelease = release;
        _store.Save();
        return release;
    }

    /// <summary>
    /// 正式环境二次确认。发布前重新查一次发布信息（服务端可能刚把项目改成正式环境，
    /// 不能只信本地缓存），isProduce == true 时弹「危险操作」确认框，点「否」直接中止。
    /// </summary>
    /// <param name="project">即将发布的项目，用来重新查发布信息。</param>
    /// <returns>可以继续发布返回 true；用户点了「否」返回 false。</returns>
    private async Task<bool> ConfirmProductionAsync(Project project)
    {
        var release = await RefreshReleaseAsync(project, silent: true) ?? project.NwfRelease;
        if (release?.IsProduce != true) return true;

        var message = "即将执行发布操作，请确认：\n\n"
                      + "环境：正式 💥\n"
                      + $"项目：{release.Name ?? project.Name}\n"
                      + $"服务器：{release.IpAddress}\n\n"
                      + "是否继续？";

        var result = IsVisible
            ? MessageBox.Show(this, message, "⚠️ 发布确认（危险操作）",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(message, "⚠️ 发布确认（危险操作）",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    /// <summary>弹提示。主窗口可能藏在托盘里，这时不设 Owner，否则提示框也跟着看不见。</summary>
    /// <param name="message">提示正文。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="icon">图标类型，默认信息图标。</param>
    private void Notify(string message, string title, MessageBoxImage icon = MessageBoxImage.Information)
    {
        if (IsVisible) MessageBox.Show(this, message, title, MessageBoxButton.OK, icon);
        else MessageBox.Show(message, title, MessageBoxButton.OK, icon);
    }

    /// <summary>菜单「帮助 → 显示发布配置」：在资源管理器里选中 settings.json（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ShowPublishSettings_Click(object sender, RoutedEventArgs e)
        => Reveal(_store.SettingsFile,
            $"还没有发布配置文件，先在「设置 → 设置发布配置」里保存一次。\n\n{_store.SettingsFile}");

    /// <summary>菜单「帮助 → 显示项目配置」：在资源管理器里选中 projects.json（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ShowProjectConfig_Click(object sender, RoutedEventArgs e)
        => Reveal(_store.ProjectsFile,
            $"还没有项目配置文件，添加一个项目后就会生成。\n\n{_store.ProjectsFile}");

    /// <summary>菜单「帮助 → 显示临时文件夹」：打开 %TEMP%\PublishStudio（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ShowTempFolder_Click(object sender, RoutedEventArgs e)
        => Reveal(Publisher.TempRoot,
            $"还没有打包临时目录，编译过一次 .NET 项目后才会生成。\n\n{Publisher.TempRoot}");

    /// <summary>菜单「帮助 → 打开日志文件夹」：打开 %TEMP%\PublishStudio\Logs（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ShowLogFolder_Click(object sender, RoutedEventArgs e)
        => Reveal(LogFile.Folder,
            $"还没有日志文件，编译或发布跑过一次后才会生成。\n\n{LogFile.Folder}");

    /// <summary>菜单「帮助 → 关于」：弹出版本、作者、运行环境和目录信息（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow(_store) { Owner = this }.ShowDialog();
    }

    /// <summary>在资源管理器里定位文件（选中）或打开目录。文件还没生成时提示一句，不静默失败。</summary>
    /// <param name="path">要展示的文件或目录全路径。</param>
    /// <param name="missingHint">路径不存在时显示的提示文案（通常顺便带上路径）。</param>
    private void Reveal(string path, string missingHint)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            MessageBox.Show(this, missingHint, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AppStore.OpenPath(path);
    }

    // ==================== 列表交互 ====================

    /// <summary>
    /// 双击列表行：按「设置 → 设置发布配置 → 双击项目列表时触发」分发到
    /// 运行 / 发布 / 编译，默认运行。只有双击落在项目行上才算，
    /// 双击表头（排序）、双击空白处都不动作。
    /// </summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数，用 <c>OriginalSource</c> 判断双击落在哪一行。</param>
    private void ProjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 表头、表头右侧空白、列表底部空白都会把双击冒泡到这里，
        // 在那儿误触发一次运行 / 发布代价太大，所以必须确认点在项目行上。
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(ProjectList, source) is not ListViewItem) return;

        if (Selected is not { } project) return;

        // 具体做什么由「设置 → 设置发布配置 → 双击项目列表时触发」决定，默认运行。
        var action = _store.Settings.DoubleClickAction;
        if (string.IsNullOrWhiteSpace(action)) action = DoubleClickActions.Run;

        switch (action)
        {
            case DoubleClickActions.Run:
                RunProject(project);
                break;

            case DoubleClickActions.Publish:
                StartTask(project, pack: true);
                break;

            default: // 编译，以及存了未知值时都退回最轻的动作
                StartTask(project, pack: false);
                break;
        }
    }

    /// <summary>列表键盘快捷键：目前只留 Delete 移除项目（F5 编译已注释掉），是事件处理器。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ProjectList_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete:
                RemoveSelected();
                break;
                //case Key.F5:
                //    if (Selected is { } project) StartTask(project, pack: false);
                //    break;
        }
    }

    /// <summary>右键菜单弹出前统一设可用性：没选中项目时所有项都灰掉（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void ItemMenu_Opened(object sender, RoutedEventArgs e)
    {
        var hasSelection = Selected is not null;
        foreach (var item in ItemMenu.Items.OfType<MenuItem>()) item.IsEnabled = hasSelection;
    }

    /// <summary>右键「发布（打包）」：走完整发布流水线（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void MenuRelease_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } project) StartTask(project, pack: true);
    }

    /// <summary>右键「仅编译」：只编译不打包（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void MenuBuild_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } project) StartTask(project, pack: false);
    }

    /// <summary>
    /// 「运行」：在系统默认控制台窗口里跑项目的开发命令（dotnet watch run / npm run dev），
    /// 不占用日志窗口、不改状态、不打包 —— 它是个常驻进程，关掉那个黑窗口就停了。
    /// </summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void MenuRun_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } project) RunProject(project);
    }

    /// <summary>右键「运行」和双击（当双击动作设为「运行」时）共用这一段。</summary>
    /// <param name="project">要跑起来的项目，用它的运行命令和目录。</param>
    private void RunProject(Project project)
    {
        var workDir = AppStore.ResolveFolder(project.Path);
        if (!Directory.Exists(workDir))
        {
            Notify($"项目目录不存在：\n{workDir}", "运行", MessageBoxImage.Warning);
            return;
        }

        var command = project.EffectiveRunCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            Notify($"「{project.Name}」没有可用的运行命令。\n\n"
                   + "「编辑…」里填一条运行命令即可，例如：\n  dotnet watch run\n  npm run dev",
                "运行", MessageBoxImage.Warning);
            return;
        }

        try
        {
            // Node 项目指定了 nvm 版本时，控制台里也先切到那个版本，免得调试用 A 版本、构建用 B 版本。
            string? nodePath = null;
            if (project.Type == ProjectTypes.Node && !string.IsNullOrWhiteSpace(project.NodeVersion)
                && !NodeRuntime.TryResolve(project.NodeVersion, out nodePath, out var nodeError))
            {
                Notify(nodeError, "运行", MessageBoxImage.Warning);
                return;
            }

            ProcessRunner.RunInConsole(command, workDir, nodePath);
        }
        catch (Exception ex)
        {
            Notify($"启动控制台失败：{ex.Message}", "运行", MessageBoxImage.Error);
        }
    }

    /// <summary>右键「查询发布信息」：强制查一次（失败会弹框），成功后把环境、站点、IP 汇总提示出来（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private async void MenuQueryRelease_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } project) await QueryReleaseAsync(project);
    }

    /// <summary>查询一个项目的发布信息并弹窗汇总；托盘菜单的「查询发布信息」也走这一段。</summary>
    /// <param name="project">要查的项目，用它的发布 ID 去内务府取信息。</param>
    private async Task QueryReleaseAsync(Project project)
    {
        var release = await RefreshReleaseAsync(project, silent: false);
        if (release is null) return;

        Notify(
            "发布信息已更新：\n\n"
            + $"环境：{(release.IsProduce == true ? "正式 💥" : "测试")}\n"
            + $"名称：{release.Name}\n"
            + $"站点：{release.IisSiteName}\n"
            + $"服务器：{release.IpAddress}",
            "查询发布信息");
    }

    /// <summary>右键「git pull」：在项目目录拉一次代码（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void MenuGitPull_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } project) GitPull(project);
    }

    /// <summary>在项目目录执行一次 git pull，日志照样进日志窗口。托盘菜单共用这一段。</summary>
    /// <param name="project">在哪个项目目录里拉代码。</param>
    private void GitPull(Project project)
    {
        // 有些机器没把 Git 加进 PATH（只能在 Git Bash 里用），先按默认安装位置找一下；
        // 找到了就用绝对路径，没找到仍交给系统按 PATH 找。
        var git = GitPath.Find();
        RunQuickCommand(project, git is null ? "git pull" : $"\"{git}\" pull", "git pull");
    }

    /// <summary>
    /// 「打开发布记录页面」：在程序内嵌的浏览器窗口里打开当前项目的发布记录。
    /// 地址由「网页地址 + 登录账号 + 发布 ID」拼成，所以三样缺一不可：
    /// 没填网页地址提示去设置，没登录提示先登录，项目没发布 ID 就告诉他打不开。
    /// </summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private async void MenuOpenReleasePage_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } project) await OpenReleasePageAsync(project);
    }

    /// <summary>
    /// 真正干活的版本：给一个项目，拼好地址开内嵌浏览器。托盘菜单的同一项也走这里。
    /// 主窗口藏在托盘里时先把它唤出来 —— 子窗口的 Owner 是主窗口，Owner 看不见的话子窗口也容易找不着。
    /// </summary>
    /// <param name="project">要打开发布记录的项目。</param>
    private async Task OpenReleasePageAsync(Project project)
    {
        if (!IsVisible) ShowFromTray();

        var releaseId = project.NwfReleaseId.Trim();
        if (releaseId.Length == 0)
        {
            Notify($"「{project.Name}」没有发布 ID，打不开发布记录。\n\n"
                   + "右键「编辑…」把内务府发布 ID 补上再试（留空的项目只能编译，不能发版）。",
                "打开发布记录页面", MessageBoxImage.Warning);
            return;
        }

        var webUrl = _store.Settings.WebUrl.Trim();
        if (webUrl.Length == 0)
        {
            Notify("还没配置网页地址。\n\n请先去「设置 → 设置发布配置」填写网页地址，再打开。",
                "打开发布记录页面", MessageBoxImage.Warning);
            return;
        }

        var userName = _store.Settings.UserName.Trim();
        if (userName.Length == 0)
        {
            Notify("还没登录，不知道该以谁的账号打开发布记录。\n\n请先点右上角「登录」。",
                "打开发布记录页面", MessageBoxImage.Warning);
            return;
        }

        var url = BuildReleaseRecordUrl(webUrl, userName, releaseId);

        // 标题上要标「正式 / 测试」，这个值来自内务府的发布信息（isProduce）。
        // 列表里那列可能还是空的（没点过「查询发布信息」），所以这里静默刷一次；
        // 查失败不打断流程，退回本地缓存，再没有就不带标识。
        await RefreshReleaseAsync(project, silent: true);

        var environment = project.ReleaseEnvironment;
        var title = environment.Length == 0
            ? $"发布记录 - {project.Name}"
            : $"发布记录 - {project.ReleaseName}（{environment}）";

        try
        {
            new WebPageWindow(url, title) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            // 窗口自己都没建起来（缺 WebView2 运行库、XAML 资源缺失等），
            // 不兜住的话用户只会看到“点了没反应”。
            Notify($"打开内嵌浏览器失败：{ex.Message}", "打开发布记录页面", MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 拼「发布记录」页面的地址。
    ///
    /// 形如 <c>{frontUrl}/?operate={userName}&amp;redirect=/dmtools/WebManageDetail/{releaseId}?simple=1</c>。
    /// 账号名会做 URL 编码（可能含冒号与中文，不编码会拼出非法 URL）。
    /// </summary>
    /// <param name="frontUrl">前端根地址，允许结尾带或不带斜杠。</param>
    /// <param name="userName">当前登录账号名。</param>
    /// <param name="releaseId">内务府发版 ID。</param>
    /// <returns>可直接打开的完整地址。</returns>
    private static string BuildReleaseRecordUrl(string frontUrl, string userName, string releaseId)
        => $"{frontUrl.TrimEnd('/')}/?operate={Uri.EscapeDataString(userName)}" +
           $"&redirect=/dmtools/WebManageDetail/{releaseId}?simple=1&t={DateTime.Now.ToString("HHmmssfffffff")}";

    /// <summary>右键「用 VS Code 打开」：拿项目目录调 code 命令（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void MenuVsCode_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } project) OpenInVsCode(project);
    }

    /// <summary>用 VS Code 打开项目目录。托盘菜单共用这一段。</summary>
    /// <param name="project">要打开的项目。</param>
    private void OpenInVsCode(Project project) => AppStore.OpenInVsCode(AppStore.ResolveFolder(project.Path));

    /// <summary>右键「打开所在文件夹」：用资源管理器打开项目目录（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } project) OpenProjectFolder(project);
    }

    /// <summary>用资源管理器打开项目目录。托盘菜单共用这一段。</summary>
    /// <param name="project">要打开的项目。</param>
    private void OpenProjectFolder(Project project) => AppStore.OpenPath(AppStore.ResolveFolder(project.Path));

    /// <summary>右键「复制路径」：把项目目录放进剪贴板（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } project) CopyProjectPath(project);
    }

    /// <summary>把项目目录复制到剪贴板；托盘菜单共用这一段，失败静默（剪贴板偶尔被别的进程占用）。</summary>
    /// <param name="project">要复制路径的项目。</param>
    private void CopyProjectPath(Project project)
    {
        try
        {
            Clipboard.SetText(AppStore.ResolveFolder(project.Path));
        }
        catch
        {
            // 剪贴板偶尔被别的进程占用，忽略。
        }
    }

    /// <summary>右键「编辑…」：在副本上改，确认后写回并保存（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void MenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } project) EditProject(project);
    }

    /// <summary>弹出项目编辑对话框（改的是副本，确认后才写回）。托盘菜单共用这一段。</summary>
    /// <param name="project">要编辑的项目。</param>
    private void EditProject(Project project)
    {
        // 发布 ID 在对话框里保存时校验，改对了才会把新的发布信息一起带回来。
        var dialog = new ProjectDialog(project.Clone(), _store.Settings.ApiUrl,
            _store.Projects.Select(p => p.GroupName)) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        project.CopyFrom(dialog.Result);
        _store.Save();
        RefreshUi();
    }

    /// <summary>右键「移除」的入口（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void MenuRemove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    /// <summary>确认后把选中项目从列表里移除并保存（只删配置，不动磁盘文件）。</summary>
    private void RemoveSelected()
    {
        if (Selected is { } project) RemoveProject(project);
    }

    /// <summary>确认后把一个项目从列表里移除并保存。托盘菜单共用这一段。</summary>
    /// <param name="project">要移除的项目。</param>
    private void RemoveProject(Project project)
    {
        if (_running.ContainsKey(project.Id))
        {
            Notify("这个项目正在运行任务，先等它结束再移除。", "提示");
            return;
        }

        var confirm = MessageBox.Show(this, $"确定从列表里移除「{project.Name}」吗？\n（只删配置，不动磁盘上的文件）",
            "移除项目", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        _store.Projects.Remove(project);
        _store.Save();
        RefreshUi();
    }

    // ==================== 托盘动作 ====================

    /// <summary>
    /// 托盘菜单点了某个项目的某个动作。二级菜单点项目时 action 是用户设置的双击动作，
    /// 三级菜单点具体项时就和列表右键菜单走同一批方法，行为完全一致。
    /// </summary>
    /// <param name="project">被操作的项目（托盘菜单里点的是哪一个就是哪一个，不看列表选中项）。</param>
    /// <param name="action">要执行的动作。</param>
    private void HandleTrayAction(Project project, TrayAction action)
    {
        // 这几个动作要弹对话框 / 子窗口，主窗口还藏在托盘里就先唤出来，否则对话框找不到落脚点。
        if (action is TrayAction.Edit or TrayAction.Remove or TrayAction.OpenReleasePage) ShowFromTray();

        switch (action)
        {
            case TrayAction.Run:
                RunProject(project);
                break;

            case TrayAction.Publish:
                StartTask(project, pack: true);
                break;

            case TrayAction.Build:
                StartTask(project, pack: false);
                break;

            case TrayAction.QueryRelease:
                _ = QueryReleaseAsync(project);
                break;

            case TrayAction.OpenReleasePage:
                _ = OpenReleasePageAsync(project);
                break;

            case TrayAction.GitPull:
                GitPull(project);
                break;

            case TrayAction.OpenInVsCode:
                OpenInVsCode(project);
                break;

            case TrayAction.OpenFolder:
                OpenProjectFolder(project);
                break;

            case TrayAction.CopyPath:
                CopyProjectPath(project);
                break;

            case TrayAction.Edit:
                EditProject(project);
                break;

            case TrayAction.Remove:
                RemoveProject(project);
                break;
        }
    }

    // ==================== 托盘与退出 ====================

    /// <summary>从托盘唤起主窗口：显示、还原（如果是最小化）、拿焦点。</summary>
    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// 托盘菜单「最小化」：把主窗口收回到托盘。先置成最小化再隐藏，
    /// 这样下次「显示主窗口」时 ShowFromTray 会把它还原成正常大小，而不是从一个最小化状态弹出来。
    /// </summary>
    private void HideToTray()
    {
        if (!IsVisible) return;

        // 先存位置再最小化：这时候还拿得到「正常状态」的矩形。
        SaveWindowPlacement();

        WindowState = WindowState.Minimized;
        Hide();
    }

    /// <summary>
    /// 关窗行为：普通点 X 只是隐藏到托盘（并提示一次），只有走托盘「退出」才真正退出。
    /// </summary>
    /// <param name="e">关闭事件参数，把 <c>Cancel</c> 设成 true 可以取消这次关闭。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        // 点右上角的 X 不退出，而是缩到托盘 —— 托盘程序关了就没法快捷发布了。
        if (!_reallyExit)
        {
            e.Cancel = true;
            SaveWindowPlacement();   // 缩到托盘前把位置记下来
            Hide();

            if (_hiddenHintShown) return;
            _hiddenHintShown = true;
            _tray.ShowHiddenHint();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>真正退出：取消所有在跑的任务、释放托盘图标，再关掉整个程序。</summary>
    private void ExitApp()
    {
        _reallyExit = true;
        SaveWindowPlacement();

        foreach (var cancellation in _running.Values)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 任务刚好结束，忽略。
            }
        }

        _tray.Dispose();
        Application.Current.Shutdown();
    }
}
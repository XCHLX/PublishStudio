using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using PublishStudio.Core;
using PublishStudio.Models;

namespace PublishStudio.Views;

/// <summary>
/// 项目的新增 / 编辑对话框。传进来的对象是副本，点「保存」才把值写回去，
/// 点「取消」不会污染原对象。
/// 保存时会拿「内务府发布 ID」去接口验一次：查不到就提示「编辑失败」并留在对话框里，
/// 免得等到发布打包才发现 ID 填错。
/// </summary>
public partial class ProjectDialog : Window
{
    /// <summary>保存后的项目数据。DialogResult 为 true 时有效。</summary>
    public Project Result { get; }

    /// <summary>内务府接口根地址，来自「设置 → 设置发布配置」。为空时无法校验发布 ID。</summary>
    private readonly string _apiUrl;

    /// <summary>「环境 / Node 版本」下拉里代表「不指定」的那一项。</summary>
    private const string SystemNode = "（系统默认）";

    /// <summary>下拉当前显示的是 node 版本还是环境，切类型时先把旧值收回正确的字段。</summary>
    private bool _variantShowsNode;

    /// <summary>表单填完之前不响应类型切换，否则会把还没显示的字段重置掉。</summary>
    private bool _formReady;

    /// <summary>
    /// 打开对话框：新建时套用默认产物目录，编辑时把已有字段填进表单。
    /// </summary>
    /// <param name="draft">编辑用的项目副本；传 null 表示新建。</param>
    /// <param name="apiUrl">内务府接口根地址，用于保存时校验发布 ID；可为空。</param>
    /// <param name="groups">现有项目用过的分组名，作为「分组」下拉的候选；可为 null。</param>
    public ProjectDialog(Project? draft, string? apiUrl, IEnumerable<string>? groups = null)
    {
        InitializeComponent();

        Result = draft ?? new Project();
        _apiUrl = (apiUrl ?? "").Trim().TrimEnd('/');
        Title = draft is null ? "添加项目" : "编辑项目";

        // 新建时给产物目录一个默认值（带占位符），省得每次手填；编辑则原样显示已保存的值。
        if (draft is null) Result.OutputDir = Project.DefaultOutputDir;

        TypeBox.ItemsSource = ProjectTypes.All;

        // 分组候选：从现有项目推导，去重 + 拼音排序。下拉可编辑，填新名字就是新建分组，
        // 所以这里不需要「新建分组」按钮，也没有单独的组管理。
        GroupBox.ItemsSource = (groups ?? Enumerable.Empty<string>())
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCulture)
            .ToList();

        NameBox.Text = Result.Name;
        GroupBox.Text = Result.GroupName.Trim();
        PathBox.Text = Result.Path;
        TypeBox.SelectedItem = ProjectTypes.All.Contains(Result.Type) ? Result.Type : ProjectTypes.Api;
        BuildCommandBox.Text = Result.BuildCommand;
        RunCommandBox.Text = Result.RunCommand;
        OutputDirBox.Text = Result.OutputDir;
        PostCommandsBox.Text = Result.PostCommands;
        ReleaseIdBox.Text = Result.NwfReleaseId;
        AutoGitPullBox.IsChecked = Result.GitPullBeforeRelease;

        _formReady = true;
        UpdateVariant(store: false);
        UpdateBuildHint();
    }

    /// <summary>
    /// 刷新「环境 / Node 版本」这一格：Api、Custom 显示 .NET 环境，Node.js 显示 nvm 里装着的 node 版本。
    /// 两个值分别存进 <see cref="Project.Environment"/> 和 <see cref="Project.NodeVersion"/>，
    /// 来回切类型时先把旧值收回去，切回来还是原样。
    /// </summary>
    /// <param name="store">切换前是否先把当前这一格的值写回 Result。构造函数初始化时传 false。</param>
    private void UpdateVariant(bool store = true)
    {
        if (VariantBox is null || VariantLabel is null) return;

        if (store) StoreVariant();

        var isNode = (TypeBox.SelectedItem as string) == ProjectTypes.Node;

        VariantLabel.Text = isNode ? "Node 版本（仅 Node.js 生效）" : "环境（仅 .NET 生效）";
        VariantBox.IsEditable = isNode;

        if (isNode)
        {
            var items = new List<string> { SystemNode };
            items.AddRange(NodeRuntime.Versions());
            VariantBox.ItemsSource = items;
            VariantBox.Text = string.IsNullOrWhiteSpace(Result.NodeVersion) ? SystemNode : Result.NodeVersion.Trim();
        }
        else
        {
            VariantBox.ItemsSource = Environments.All;
            VariantBox.SelectedItem =
                Environments.All.Contains(Result.Environment) ? Result.Environment : Environments.None;
        }

        _variantShowsNode = isNode;
    }

    /// <summary>把这一格当前的值写回 Result（Node 版本 / 环境各归各的字段）。切换类型和保存前都要调。</summary>
    private void StoreVariant()
    {
        if (VariantBox is null) return;

        if (_variantShowsNode)
        {
            var text = (VariantBox.Text ?? "").Trim();
            Result.NodeVersion = text == SystemNode ? "" : text;
        }
        else
        {
            Result.Environment = VariantBox.SelectedItem as string ?? Environments.None;
        }
    }

    /// <summary>提示当前命令是「用默认」还是「用你写的」，避免用户不知道留空会发生什么。</summary>
    private void UpdateBuildHint()
    {
        var type = TypeBox.SelectedItem as string ?? ProjectTypes.Api;

        if (!string.IsNullOrWhiteSpace(BuildCommandBox.Text))
        {
            BuildHint.Text = "已自定义，将完全按填写的命令执行。可用 {output} 引用产物目录。";
        }
        else
        {
            var defaultCommand = Project.DefaultBuildCommand(type);
            BuildHint.Text = string.IsNullOrEmpty(defaultCommand)
                ? "自定义类型必须填写构建命令，否则无法执行。"
                : $"留空则使用默认命令：{defaultCommand}";
        }

        UpdateRunHint(type);
    }

    /// <summary>刷新「运行命令」下方的说明：填了就是自定义，留空则显示该类型的默认命令。</summary>
    /// <param name="type">当前选择的项目类型，用来取默认运行命令。</param>
    private void UpdateRunHint(string type)
    {
        if (!string.IsNullOrWhiteSpace(RunCommandBox.Text))
        {
            RunHint.Text = "已自定义，右键「运行」时就在项目目录执行这条命令。";
            return;
        }

        var defaultCommand = Project.DefaultRunCommand(type);
        RunHint.Text = string.IsNullOrEmpty(defaultCommand)
            ? "自定义类型需要填一条运行命令（如 dotnet watch run），否则「运行」会提示未配置。"
            : $"留空则使用默认命令：{defaultCommand}";
    }

    /// <summary>类型下拉变化后，切换「环境 / Node 版本」的内容并同步两条命令说明（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void TypeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // InitializeComponent 期间也可能触发，此时控件还没全部就绪。
        if (BuildHint is null) return;
        if (_formReady) UpdateVariant();
        UpdateBuildHint();
    }

    /// <summary>「浏览…」选项目目录；名称还空着就顺手拿目录名填上（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择项目目录" };
        if (Directory.Exists(PathBox.Text)) dialog.InitialDirectory = PathBox.Text;

        if (dialog.ShowDialog(this) != true) return;

        PathBox.Text = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = new DirectoryInfo(dialog.FolderName).Name;
    }

    /// <summary>「浏览…」选产物目录（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择产物目录" };
        if (Directory.Exists(OutputDirBox.Text)) dialog.InitialDirectory = OutputDirBox.Text;

        if (dialog.ShowDialog(this) == true) OutputDirBox.Text = dialog.FolderName;
    }

    /// <summary>
    /// 保存：校验目录存在 → 校验发布 ID（改动过才发请求）→ 把表单值写回 <see cref="Result"/> → 关闭对话框。
    /// </summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var folder = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show(this, "请填写项目目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Directory.Exists(folder) && !File.Exists(folder))
        {
            MessageBox.Show(this, $"目录不存在：\n{folder}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var releaseId = ReleaseIdBox.Text.Trim();

        // 发布 ID 是新填的 / 改过的，就先验一次再保存。
        // 已经验过且没改动（缓存里的 ID 一致）就不再发请求，编辑别的字段不至于被网络卡住。
        if (releaseId.Length > 0 && Result.NwfRelease?.Id != releaseId
            && !await ValidateReleaseIdAsync(releaseId)) return;

        Result.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? new DirectoryInfo(folder).Name : NameBox.Text.Trim();
        Result.GroupName = (GroupBox.Text ?? "").Trim();
        Result.Path = folder;
        StoreVariant();   // 环境 / Node 版本：按这一格当前显示的内容写回对应字段
        Result.Type = TypeBox.SelectedItem as string ?? ProjectTypes.Api;
        Result.BuildCommand = BuildCommandBox.Text.Trim();
        Result.RunCommand = RunCommandBox.Text.Trim();
        Result.OutputDir = OutputDirBox.Text.Trim();
        Result.PostCommands = PostCommandsBox.Text;
        Result.NwfReleaseId = releaseId;
        Result.GitPullBeforeRelease = AutoGitPullBox.IsChecked == true;

        // ID 清空了就把旧的发布信息一起丢了，列表不会还挂着上次查到的正式/测试。
        if (releaseId.Length == 0) Result.NwfRelease = null;

        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 拿发布 ID 去内务府查一次。查到就把结果存进 <see cref="Result"/>，查不到 / 接口不通
    /// 就提示「编辑失败」并返回 false（对话框不关，让用户改）。
    /// </summary>
    /// <param name="releaseId">用户填的内务府发布 ID。</param>
    /// <returns>查到了返回 true（结果已存进 <see cref="Result"/>）；接口没配、查不到或出错返回 false。</returns>
    private async Task<bool> ValidateReleaseIdAsync(string releaseId)
    {
        if (_apiUrl.Length == 0)
        {
            MessageBox.Show(this,
                "还没配置内务府接口地址，无法校验发布 ID。\n\n"
                + "请先去「设置 → 设置发布配置」填写接口地址，再回来保存。",
                "编辑失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        SetBusy(true);
        try
        {
            var node = await new NwfApi(_apiUrl).GetIisManagerAsync(releaseId);

            // 校验期间用户可能已经把对话框关了，这时候不能再弹框、也不能设 DialogResult。
            if (!IsLoaded) return false;

            var release = node?.Deserialize<NwfRelease>();
            if (release is null)
            {
                MessageBox.Show(this,
                    $"发布 ID「{releaseId}」在内务府里查不到，编辑未保存。\n\n"
                    + "请确认 ID 是否复制完整。\n"
                    + "如果这个项目不需要发布、只想本地编译，把发布 ID 清空即可保存。",
                    "编辑失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            Result.NwfRelease = release;
            return true;
        }
        catch (Exception ex)
        {
            if (!IsLoaded) return false;

            MessageBox.Show(this,
                $"校验发布 ID 时出错，编辑未保存。\n\n{ex.Message}",
                "编辑失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>校验期间锁住保存按钮，避免连点发起多次请求。</summary>
    /// <param name="busy">true 进入校验态（按钮变灰、文案「校验中…」、发布 ID 只读）；false 恢复。</param>
    private void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = !busy;
        SaveButton.Content = busy ? "校验中…" : "保存";
        ReleaseIdBox.IsReadOnly = busy;
    }

    /// <summary>取消：直接关掉对话框，不改动原项目（事件处理器）。</summary>
    /// <param name="sender">事件源（这里用不到）。</param>
    /// <param name="e">事件参数（这里用不到）。</param>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

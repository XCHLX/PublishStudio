using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using PublishStudio.Models;

namespace PublishStudio.Core;

/// <summary>
/// 项目清单的读写。数据只有一份 JSON，放在 %LOCALAPPDATA%\PublishStudio\projects.json，
/// 不碰系统注册表，删掉这个文件即恢复出厂状态。
/// </summary>
public sealed class AppStore
{
    /// <summary>JSON 序列化选项：缩进 + 中文不转义，方便用户直接手改文件。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>数据目录（%LOCALAPPDATA%\PublishStudio）。</summary>
    public static string DataDir { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PublishStudio");

    /// <summary>项目清单文件全路径。</summary>
    public string ProjectsFile { get; } = System.IO.Path.Combine(DataDir, "projects.json");

    /// <summary>全局发布配置文件全路径。</summary>
    public string SettingsFile { get; } = System.IO.Path.Combine(DataDir, "settings.json");

    /// <summary>内存中的项目列表，界面直接绑这个集合。</summary>
    public ObservableCollection<Project> Projects { get; } = new();

    /// <summary>内存中的发布配置，设置对话框读写这一份。</summary>
    public PublishSettings Settings { get; private set; } = new();

    /// <summary>读盘。文件不存在或损坏时保持空列表，不抛异常。</summary>
    public void Load()
    {
        Projects.Clear();
        var list = ReadJson<List<Project>>(ProjectsFile);
        if (list is null) return;
        foreach (var project in list) Projects.Add(project);
    }

    /// <summary>
    /// 写盘。先写 .tmp 再替换，避免写一半断电把清单写坏。
    /// </summary>
    public void Save() => WriteJson(ProjectsFile, Projects);

    /// <summary>
    /// 读发布配置。敏感字段（接口地址、网页地址、OSS Key / 密钥）在这里解开成明文，
    /// 内存里始终是明文，只有落盘时才是密文（见 <see cref="SaveSettings"/>）。
    /// 读到老版本留下的明文会自动转成密文再存一次，不用用户手动做什么。
    /// </summary>
    public void LoadSettings()
    {
        var loaded = ReadJson<PublishSettings>(SettingsFile) ?? new PublishSettings();

        var hasPlain = false;
        loaded.ApiUrl = Reveal(loaded.ApiUrl, ref hasPlain);
        loaded.WebUrl = Reveal(loaded.WebUrl, ref hasPlain);
        loaded.OssKey = Reveal(loaded.OssKey, ref hasPlain);
        loaded.OssSecret = Reveal(loaded.OssSecret, ref hasPlain);

        Settings = loaded;

        // 老配置里是明文 → 升级后第一次启动就顺手加密，免得一直明文躺在磁盘上。
        // 写盘失败（目录只读之类）不影响启动，这次就还是明文，下次再说。
        if (hasPlain)
        {
            try { SaveSettings(); }
            catch (Exception ex) { Debug.WriteLine($"[AppStore] 加密回写 settings.json 失败: {ex.Message}"); }
        }
    }

    /// <summary>
    /// 写发布配置，写法与项目清单一致（先 .tmp 再替换）。
    /// 敏感字段先加密再序列化，所以 settings.json 里看到的是 <c>enc:v1:...</c> 密文。
    /// 注意加密的是副本，内存里那份仍然是明文，界面和发布流程不受影响。
    /// </summary>
    public void SaveSettings()
    {
        var copy = Settings.Clone();
        copy.ApiUrl = Secret.Protect(copy.ApiUrl);
        copy.WebUrl = Secret.Protect(copy.WebUrl);
        copy.OssKey = Secret.Protect(copy.OssKey);
        copy.OssSecret = Secret.Protect(copy.OssSecret);

        WriteJson(SettingsFile, copy);
    }

    /// <summary>把配置里读出来的值还原成明文，并记录这次有没有遇到「还是明文」的老配置。</summary>
    /// <param name="stored">JSON 里的原始值。</param>
    /// <param name="hasPlain">遇到过明文就置 true，用来决定要不要顺手加密存一次。</param>
    /// <returns>明文；解不开时为空串。</returns>
    private static string Reveal(string? stored, ref bool hasPlain)
    {
        if (!string.IsNullOrEmpty(stored) && !Secret.IsProtected(stored)) hasPlain = true;
        return Secret.Unprotect(stored);
    }

    /// <summary>读 JSON。文件不存在或损坏时返回 default，损坏的那份留 .broken 备份。</summary>
    /// <typeparam name="T">目标类型（<see cref="List{T}"/> 或单个配置对象）。</typeparam>
    /// <param name="file">JSON 文件全路径。</param>
    /// <returns>反序列化结果；文件不存在、读取失败或内容损坏时返回 default。</returns>
    private static T? ReadJson<T>(string file)
    {
        if (!File.Exists(file)) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file), JsonOptions);
        }
        catch (Exception ex)
        {
            // 配置文件被手改坏了不该让程序打不开，留个备份再当空处理。
            var backup = file + ".broken";
            try { File.Copy(file, backup, overwrite: true); } catch { /* 备份失败也无所谓 */ }
            Debug.WriteLine($"[AppStore] 读取 {file} 失败: {ex.Message}");
            return default;
        }
    }

    /// <summary>写 JSON：先写 .tmp 再替换，避免写一半断电把文件写坏。</summary>
    /// <typeparam name="T">要序列化的类型。</typeparam>
    /// <param name="file">目标文件全路径。</param>
    /// <param name="value">要写入的对象，按 <see cref="JsonOptions"/> 缩进、中文不转义。</param>
    private static void WriteJson<T>(string file, T value)
    {
        Directory.CreateDirectory(DataDir);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>
    /// 把用户选的路径统一成目录：选了 .csproj / .sln / package.json 就往上取一级目录。
    /// </summary>
    /// <param name="path">用户选的文件或目录路径。</param>
    /// <returns>目录路径（不带结尾分隔符）；入参为空时返回空串。</returns>
    public static string ResolveFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return File.Exists(path)
            ? System.IO.Path.GetDirectoryName(path) ?? path
            : path.TrimEnd('\\', '/');
    }

    /// <summary>用资源管理器打开目录或选中文件。路径不存在时什么都不做。</summary>
    /// <param name="path">要打开的目录，或要在资源管理器里选中的文件。</param>
    public static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
            }
        }
        catch
        {
            // 资源管理器起不来不影响主流程。
        }
    }

    /// <summary>用 VS Code 打开项目目录。没装 VS Code 时静默失败。</summary>
    /// <param name="folder">要打开的项目目录；不存在就不动作。</param>
    public static void OpenInVsCode(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "code", Arguments = $"\"{folder}\"", UseShellExecute = true });
        }
        catch
        {
            // 没装 code 命令就忽略。
        }
    }
}

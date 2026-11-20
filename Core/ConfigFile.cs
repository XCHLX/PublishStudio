using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PublishStudio.Models;

namespace PublishStudio.Core;

/// <summary>
/// 发布配置的备份文件（<c>.psbak</c>）读写。
///
/// 为什么要单独一套加密：settings.json 用的是 DPAPI，密钥绑「本机 + 当前 Windows 账户」，
/// 拷到别的电脑就解不开了。备份文件改用**口令派生的密钥**（PBKDF2 → AES-256-GCM），
/// 因而换电脑、换账户都能导入，口令只在导出/导入这一次里用，不落盘、不记忆。
///
/// 备份里只装这 6 项：接口地址、网页地址、OSS Key、OSS 密钥、保留天数、双击动作。
/// 窗口位置、排序列、登录账号属于「这台电脑上的状态」，不进备份。
/// </summary>
public static class ConfigFile
{
    /// <summary>备份文件后缀。</summary>
    public const string Extension = ".psbak";

    /// <summary>导出时口令的最短位数。</summary>
    public const int MinimumPasswordLength = 8;

    /// <summary>背包里那层 JSON 的标识，导入时用它认文件。</summary>
    private const string FormatName = "PublishStudio.Backup";

    /// <summary>当前格式版本。读到更高版本说明是更新版程序导出的，直接拒绝免得读错。</summary>
    private const int FormatVersion = 1;

    /// <summary>PBKDF2 迭代次数。20 万次在本机约零点几秒，够拖住暴力破解又不至于让人等。</summary>
    private const int Iterations = 200_000;

    /// <summary>迭代次数上限，防止有人手改备份文件塞个天文数字把界面卡死。</summary>
    private const int MaxIterations = 2_000_000;

    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 导出备份：把这 6 项配置用口令加密后写到 <paramref name="path"/>。
    /// 明文先序列化成 JSON，整段用 AES-256-GCM 加密（自带完整性校验，文件被改过就会解密失败）。
    /// </summary>
    /// <param name="path">备份文件全路径，一般是 <c>.psbak</c>。</param>
    /// <param name="settings">当前生效的配置（内存里那份，字段是明文）。</param>
    /// <param name="password">用户设置的口令，至少 <see cref="MinimumPasswordLength"/> 位。</param>
    public static void Export(string path, PublishSettings settings, string password)
    {
        if (password.Length < MinimumPasswordLength)
            throw new InvalidOperationException($"口令至少 {MinimumPasswordLength} 位。");

        var payload = new BackupPayload
        {
            ApiUrl = settings.ApiUrl,
            WebUrl = settings.WebUrl,
            OssKey = settings.OssKey,
            OssSecret = settings.OssSecret,
            KeepBuildDays = settings.KeepBuildDays,
            DoubleClickAction = settings.DoubleClickAction,
        };

        var plain = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize)) aes.Encrypt(nonce, plain, cipher, tag);

        var container = new Container
        {
            Format = FormatName,
            Version = FormatVersion,
            CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            Kdf = new KdfPart
            {
                Name = "PBKDF2-SHA256",
                Iterations = Iterations,
                Salt = Convert.ToBase64String(salt),
            },
            Cipher = new CipherPart
            {
                Name = "AES-256-GCM",
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Data = Convert.ToBase64String(cipher),
            },
        };

        File.WriteAllText(path, JsonSerializer.Serialize(container, JsonOptions), new UTF8Encoding(false));
    }

    /// <summary>
    /// 读备份。口令不对、文件被改过、不是本程序的备份，都会抛
    /// <see cref="InvalidDataException"/>，消息是能直接显示给用户的中文。
    /// </summary>
    /// <param name="path">备份文件全路径。</param>
    /// <param name="password">导出时设置的口令。</param>
    /// <returns>解出来的 6 项配置。</returns>
    public static BackupPayload Import(string path, string password)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);

        Container? container;
        try
        {
            container = JsonSerializer.Deserialize<Container>(text, JsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("这个文件不是 PublishStudio 的配置备份，或者内容已经损坏。");
        }

        if (container is null || !string.Equals(container.Format, FormatName, StringComparison.Ordinal))
            throw new InvalidDataException("这个文件不是 PublishStudio 的配置备份。");

        if (container.Version > FormatVersion)
            throw new InvalidDataException("这份备份来自更新版本的程序，请先把程序升级到对应版本再导入。");

        var iterations = container.Kdf.Iterations;
        if (iterations <= 0 || iterations > MaxIterations)
            throw new InvalidDataException("备份文件的加密参数不正常，文件可能被改过。");

        byte[] plain;
        try
        {
            var salt = Convert.FromBase64String(container.Kdf.Salt);
            var nonce = Convert.FromBase64String(container.Cipher.Nonce);
            var tag = Convert.FromBase64String(container.Cipher.Tag);
            var cipher = Convert.FromBase64String(container.Cipher.Data);

            var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
            plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            // GCM 认证失败无法区分「口令错」和「内容被改」，对用户来说提示一句就够。
            throw new InvalidDataException("口令不对，或者备份文件已经被修改过。");
        }

        BackupPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BackupPayload>(plain, JsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("备份内容解析失败，文件可能已经损坏。");
        }

        if (payload is null) throw new InvalidDataException("备份里没有配置内容。");

        // 手改过的备份可能塞进下拉框里没有的值，落回默认，别让界面出现空白选项。
        if (!DoubleClickActions.All.Contains(payload.DoubleClickAction))
            payload.DoubleClickAction = DoubleClickActions.Run;

        if (payload.KeepBuildDays < 0) payload.KeepBuildDays = PublishSettings.DefaultKeepDays;

        return payload;
    }

    /// <summary>背包文件的外层结构（不含敏感内容，敏感内容在 <see cref="CipherPart.Data"/> 里）。</summary>
    private sealed class Container
    {
        /// <summary>固定标识，用来认文件。</summary>
        public string Format { get; set; } = "";

        /// <summary>格式版本。</summary>
        public int Version { get; set; }

        /// <summary>导出时间，只是给人看。</summary>
        public string CreatedAt { get; set; } = "";

        /// <summary>密钥派生参数。</summary>
        public KdfPart Kdf { get; set; } = new();

        /// <summary>加密参数与密文。</summary>
        public CipherPart Cipher { get; set; } = new();
    }

    /// <summary>密钥派生参数：算法名、迭代次数、随机盐。</summary>
    private sealed class KdfPart
    {
        /// <summary>固定为 PBKDF2-SHA256。</summary>
        public string Name { get; set; } = "";

        /// <summary>迭代次数。</summary>
        public int Iterations { get; set; }

        /// <summary>随机盐（Base64）。</summary>
        public string Salt { get; set; } = "";
    }

    /// <summary>AES-GCM 的随机数、认证标签与密文。</summary>
    private sealed class CipherPart
    {
        /// <summary>固定为 AES-256-GCM。</summary>
        public string Name { get; set; } = "";

        /// <summary>随机数（Base64）。</summary>
        public string Nonce { get; set; } = "";

        /// <summary>认证标签（Base64）。</summary>
        public string Tag { get; set; } = "";

        /// <summary>密文（Base64）。</summary>
        public string Data { get; set; } = "";
    }
}

/// <summary>备份文件里装的 6 项配置。导入时原样交给设置对话框。</summary>
public sealed class BackupPayload
{
    /// <summary>后端接口地址。</summary>
    public string ApiUrl { get; set; } = "";

    /// <summary>前端网页地址。</summary>
    public string WebUrl { get; set; } = "";

    /// <summary>对象存储 AccessKeyId。</summary>
    public string OssKey { get; set; } = "";

    /// <summary>对象存储 AccessKeySecret。</summary>
    public string OssSecret { get; set; } = "";

    /// <summary>临时编译目录保留天数，0 表示不保留。</summary>
    public int KeepBuildDays { get; set; } = PublishSettings.DefaultKeepDays;

    /// <summary>双击项目列表时触发的动作。</summary>
    public string DoubleClickAction { get; set; } = DoubleClickActions.Run;
}

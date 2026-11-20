using System.Runtime.InteropServices;
using System.Text;

namespace PublishStudio.Core;

/// <summary>
/// 配置里敏感字段（接口地址、网页地址、OSS Key、OSS 密钥）的落盘加密与界面脱敏。
///
/// 加密用 Windows 自带的 DPAPI（<c>CryptProtectData</c>），密钥由系统按「当前用户」派生，
/// 程序自己不用存任何密钥。所以：
///   · 只有本机、同一个 Windows 账户能解开；
///   · 把 settings.json 拷给别人，对方看到的是 <c>enc:v1:...</c> 一串密文，解不开；
///   · 换电脑、换账户登录后凭据解不开，需要在设置里重填一次。
///
/// 密文带 <c>enc:v1:</c> 前缀，老版本存下来的明文没有前缀，读到就当作明文用，
/// 并在下一次保存时自动转成密文（见 <see cref="AppStore.LoadSettings"/>）。
/// </summary>
public static class Secret
{
    /// <summary>密文前缀。改加密方式时换版本号即可，老的密文会自然失效（退回重新填写）。</summary>
    private const string Prefix = "enc:v1:";

    /// <summary>脱敏时中间固定用几个圆点，不泄漏真实长度。</summary>
    private const string Dots = "••••••";

    /// <summary>DPAPI 标志：禁止弹出任何系统界面（本来也不会弹，防一手）。</summary>
    private const int UiForbidden = 0x1;

    /// <summary>给 DPAPI 的附加说明文字，解密时不需要它。</summary>
    private const string Description = "PublishStudio";

    /// <summary>判断一个值是不是已经加密过（带 <c>enc:v1:</c> 前缀）。</summary>
    /// <param name="stored">配置文件里读出来的原始值。</param>
    /// <returns>已加密返回 true。</returns>
    public static bool IsProtected(string? stored)
        => !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// 把明文加密成可落盘的字符串。空值原样返回空串（配置没填时 JSON 里还是 <c>""</c>，方便手看）。
    /// 加密失败（极端情况）时退回明文，宁可漏加密也不能把用户的配置弄丢。
    /// </summary>
    /// <param name="plain">内存里的明文。</param>
    /// <returns>带前缀的密文，或空串、或原明文。</returns>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        if (IsProtected(plain)) return plain;

        var cipher = Dpapi(Encoding.UTF8.GetBytes(plain), protect: true);
        return cipher is null ? plain : Prefix + Convert.ToBase64String(cipher);
    }

    /// <summary>
    /// 把配置里的值还原成明文。没有前缀的视为老版本的明文，原样返回；
    /// 有前缀但解不开（换了电脑 / 换了账户）返回空串，界面上会显示成「未配置」。
    /// </summary>
    /// <param name="stored">配置文件里读出来的原始值。</param>
    /// <returns>明文；解不开时为空串。</returns>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!IsProtected(stored)) return stored;

        try
        {
            var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            var plain = Dpapi(cipher, protect: false);
            return plain is null ? "" : Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Base64 被手改坏了之类，当作没配置。
            return "";
        }
    }

    /// <summary>
    /// 脱敏显示：保留头尾各 4 个字符，中间固定 6 个圆点。
    /// 用于「关于」窗口、发布前预检这些不该出现完整凭据的地方。
    /// </summary>
    /// <param name="value">明文的地址或凭据。</param>
    /// <returns>脱敏后的文本；空值返回「未配置」。</returns>
    public static string Mask(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return "未配置";
        if (text.Length <= 8) return Dots;

        return text[..4] + Dots + text[^4..];
    }

    /// <summary>调 DPAPI 加密或解密一段字节；失败返回 null。</summary>
    /// <param name="input">输入字节。</param>
    /// <param name="protect">true 加密，false 解密。</param>
    /// <returns>输出字节；失败为 null。</returns>
    private static byte[]? Dpapi(byte[] input, bool protect)
    {
        var source = new DataBlob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        try
        {
            Marshal.Copy(input, 0, source.Data, input.Length);

            DataBlob result;
            bool ok;
            if (protect)
                ok = CryptProtectData(ref source, Description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out result);
            else
                ok = CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out result);

            if (!ok || result.Size <= 0 || result.Data == IntPtr.Zero) return null;

            try
            {
                var output = new byte[result.Size];
                Marshal.Copy(result.Data, output, 0, result.Size);
                return output;
            }
            finally
            {
                // DPAPI 用 LocalAlloc 分配，必须 LocalFree，否则每次保存都漏一块。
                LocalFree(result.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(source.Data);
        }
    }

    /// <summary>crypt32.dll 用的二进制块结构。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        /// <summary>字节数。</summary>
        public int Size;

        /// <summary>数据指针。</summary>
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}

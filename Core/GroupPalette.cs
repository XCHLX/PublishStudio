namespace PublishStudio.Core;

/// <summary>
/// 分组色点的取色规则：按分组名算一个稳定的序号，落到固定的调色板上。
/// 分组名本身不存颜色 —— 改个名字就重新取色，同一个名字在任何地方（列表、托盘）
/// 都是同一个颜色，不用再维护一份「分组 → 颜色」的配置。
/// </summary>
public static class GroupPalette
{
    /// <summary>
    /// 调色板：8 个色相均匀铺开、明度错开的深色，浅底上都能看清。
    /// 顺序本身没含义，只是轮着用 —— 关键是任意两个的色相差够大，
    /// 小尺寸色点上也能一眼分开（以前橙和红挨着，8px 圆点看着是同一个色）。
    /// </summary>
    private static readonly string[] Colors =
    {
        "#1F6FEB",   // 蓝
        "#0E7490",   // 青
        "#15803D",   // 绿
        "#4D7C0F",   // 橄榄
        "#B45309",   // 琥珀
        "#DC2626",   // 红
        "#BE185D",   // 玫红
        "#7C3AED",   // 紫
    };

    /// <summary>「未分组」用的颜色：调色板之外固定灰，列表和托盘都用它。</summary>
    public const string NoneColor = "#8B949E";

    /// <summary>
    /// 取分组对应的颜色。
    /// </summary>
    /// <param name="group">分组名；空或全空白表示未分组。</param>
    /// <returns>形如 <c>#15803D</c> 的颜色值。未分组返回固定灰 <see cref="NoneColor"/>。</returns>
    public static string For(string? group)
    {
        var name = (group ?? "").Trim();
        if (name.Length == 0) return NoneColor;

        // 自己算哈希，不能用 string.GetHashCode()：.NET Core 起它每个进程随机化，
        // 程序重启后同一个分组会换颜色，看着像数据错乱。
        var hash = 0;
        foreach (var ch in name) hash = (hash * 31 + ch) & 0x7FFFFFFF;

        return Colors[hash % Colors.Length];
    }
}

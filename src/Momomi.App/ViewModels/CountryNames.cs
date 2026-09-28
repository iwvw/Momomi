namespace Momomi.App.ViewModels;

/// <summary>
/// 国家码（ISO 3166-1 alpha-2）→ 中文名映射，用于展示出口 IP 归属地。
/// 覆盖常见国家/地区，未收录的返回空字符串。
/// </summary>
public static class CountryNames
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CN"] = "中国", ["HK"] = "中国香港", ["MO"] = "中国澳门", ["TW"] = "中国台湾",
        ["US"] = "美国", ["JP"] = "日本", ["KR"] = "韩国", ["SG"] = "新加坡",
        ["GB"] = "英国", ["DE"] = "德国", ["FR"] = "法国", ["CA"] = "加拿大",
        ["AU"] = "澳大利亚", ["NL"] = "荷兰", ["RU"] = "俄罗斯", ["IN"] = "印度",
        ["MY"] = "马来西亚", ["TH"] = "泰国", ["VN"] = "越南", ["ID"] = "印度尼西亚",
        ["PH"] = "菲律宾", ["AE"] = "阿联酋", ["SA"] = "沙特阿拉伯", ["TR"] = "土耳其",
        ["IL"] = "以色列", ["CH"] = "瑞士", ["SE"] = "瑞典", ["NO"] = "挪威",
        ["FI"] = "芬兰", ["DK"] = "丹麦", ["BE"] = "比利时", ["AT"] = "奥地利",
        ["IT"] = "意大利", ["ES"] = "西班牙", ["PT"] = "葡萄牙", ["PL"] = "波兰",
        ["CZ"] = "捷克", ["RO"] = "罗马尼亚", ["UA"] = "乌克兰", ["BR"] = "巴西",
        ["MX"] = "墨西哥", ["AR"] = "阿根廷", ["CL"] = "智利", ["ZA"] = "南非",
        ["EG"] = "埃及", ["NG"] = "尼日利亚", ["NZ"] = "新西兰", ["IE"] = "爱尔兰",
        ["LU"] = "卢森堡", ["LI"] = "列支敦士登", ["IS"] = "冰岛", ["EE"] = "爱沙尼亚",
        ["LV"] = "拉脱维亚", ["LT"] = "立陶宛", ["GR"] = "希腊", ["HU"] = "匈牙利",
        ["SK"] = "斯洛伐克", ["SI"] = "斯洛文尼亚", ["HR"] = "克罗地亚", ["RS"] = "塞尔维亚",
        ["BG"] = "保加利亚", ["AL"] = "阿尔巴尼亚", ["MK"] = "北马其顿", ["BA"] = "波黑",
        ["GE"] = "格鲁吉亚", ["AM"] = "亚美尼亚", ["AZ"] = "阿塞拜疆", ["KZ"] = "哈萨克斯坦",
        ["UZ"] = "乌兹别克斯坦", ["KG"] = "吉尔吉斯斯坦", ["TJ"] = "塔吉克斯坦",
        ["BD"] = "孟加拉国", ["PK"] = "巴基斯坦", ["LK"] = "斯里兰卡", ["NP"] = "尼泊尔",
        ["KH"] = "柬埔寨", ["LA"] = "老挝", ["MM"] = "缅甸", ["BN"] = "文莱",
        ["KW"] = "科威特", ["QA"] = "卡塔尔", ["BH"] = "巴林", ["OM"] = "阿曼",
        ["YE"] = "也门", ["JO"] = "约旦", ["LB"] = "黎巴嫩", ["CY"] = "塞浦路斯",
        ["MT"] = "马耳他", ["MC"] = "摩纳哥", ["AD"] = "安道尔", ["SM"] = "圣马力诺",
        ["VA"] = "梵蒂冈", ["CO"] = "哥伦比亚", ["PE"] = "秘鲁", ["VE"] = "委内瑞拉",
        ["UY"] = "乌拉圭", ["PY"] = "巴拉圭", ["BO"] = "玻利维亚", ["EC"] = "厄瓜多尔",
        ["CR"] = "哥斯达黎加", ["PA"] = "巴拿马", ["DO"] = "多米尼加", ["CU"] = "古巴",
        ["JM"] = "牙买加", ["TT"] = "特立尼达和多巴哥", ["PR"] = "波多黎各",
        ["DZ"] = "阿尔及利亚", ["MA"] = "摩洛哥", ["TN"] = "突尼斯", ["LY"] = "利比亚",
        ["SD"] = "苏丹", ["ET"] = "埃塞俄比亚", ["KE"] = "肯尼亚", ["TZ"] = "坦桑尼亚",
        ["GH"] = "加纳", ["SN"] = "塞内加尔", ["CI"] = "科特迪瓦", ["CM"] = "喀麦隆",
        ["ZW"] = "津巴布韦", ["MZ"] = "莫桑比克", ["AO"] = "安哥拉", ["NA"] = "纳米比亚",
        ["BW"] = "博茨瓦纳", ["GH"] = "加纳",
        ["FJ"] = "斐济", ["PG"] = "巴布亚新几内亚",
    };

    /// <summary>国家码转中文名；未收录返回空字符串。</summary>
    public static string GetName(string? countryCode)
        => countryCode is not null && Map.TryGetValue(countryCode, out var name) ? name : "";

    /// <summary>国家码转内置国旗 SVG 资源路径（ms-appx 协议）。</summary>
    public static string GetFlag(string? countryCode)
    {
        if (string.IsNullOrEmpty(countryCode) || countryCode.Length != 2) return "";
        var c = countryCode.ToLowerInvariant();
        return $"ms-appx:///Assets/flags/{c}.svg";
    }
}

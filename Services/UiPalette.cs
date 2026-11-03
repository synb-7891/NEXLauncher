using System.Windows;
using System.Windows.Media;

namespace NEXLauncher.Services;

/// <summary>
/// 全局 UI 调色板：所有界面颜色从这里取，支持主题预设切换。
/// 代码构建的控件在构建时读取当前值；窗口框架通过 DynamicResource 绑定 App 资源。
/// </summary>
public static class UiPalette
{
    public static string Bg { get; private set; } = "#F3F5F9";
    public static string Header { get; private set; } = "#FFFFFF";
    public static string Surface { get; private set; } = "#FFFFFF";
    public static string SurfaceAlt { get; private set; } = "#EDF1F7";
    public static string Border { get; private set; } = "#DFE5EE";
    public static string Accent { get; private set; } = "#4C9AFF";
    public static string Text { get; private set; } = "#1B2532";
    public static string TextDim { get; private set; } = "#46566B";
    public static string Muted { get; private set; } = "#7C8BA1";
    public static string MutedLight { get; private set; } = "#9AA7B9";

    public sealed record ThemePresetDef(string Name, string Bg, string Header, string Surface,
        string SurfaceAlt, string Border, string Accent, string Text, string TextDim,
        string Muted, string MutedLight);

    public static readonly ThemePresetDef[] Presets =
    [
        new("云白",   "#F3F5F9", "#FFFFFF", "#FFFFFF", "#EDF1F7", "#DFE5EE", "#4C9AFF", "#1B2532", "#46566B", "#7C8BA1", "#9AA7B9"),
        new("熔光",   "#020817", "#060C18", "#0B1220", "#141E32", "#1E293B", "#F6A82A", "#F8FAFC", "#CBD5E1", "#94A3B8", "#A8B4C4"),
        new("深蓝",   "#030A15", "#071120", "#0C1626", "#152238", "#1E3050", "#4C9AFF", "#F1F5F9", "#CBD5E1", "#8FA3BE", "#A6B7CC"),
        new("暗夜黑", "#070809", "#0D0F11", "#14171A", "#1E2226", "#2A2F35", "#4ECB73", "#EFF2F4", "#C8D0D6", "#8A939B", "#A2ABB3"),
        new("紫罗兰", "#0A0714", "#120D22", "#171029", "#231A3C", "#332A4E", "#B06BFF", "#F3EFFB", "#D6CAEA", "#9887B5", "#B0A0C9"),
        new("翡翠绿", "#04100D", "#081813", "#0C1F19", "#143028", "#1E4034", "#22D3A6", "#E8F7F2", "#C4E4D9", "#7DA396", "#95B8AB"),
        new("暖橙",   "#120C06", "#1A130A", "#221809", "#302313", "#3D2C17", "#FFB340", "#FAF3E6", "#E3D3B8", "#A3906F", "#BBA98A"),
    ];

    /// <summary>应用主题预设到全局调色板与 App 资源。返回 false 表示预设不存在。</summary>
    public static bool ApplyPreset(string name)
    {
        var p = Presets.FirstOrDefault(x => x.Name == name) ?? Presets[0];
        Bg = p.Bg; Header = p.Header; Surface = p.Surface; SurfaceAlt = p.SurfaceAlt;
        Border = p.Border; Accent = p.Accent; Text = p.Text; TextDim = p.TextDim;
        Muted = p.Muted; MutedLight = p.MutedLight;
        PushToAppResources();
        return true;
    }

    /// <summary>仅更换强调色（主题色卡片使用）。传空表示跟随主题预设，不做覆盖。</summary>
    public static void SetAccent(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        Accent = hex;
        PushToAppResources();
    }

    /// <summary>把当前调色板写进 Application.Resources，供 XAML DynamicResource 使用。</summary>
    public static void PushToAppResources()
    {
        var r = Application.Current.Resources;
        Set(r, "BgBrush", Bg); Set(r, "HeaderBrush", Header);
        Set(r, "SurfaceBrush", Surface); Set(r, "SurfaceAltBrush", SurfaceAlt);
        Set(r, "BorderBrush", Border); Set(r, "AccentBrush", Accent);
        Set(r, "TextBrush", Text); Set(r, "TextDimBrush", TextDim);
        Set(r, "MutedBrush", Muted); Set(r, "MutedLightBrush", MutedLight);
        Set(r, "BgColor", Bg); Set(r, "HeaderColor", Header);
        Set(r, "SurfaceColor", Surface); Set(r, "BorderColor", Border);
        Set(r, "AccentColor", Accent); Set(r, "TextColor", Text); Set(r, "MutedColor", Muted);
    }

    private static void Set(ResourceDictionary r, string key, string hex)
    {
        if (!TryParseColor(hex, out var color))
        {
            // 脏数据兜底：颜色字符串非法（如被外部改坏）时重置为默认主题，绝不让主题配置炸掉启动
            var d = Presets[0];
            Bg = d.Bg; Header = d.Header; Surface = d.Surface; SurfaceAlt = d.SurfaceAlt;
            Border = d.Border; Accent = d.Accent; Text = d.Text; TextDim = d.TextDim;
            Muted = d.Muted; MutedLight = d.MutedLight;
            hex = key switch
            {
                "BgBrush" or "BgColor" => Bg,
                "HeaderBrush" or "HeaderColor" => Header,
                "SurfaceBrush" or "SurfaceColor" => Surface,
                "SurfaceAltBrush" => SurfaceAlt,
                "BorderBrush" or "BorderColor" => Border,
                "AccentBrush" or "AccentColor" => Accent,
                "TextBrush" or "TextColor" => Text,
                "TextDimBrush" => TextDim,
                "MutedBrush" or "MutedColor" => Muted,
                "MutedLightBrush" => MutedLight,
                _ => d.Accent
            };
            color = (Color)ColorConverter.ConvertFromString(hex);
        }
        if (key.EndsWith("Color"))
            r[key] = color;
        else
            r[key] = new SolidColorBrush(color);
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        try { color = (Color)ColorConverter.ConvertFromString(hex); return true; }
        catch { color = default; return false; }
    }

    public static SolidColorBrush BrushOf(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}

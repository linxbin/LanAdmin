namespace LanAdmin.Contracts;

public static class ReminderStyleDefaults
{
    public const int MinWidth = 320;
    public const int MaxWidth = 900;
    public const int MinHeight = 160;
    public const int MaxHeight = 600;
    public const int MinCornerRadius = 0;
    public const int MaxCornerRadius = 40;
    public const int MinBorderWidth = 0;
    public const int MaxBorderWidth = 8;
    public const double MinFontSize = 8;
    public const double MaxFontSize = 36;
    public const int MaxTextLength = 500;
    public const int MaxFontFamilyLength = 64;
    public const int MaxImageUrlLength = 1024;

    public const string DefaultTitle = "电脑运行时间过长会导致性能下降，建议重启电脑";
    public const string DefaultContentTemplate = "已运行：{uptime}\r\n请及时关机重启电脑，避免长时间运行造成卡顿";
    public const string DefaultButtonText = "知道了";
    public const string DefaultFontFamily = "Microsoft YaHei UI";

    public static ReminderStyleDto CreateDefault()
    {
        return new ReminderStyleDto
        {
            Title = DefaultTitle,
            ContentTemplate = DefaultContentTemplate,
            ButtonText = DefaultButtonText,
            Width = 420,
            Height = 220,
            Position = "BottomRight",
            CornerRadius = 0,
            BorderWidth = 2,
            BorderColor = "#DC2626",
            BackgroundColor = "#FFFFFF",
            BackgroundImageUrl = "",
            BackgroundImageLayout = "Zoom",
            IconType = "Warning",
            TitleFontFamily = DefaultFontFamily,
            TitleFontSize = 14,
            TitleFontStyle = "Bold",
            TitleColor = "#B91C1C",
            ContentFontFamily = DefaultFontFamily,
            ContentFontSize = 10.5,
            ContentFontStyle = "Regular",
            ContentColor = "#7F1D1D",
            ButtonFontFamily = DefaultFontFamily,
            ButtonFontSize = 9.5,
            ButtonFontStyle = "Bold",
            ButtonTextColor = "#FFFFFF",
            ButtonBackgroundColor = "#DC2626",
            TopMost = true,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
    }
}

public sealed class ReminderStyleDto
{
    public string Title { get; set; } = ReminderStyleDefaults.DefaultTitle;
    public string ContentTemplate { get; set; } = ReminderStyleDefaults.DefaultContentTemplate;
    public string ButtonText { get; set; } = ReminderStyleDefaults.DefaultButtonText;
    public int Width { get; set; } = 420;
    public int Height { get; set; } = 220;
    public string Position { get; set; } = "BottomRight";
    public int CornerRadius { get; set; }
    public int BorderWidth { get; set; } = 2;
    public string BorderColor { get; set; } = "#DC2626";
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public string BackgroundImageUrl { get; set; } = "";
    public string BackgroundImageLayout { get; set; } = "Zoom";
    public string IconType { get; set; } = "Warning";
    public string TitleFontFamily { get; set; } = ReminderStyleDefaults.DefaultFontFamily;
    public double TitleFontSize { get; set; } = 14;
    public string TitleFontStyle { get; set; } = "Bold";
    public string TitleColor { get; set; } = "#B91C1C";
    public string ContentFontFamily { get; set; } = ReminderStyleDefaults.DefaultFontFamily;
    public double ContentFontSize { get; set; } = 10.5;
    public string ContentFontStyle { get; set; } = "Regular";
    public string ContentColor { get; set; } = "#7F1D1D";
    public string ButtonFontFamily { get; set; } = ReminderStyleDefaults.DefaultFontFamily;
    public double ButtonFontSize { get; set; } = 9.5;
    public string ButtonFontStyle { get; set; } = "Bold";
    public string ButtonTextColor { get; set; } = "#FFFFFF";
    public string ButtonBackgroundColor { get; set; } = "#DC2626";
    public bool TopMost { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UnixEpoch;
}

public sealed record ReminderBackgroundImageUploadResult(string Url);

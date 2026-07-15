using System.Globalization;
using LanAdmin.Contracts;

namespace LanAdmin.Server;

internal static class ReminderStyleValidator
{
    private static readonly HashSet<string> ValidPositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "BottomRight",
        "BottomLeft",
        "TopRight",
        "TopLeft",
        "Center"
    };

    private static readonly HashSet<string> ValidImageLayouts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Zoom",
        "Stretch",
        "Center",
        "Tile",
        "None"
    };

    private static readonly HashSet<string> ValidIconTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Warning",
        "Information",
        "Error",
        "Success",
        "None"
    };

    private static readonly HashSet<string> ValidFontStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Regular",
        "Bold",
        "Italic",
        "BoldItalic"
    };

    public static bool TryNormalize(ReminderStyleDto? input, out ReminderStyleDto style, out string? errorMessage)
    {
        style = ReminderStyleDefaults.CreateDefault();
        errorMessage = null;

        if (input is null)
        {
            errorMessage = "Reminder style is required.";
            return false;
        }

        style = new ReminderStyleDto
        {
            Title = NormalizeText(input.Title, ReminderStyleDefaults.DefaultTitle),
            ContentTemplate = NormalizeText(input.ContentTemplate, ReminderStyleDefaults.DefaultContentTemplate),
            ButtonText = NormalizeText(input.ButtonText, ReminderStyleDefaults.DefaultButtonText),
            Width = input.Width,
            Height = input.Height,
            Position = NormalizeToken(input.Position, "BottomRight"),
            CornerRadius = input.CornerRadius,
            BorderWidth = input.BorderWidth,
            BorderColor = NormalizeColor(input.BorderColor, "#DC2626"),
            BackgroundColor = NormalizeColor(input.BackgroundColor, "#FFFFFF"),
            BackgroundImageUrl = input.BackgroundImageUrl?.Trim() ?? "",
            BackgroundImageLayout = NormalizeToken(input.BackgroundImageLayout, "Zoom"),
            IconType = NormalizeToken(input.IconType, "Warning"),
            TitleFontFamily = NormalizeText(input.TitleFontFamily, ReminderStyleDefaults.DefaultFontFamily),
            TitleFontSize = input.TitleFontSize,
            TitleFontStyle = NormalizeToken(input.TitleFontStyle, "Bold"),
            TitleColor = NormalizeColor(input.TitleColor, "#B91C1C"),
            ContentFontFamily = NormalizeText(input.ContentFontFamily, ReminderStyleDefaults.DefaultFontFamily),
            ContentFontSize = input.ContentFontSize,
            ContentFontStyle = NormalizeToken(input.ContentFontStyle, "Regular"),
            ContentColor = NormalizeColor(input.ContentColor, "#7F1D1D"),
            ButtonFontFamily = NormalizeText(input.ButtonFontFamily, ReminderStyleDefaults.DefaultFontFamily),
            ButtonFontSize = input.ButtonFontSize,
            ButtonFontStyle = NormalizeToken(input.ButtonFontStyle, "Bold"),
            ButtonTextColor = NormalizeColor(input.ButtonTextColor, "#FFFFFF"),
            ButtonBackgroundColor = NormalizeColor(input.ButtonBackgroundColor, "#DC2626"),
            TopMost = input.TopMost,
            UpdatedAt = input.UpdatedAt
        };

        return Validate(style, out errorMessage);
    }

    private static bool Validate(ReminderStyleDto style, out string? errorMessage)
    {
        if (!ValidateText(style.Title, "Title", required: true, ReminderStyleDefaults.MaxTextLength, out errorMessage) ||
            !ValidateText(style.ContentTemplate, "Content template", required: true, ReminderStyleDefaults.MaxTextLength, out errorMessage) ||
            !ValidateText(style.ButtonText, "Button text", required: true, 40, out errorMessage) ||
            !ValidateText(style.TitleFontFamily, "Title font family", required: true, ReminderStyleDefaults.MaxFontFamilyLength, out errorMessage) ||
            !ValidateText(style.ContentFontFamily, "Content font family", required: true, ReminderStyleDefaults.MaxFontFamilyLength, out errorMessage) ||
            !ValidateText(style.ButtonFontFamily, "Button font family", required: true, ReminderStyleDefaults.MaxFontFamilyLength, out errorMessage))
        {
            return false;
        }

        if (!ValidateRange(style.Width, ReminderStyleDefaults.MinWidth, ReminderStyleDefaults.MaxWidth, "Width", out errorMessage) ||
            !ValidateRange(style.Height, ReminderStyleDefaults.MinHeight, ReminderStyleDefaults.MaxHeight, "Height", out errorMessage) ||
            !ValidateRange(style.CornerRadius, ReminderStyleDefaults.MinCornerRadius, ReminderStyleDefaults.MaxCornerRadius, "Corner radius", out errorMessage) ||
            !ValidateRange(style.BorderWidth, ReminderStyleDefaults.MinBorderWidth, ReminderStyleDefaults.MaxBorderWidth, "Border width", out errorMessage) ||
            !ValidateRange(style.TitleFontSize, ReminderStyleDefaults.MinFontSize, ReminderStyleDefaults.MaxFontSize, "Title font size", out errorMessage) ||
            !ValidateRange(style.ContentFontSize, ReminderStyleDefaults.MinFontSize, ReminderStyleDefaults.MaxFontSize, "Content font size", out errorMessage) ||
            !ValidateRange(style.ButtonFontSize, ReminderStyleDefaults.MinFontSize, ReminderStyleDefaults.MaxFontSize, "Button font size", out errorMessage))
        {
            return false;
        }

        if (!ValidPositions.Contains(style.Position))
        {
            errorMessage = "Position must be one of BottomRight, BottomLeft, TopRight, TopLeft, Center.";
            return false;
        }

        if (!ValidImageLayouts.Contains(style.BackgroundImageLayout))
        {
            errorMessage = "Background image layout must be one of Zoom, Stretch, Center, Tile, None.";
            return false;
        }

        if (!ValidIconTypes.Contains(style.IconType))
        {
            errorMessage = "Icon type must be one of Warning, Information, Error, Success, None.";
            return false;
        }

        if (!ValidateFontStyle(style.TitleFontStyle, "Title font style", out errorMessage) ||
            !ValidateFontStyle(style.ContentFontStyle, "Content font style", out errorMessage) ||
            !ValidateFontStyle(style.ButtonFontStyle, "Button font style", out errorMessage))
        {
            return false;
        }

        if (!ValidateColor(style.BorderColor, "Border color", out errorMessage) ||
            !ValidateColor(style.BackgroundColor, "Background color", out errorMessage) ||
            !ValidateColor(style.TitleColor, "Title color", out errorMessage) ||
            !ValidateColor(style.ContentColor, "Content color", out errorMessage) ||
            !ValidateColor(style.ButtonTextColor, "Button text color", out errorMessage) ||
            !ValidateColor(style.ButtonBackgroundColor, "Button background color", out errorMessage))
        {
            return false;
        }

        if (style.BackgroundImageUrl.Length > ReminderStyleDefaults.MaxImageUrlLength)
        {
            errorMessage = $"Background image URL must be {ReminderStyleDefaults.MaxImageUrlLength} characters or fewer.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(style.BackgroundImageUrl) &&
            !Uri.TryCreate(style.BackgroundImageUrl, UriKind.RelativeOrAbsolute, out _))
        {
            errorMessage = "Background image URL is invalid.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private static string NormalizeText(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeToken(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized.ToUpperInvariant();
    }

    private static bool ValidateText(string value, string name, bool required, int maxLength, out string? errorMessage)
    {
        if (required && string.IsNullOrWhiteSpace(value))
        {
            errorMessage = $"{name} is required.";
            return false;
        }

        if (value.Length > maxLength)
        {
            errorMessage = $"{name} must be {maxLength} characters or fewer.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private static bool ValidateRange(int value, int min, int max, string name, out string? errorMessage)
    {
        if (value < min || value > max)
        {
            errorMessage = $"{name} must be between {min} and {max}.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private static bool ValidateRange(double value, double min, double max, string name, out string? errorMessage)
    {
        if (double.IsNaN(value) || value < min || value > max)
        {
            errorMessage = $"{name} must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private static bool ValidateColor(string value, string name, out string? errorMessage)
    {
        if (value.Length != 7 || value[0] != '#' || !value.Skip(1).All(IsHex))
        {
            errorMessage = $"{name} must use #RRGGBB format.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private static bool ValidateFontStyle(string value, string name, out string? errorMessage)
    {
        if (!ValidFontStyles.Contains(value))
        {
            errorMessage = $"{name} must be one of Regular, Bold, Italic, BoldItalic.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private static bool IsHex(char value)
    {
        return value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';
    }
}

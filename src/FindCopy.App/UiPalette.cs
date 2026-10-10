using System.Windows;
using System.Windows.Media;

namespace FindCopy.App;

/// <summary>Keep custom surfaces and text in the same Windows high-contrast palette.</summary>
internal sealed class UiPalette
{
    private readonly Dictionary<string, Brush> _normal = new();

    internal UiPalette(ResourceDictionary resources)
    {
        foreach (object key in resources.Keys)
            if (key is string name && resources[key] is SolidColorBrush brush)
                _normal[name] = brush;
    }

    internal void Apply(ResourceDictionary resources)
    {
        foreach (var (key, normal) in _normal)
        {
            Brush brush = normal;
            if (SystemParameters.HighContrast)
            {
                brush = key switch
                {
                    "Accent" or "AccentHover" or "AccentPressed" or "Danger" or "DangerHover" or "DangerPressed"
                        => SystemColors.HighlightBrush,
                    "AccentText" => SystemColors.HighlightTextBrush,
                    "WindowBg" or "CardBg" or "SurfaceAlt" or "ButtonHover" or "ButtonPressed" or "ProgressBg"
                        or "NumberBg" or "HashBadgeBg" or "ExactBadgeBg" => SystemColors.WindowBrush,
                    _ => SystemColors.WindowTextBrush,
                };
            }
            resources[key] = brush;
        }
    }
}

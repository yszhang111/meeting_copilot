using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ProactiveAI.Desktop.Views;

/// <summary>x:Bind helper functions.</summary>
public static class Ui
{
    public static Brush SeverityBrush(InfoBarSeverity severity)
    {
        string key = severity switch
        {
            InfoBarSeverity.Success => "SystemFillColorSuccessBrush",
            InfoBarSeverity.Warning => "SystemFillColorCautionBrush",
            InfoBarSeverity.Error => "SystemFillColorCriticalBrush",
            _ => "SystemFillColorAttentionBrush",
        };
        return (Brush)Application.Current.Resources[key];
    }

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static Brush Highlight(bool value) => value
        ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public static Brush Accent(bool value) => value
        ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
        : (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
}

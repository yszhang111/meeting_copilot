using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ProactiveAI.Desktop.Services;

/// <summary>Marshals work onto the UI thread.</summary>
public sealed class UiDispatcher(DispatcherQueue queue)
{
    public DispatcherQueue Queue { get; } = queue;

    public bool HasAccess => Queue.HasThreadAccess;

    public void Post(Action action)
    {
        if (Queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            Queue.TryEnqueue(() => action());
        }
    }
}

/// <summary>Speaker colors. Always paired with a text label; replaced by system colors in high contrast.</summary>
public static class SpeakerPalette
{
    private static readonly Color[] Colors =
    [
        Color.FromArgb(255, 37, 99, 235),
        Color.FromArgb(255, 219, 39, 119),
        Color.FromArgb(255, 5, 150, 105),
        Color.FromArgb(255, 217, 119, 6),
        Color.FromArgb(255, 124, 58, 237),
        Color.FromArgb(255, 8, 145, 178),
        Color.FromArgb(255, 220, 38, 38),
        Color.FromArgb(255, 101, 163, 13),
    ];

    private static readonly Dictionary<int, SolidColorBrush> Cache = new();
    private static SolidColorBrush? _neutral;

    public static bool HighContrast { get; set; }

    public static Brush For(int index)
    {
        if (HighContrast || index < 0)
        {
            return Neutral();
        }
        int i = index % Colors.Length;
        if (!Cache.TryGetValue(i, out var brush))
        {
            brush = new SolidColorBrush(Colors[i]);
            Cache[i] = brush;
        }
        return brush;
    }

    public static Brush Neutral() =>
        _neutral ??= Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var b) && b is SolidColorBrush sb
            ? sb
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}

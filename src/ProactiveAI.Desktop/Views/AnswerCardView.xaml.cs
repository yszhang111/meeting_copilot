using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProactiveAI.Desktop.ViewModels;

namespace ProactiveAI.Desktop.Views;

public sealed partial class AnswerCardView : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(CardViewModel), typeof(AnswerCardView), new PropertyMetadata(null, OnCardChanged));

    public static readonly DependencyProperty HostProperty = DependencyProperty.Register(
        nameof(Host), typeof(LiveSessionViewModel), typeof(AnswerCardView), new PropertyMetadata(null));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(AnswerCardView), new PropertyMetadata(false, OnCompactChanged));

    private bool _programmaticScroll;

    public AnswerCardView()
    {
        InitializeComponent();
    }

    public CardViewModel? Card
    {
        get => (CardViewModel?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    public LiveSessionViewModel? Host
    {
        get => (LiveSessionViewModel?)GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public double BodyFontSize => Compact ? 14 : 15;

    public double LineHeight => Compact ? 21 : 24;

    public double QuestionFontSize => Compact ? 16 : 20;

    public void ScrollToTop()
    {
        _programmaticScroll = true;
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private static void OnCardChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // A different answer starts at the top; streaming updates of the same answer never move the reading position.
        if (d is AnswerCardView v && !ReferenceEquals(e.OldValue, e.NewValue))
        {
            v.ScrollToTop();
        }
    }

    private static void OnCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnswerCardView v)
        {
            v.Bindings.Update();
        }
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_programmaticScroll)
        {
            if (!e.IsIntermediate)
            {
                _programmaticScroll = false;
            }
            return;
        }
        if (Scroller.VerticalOffset > 4)
        {
            Host?.NotifyReadingActivity();
        }
    }

    private void Scroller_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => Host?.NotifyReadingActivity();
}

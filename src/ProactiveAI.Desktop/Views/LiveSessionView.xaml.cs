using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Desktop.ViewModels;

namespace ProactiveAI.Desktop.Views;

public sealed partial class LiveSessionView : UserControl
{
    private ScrollViewer? _transcriptScroller;
    private bool _atBottom = true;

    public LiveSessionView()
    {
        ViewModel = App.Current!.GetService<LiveSessionViewModel>();
        InitializeComponent();
        ViewModel.SegmentsAppended += OnSegmentsAppended;
        ViewModel.EvidenceRequested += OnEvidenceRequested;
        ViewModel.FocusedCardReplaced += () => FocusedAnswer.ScrollToTop();
        ViewModel.ConfirmDisclosure = ConfirmDisclosureAsync;
        TranscriptList.Loaded += (_, _) => AttachScroller();
    }

    public LiveSessionViewModel ViewModel { get; }

    // ------------------------------------------------------------------ transcript scrolling

    private void AttachScroller()
    {
        _transcriptScroller ??= FindChild<ScrollViewer>(TranscriptList);
        if (_transcriptScroller is not null)
        {
            _transcriptScroller.ViewChanged += (_, _) => UpdateAtBottom();
        }
    }

    private void UpdateAtBottom()
    {
        if (_transcriptScroller is null)
        {
            return;
        }
        _atBottom = _transcriptScroller.VerticalOffset >= _transcriptScroller.ScrollableHeight - 32;
        JumpToLiveButton.Visibility = _atBottom ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSegmentsAppended()
    {
        // Auto-scroll only while the user is at the bottom; otherwise keep their position.
        if (_atBottom && ViewModel.Segments.Count > 0)
        {
            TranscriptList.ScrollIntoView(ViewModel.Segments[^1], ScrollIntoViewAlignment.Leading);
        }
    }

    private void JumpToLive_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Segments.Count > 0)
        {
            _atBottom = true;
            TranscriptList.ScrollIntoView(ViewModel.Segments[^1], ScrollIntoViewAlignment.Leading);
            _transcriptScroller?.ChangeView(null, _transcriptScroller.ScrollableHeight, null, true);
        }
    }

    private async void OnEvidenceRequested(IReadOnlyList<string> ids)
    {
        var first = ViewModel.Segments.FirstOrDefault(s => ids.Contains(s.Id));
        if (first is null)
        {
            return;
        }
        _atBottom = false;
        TranscriptList.ScrollIntoView(first, ScrollIntoViewAlignment.Leading);
        JumpToLiveButton.Visibility = Visibility.Visible;
        await Task.Delay(6000);
        foreach (var s in ViewModel.Segments)
        {
            s.IsHighlighted = false;
        }
    }

    // ------------------------------------------------------------------ selection / manual answer

    private void TranscriptList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.HasSelection = TranscriptList.SelectedItems.OfType<SegmentViewModel>().Any(s => s.IsSpeech && s.IsFinal);

    private void AnswerSelection_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AnswerSelection(TranscriptList.SelectedItems.OfType<SegmentViewModel>().ToList());
        TranscriptList.SelectedItems.Clear();
        PanelSelector.SelectedItem = AssistantTab;
    }

    // ------------------------------------------------------------------ speakers

    private void SpeakerLabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string segmentId } element)
        {
            return;
        }
        var segment = ViewModel.Segments.FirstOrDefault(s => s.Id == segmentId);
        if (segment is null)
        {
            return;
        }
        var menu = new MenuFlyout();
        var currentId = segment.Model.Attribution.SpeakerId;
        if (currentId is not null && ViewModel.Speakers.FirstOrDefault(s => s.Id == currentId) is { } current)
        {
            var rename = new MenuFlyoutItem { Text = $"Rename {current.Name}…", Icon = new FontIcon { Glyph = "\uE70F" } };
            rename.Click += async (_, _) => await RenameAsync(current);
            menu.Items.Add(rename);
            var role = new MenuFlyoutSubItem { Text = "Set role", Icon = new FontIcon { Glyph = "\uE77B" } };
            foreach (var r in new[] { SpeakerRole.Interviewer, SpeakerRole.Examiner, SpeakerRole.Participant, SpeakerRole.Me, SpeakerRole.Unknown })
            {
                var item = new ToggleMenuFlyoutItem { Text = r == SpeakerRole.Unknown ? "No role" : r.ToString(), IsChecked = current.Model.Role == r };
                item.Click += (_, _) => ViewModel.SetSpeakerRole(current.Id, r);
                role.Items.Add(item);
            }
            menu.Items.Add(role);
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        var assign = new MenuFlyoutSubItem { Text = "This line was said by", Icon = new FontIcon { Glyph = "\uE716" } };
        foreach (var sp in ViewModel.Speakers)
        {
            var item = new ToggleMenuFlyoutItem { Text = sp.Name, IsChecked = sp.Id == currentId };
            item.Click += (_, _) => ViewModel.AssignSegment(segmentId, sp.Id);
            assign.Items.Add(item);
        }
        if (!ViewModel.Speakers.Any(s => s.Id == SpeakerRegistry.SelfAliasId))
        {
            var me = new MenuFlyoutItem { Text = "Me" };
            me.Click += (_, _) => ViewModel.AssignSegment(segmentId, SpeakerRegistry.SelfAliasId);
            assign.Items.Add(me);
        }
        menu.Items.Add(assign);
        menu.ShowAt(element);
    }

    private async void SpeakerList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SpeakerViewModel speaker)
        {
            await RenameAsync(speaker);
        }
    }

    private async Task RenameAsync(SpeakerViewModel speaker)
    {
        var box = new TextBox { Text = speaker.Model.DisplayName ?? string.Empty, PlaceholderText = speaker.Model.Label, MaxLength = 40 };
        var roles = new ComboBox { Header = "Role", HorizontalAlignment = HorizontalAlignment.Stretch };
        var roleValues = new[] { SpeakerRole.Unknown, SpeakerRole.Interviewer, SpeakerRole.Examiner, SpeakerRole.Participant, SpeakerRole.Me };
        foreach (var r in roleValues)
        {
            roles.Items.Add(r == SpeakerRole.Unknown ? "No role" : r.ToString());
        }
        roles.SelectedIndex = Array.IndexOf(roleValues, speaker.Model.Role);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Rename {speaker.Model.Label}",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "The name is used only in this session. Leave empty to use the automatic label.", TextWrapping = TextWrapping.Wrap },
                    box,
                    roles,
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.RenameSpeaker(speaker.Id, box.Text);
            if (roles.SelectedIndex >= 0)
            {
                ViewModel.SetSpeakerRole(speaker.Id, roleValues[roles.SelectedIndex]);
            }
        }
    }

    // ------------------------------------------------------------------ panels

    private void PanelSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        AssistantContent.Visibility = sender.SelectedItem == AssistantTab ? Visibility.Visible : Visibility.Collapsed;
        HistoryContent.Visibility = sender.SelectedItem == HistoryTab ? Visibility.Visible : Visibility.Collapsed;
        PrepContent.Visibility = sender.SelectedItem == PrepTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private void History_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CardViewModel card)
        {
            ViewModel.ViewCardCommand.Execute(card);
            PanelSelector.SelectedItem = AssistantTab;
        }
    }

    private CardViewModel? Insight(object sender) =>
        sender is FrameworkElement { Tag: string id } ? ViewModel.Insights.FirstOrDefault(i => i.Id == id) : null;

    private void InsightEvidence_Click(object sender, RoutedEventArgs e) => ViewModel.ShowEvidenceCommand.Execute(Insight(sender));

    private void InsightCopy_Click(object sender, RoutedEventArgs e) => ViewModel.CopyCommand.Execute(Insight(sender));

    private void InsightDismiss_Click(object sender, RoutedEventArgs e) => ViewModel.DismissCommand.Execute(Insight(sender));

    private void ConfirmReset_Click(object sender, RoutedEventArgs e)
    {
        ResetFlyout.Hide();
        ViewModel.ResetCommand.Execute(null);
    }

    private void ConfirmClearPrep_Click(object sender, RoutedEventArgs e)
    {
        ClearPrepFlyout.Hide();
        ViewModel.ClearPreparationCommand.Execute(null);
    }

    public void ShowPreparation() => PanelSelector.SelectedItem = PrepTab;

    // ------------------------------------------------------------------ disclosure

    private async Task<bool> ConfirmDisclosureAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot ?? App.Current?.MainXamlRoot,
            Title = "Start listening?",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text =
                        "• System audio: everything playing on the selected output device (including other apps) is streamed to OpenAI for live transcription.\n" +
                        "• Microphone: if it is on (the default), it is streamed the same way.\n" +
                        "• Speaker labels: short audio windows (about 20 s) and a few temporary voice snippets are kept in memory and uploaded to OpenAI to tell speakers apart. They are deleted when you stop, reset or exit.\n" +
                        "• Answers: recent transcript text, earlier questions of this session with your replies, relevant preparation notes and (interview mode) your selected résumé are sent to the configured OpenAI model.\n" +
                        "• Audio is never saved. The transcript text and answers are saved to History on this PC, encrypted for your Windows account (Settings > Session history to turn off or delete). Cloud processing is governed by your OpenAI account's data policies.\n\n" +
                        "Make sure you have consent from the other participants and follow your organization's rules.",
                },
            },
            PrimaryButtonText = "Start",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t)
            {
                return t;
            }
            if (FindChild<T>(child) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}

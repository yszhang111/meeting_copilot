using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.History;
using ProactiveAI.Core.Session;
using ProactiveAI.Desktop.Services;
using Windows.ApplicationModel.DataTransfer;

namespace ProactiveAI.Desktop.ViewModels;

public sealed class HistoryLineViewModel(HistoryLine line)
{
    public HistoryLine Model { get; } = line;

    public string TimeText => SessionSummaryViewModel.Format(Model.Start < TimeSpan.Zero ? TimeSpan.Zero : Model.Start);

    public string Speaker => Model.Speaker;

    public string Text => Model.Text;

    public string Glyph => Model.Source == Core.Audio.AudioSourceKind.Microphone ? "\uE720" : "\uE7F5";

    public double TextOpacity => Model.Final ? 1.0 : 0.75;

    /// <summary>Accessible name of the list row.</summary>
    public override string ToString() => $"{TimeText}, {Speaker}: {Text}";
}

public sealed class HistoryCardViewModel(HistoryCard card)
{
    public HistoryCard Model { get; } = card;

    public string KindText => Model.Kind switch
    {
        CardKind.Clarification => "Clarification",
        CardKind.Insight => "Insight",
        _ => Model.State == CardState.Complete ? "Answer" : "Answer (incomplete)",
    };

    public string Heading => !string.IsNullOrWhiteSpace(Model.QuestionText) ? Model.QuestionText! : Model.Title ?? KindText;

    public string? DirectPosition => Model.DirectPosition;

    public string? Body => Model.Kind == CardKind.Answer ? Model.SpokenAnswer : Model.Suggestion;

    public string SupportingText => Bullets(Model.SupportingPoints);

    public string LimitsText => Bullets(Model.AssumptionsAndLimits);

    public string FollowupsText => string.Join("\n\n", Model.LikelyFollowups.Select(f => $"• {f.Question}\n  {f.ResponseDirection}"));

    public string MissingText => Bullets(Model.MissingFacts);

    public string FilledInText => Bullets(Model.FilledInDetails ?? []);

    public string TimeText => Model.CreatedUtc.ToLocalTime().ToString("HH:mm");

    public Visibility DirectVisibility => Vis(DirectPosition);
    public Visibility BodyVisibility => Vis(Body);
    public Visibility SupportingVisibility => Vis(SupportingText);
    public Visibility LimitsVisibility => Vis(LimitsText);
    public Visibility FollowupsVisibility => Vis(FollowupsText);
    public Visibility MissingVisibility => Vis(MissingText);
    public Visibility FilledInVisibility => Vis(FilledInText);

    private static string Bullets(IReadOnlyList<string> items) => string.Join("\n", items.Select(i => "• " + i));

    private static Visibility Vis(string? s) => string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Read-only view of a saved session.</summary>
public sealed partial class SessionDetailViewModel : ObservableObject
{
    private readonly SessionCoordinator _session;
    private readonly AppNavigator _nav;
    private SessionRecord? _record;

    public SessionDetailViewModel(SessionCoordinator session, AppNavigator nav)
    {
        _session = session;
        _nav = nav;
    }

    /// <summary>Raised after the shown session was deleted.</summary>
    public event Action<string>? Deleted;

    public ObservableCollection<HistoryLineViewModel> Lines { get; } = new();

    public ObservableCollection<HistoryCardViewModel> Cards { get; } = new();

    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string DateText { get; set; } = string.Empty;
    [ObservableProperty] public partial string MetaText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CopyStatus { get; set; } = string.Empty;

    public Visibility NoCardsVisibility => Cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoLinesVisibility => Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public void Show(SessionRecord record)
    {
        _record = record;
        var summary = new SessionSummaryViewModel(record.Summary);
        Title = record.Summary.Title;
        DateText = summary.DateText;
        MetaText = summary.MetaText;
        CopyStatus = string.Empty;
        Lines.Clear();
        foreach (var l in record.Lines)
        {
            Lines.Add(new HistoryLineViewModel(l));
        }
        Cards.Clear();
        foreach (var c in record.Cards)
        {
            Cards.Add(new HistoryCardViewModel(c));
        }
        OnPropertyChanged(nameof(NoCardsVisibility));
        OnPropertyChanged(nameof(NoLinesVisibility));
    }

    [RelayCommand]
    private void Back() => _nav.Navigate(AppPage.Home);

    [RelayCommand]
    private void CopyTranscript()
    {
        if (_record is null)
        {
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine(Title).AppendLine(DateText).AppendLine();
        foreach (var l in _record.Lines)
        {
            sb.Append('[').Append(SessionSummaryViewModel.Format(l.Start < TimeSpan.Zero ? TimeSpan.Zero : l.Start)).Append("] ")
              .Append(l.Speaker).Append(": ").AppendLine(l.Text);
        }
        if (_record.Cards.Count > 0)
        {
            sb.AppendLine().AppendLine("---- Answers ----");
            foreach (var c in _record.Cards)
            {
                var vm = new HistoryCardViewModel(c);
                sb.AppendLine().AppendLine("Q: " + vm.Heading);
                if (!string.IsNullOrWhiteSpace(c.DirectPosition))
                {
                    sb.AppendLine(c.DirectPosition);
                }
                if (!string.IsNullOrWhiteSpace(vm.Body))
                {
                    sb.AppendLine().AppendLine(vm.Body);
                }
                if (vm.FilledInText.Length > 0)
                {
                    sb.AppendLine().AppendLine(CardViewModel.FilledInHeading).AppendLine(vm.FilledInText);
                }
            }
        }
        var package = new DataPackage();
        package.SetText(sb.ToString());
        Clipboard.SetContent(package);
        CopyStatus = "Copied to the clipboard";
    }

    public async Task DeleteAsync()
    {
        if (_record is null || _session.Archive is not { } archive)
        {
            return;
        }
        string id = _record.Summary.Id;
        await Task.Run(() => archive.Delete(id));
        _record = null;
        Lines.Clear();
        Cards.Clear();
        Deleted?.Invoke(id);
        _nav.Navigate(AppPage.Home);
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using ProactiveAI.Core.History;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Desktop.Services;

namespace ProactiveAI.Desktop.ViewModels;

public sealed class SessionSummaryViewModel(SessionSummary summary)
{
    public SessionSummary Model { get; } = summary;

    public string Id => Model.Id;

    public string Title => Model.Title;

    public string DateText => Model.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd  HH:mm");

    public string MetaText =>
        $"{Model.Scenario} · {Format(Model.Duration)} · {Model.LineCount} lines · {Model.AnswerCount} {(Model.AnswerCount == 1 ? "answer" : "answers")}" +
        (Model.EndedUtc is null ? " · not stopped normally" : string.Empty);

    public string Glyph => Model.Scenario switch
    {
        Scenario.Meeting => "\uE716",
        Scenario.Defense => "\uE7BE",
        _ => "\uE77B",
    };

    internal static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    /// <summary>Accessible name of the list row.</summary>
    public override string ToString() => $"{Title}, {DateText}, {MetaText}";
}

/// <summary>Start page: begin a new session (via the device check) or open a saved one.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly SessionCoordinator _session;
    private readonly ISessionArchive? _archive;
    private readonly AppNavigator _nav;
    private readonly SessionDetailViewModel _detail;
    private readonly UiDispatcher _ui;
    private int _loadVersion;

    public HomeViewModel(SessionCoordinator session, AppNavigator nav, SessionDetailViewModel detail, UiDispatcher ui)
    {
        _session = session;
        _archive = session.Archive;
        _nav = nav;
        _detail = detail;
        _ui = ui;
        session.ArchiveSaved += _ => _ui.Post(Refresh);
        session.StatusChanged += snap => _ui.Post(() => ApplyState(snap.State));
        detail.Deleted += _ => Refresh();
        Refresh();
    }

    public ObservableCollection<SessionSummaryViewModel> Sessions { get; } = new();

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool SessionActive { get; set; }
    [ObservableProperty] public partial bool CanReturn { get; set; }
    [ObservableProperty] public partial string SessionNote { get; set; } = string.Empty;
    [ObservableProperty] public partial string EmptyText { get; set; } = string.Empty;

    public Visibility EmptyVisibility => !IsLoading && Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The live session page is only reachable from here: while a session runs, or after it failed (to retry).</summary>
    public Visibility ReturnVisibility => CanReturn ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SessionNoteVisibility => string.IsNullOrEmpty(SessionNote) ? Visibility.Collapsed : Visibility.Visible;

    public bool CanStartNew => !SessionActive;

    partial void OnSessionActiveChanged(bool value) => OnPropertyChanged(nameof(CanStartNew));

    partial void OnCanReturnChanged(bool value) => OnPropertyChanged(nameof(ReturnVisibility));

    partial void OnSessionNoteChanged(string value) => OnPropertyChanged(nameof(SessionNoteVisibility));

    private void ApplyState(SessionState state)
    {
        SessionActive = state is not (SessionState.Stopped or SessionState.Error);
        CanReturn = state is not SessionState.Stopped;
        SessionNote = state switch
        {
            SessionState.Stopped => string.Empty,
            SessionState.Error => "The last session stopped because of an error. Return to it to see what happened and retry, or start a new one.",
            _ => "A session is running. Return to it, or stop it before starting a new one.",
        };
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(EmptyVisibility));

    [RelayCommand]
    private void NewSession() => _nav.Navigate(AppPage.Setup);

    [RelayCommand]
    private void ReturnToSession() => _nav.Navigate(AppPage.Live);

    public async Task OpenAsync(SessionSummaryViewModel item)
    {
        if (_archive is null)
        {
            return;
        }
        var record = await Task.Run(() => _archive.Load(item.Id));
        if (record is null)
        {
            EmptyText = "That session could not be opened (it may have been deleted or damaged).";
            Refresh();
            return;
        }
        _detail.Show(record);
        _nav.Navigate(AppPage.Detail);
    }

    public async Task DeleteAsync(SessionSummaryViewModel item)
    {
        if (_archive is null)
        {
            return;
        }
        await Task.Run(() => _archive.Delete(item.Id));
        Sessions.Remove(item);
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    public void Refresh()
    {
        ApplyState(_session.State);
        bool enabled = _session.Settings.SaveSessionHistory;
        if (_archive is null)
        {
            EmptyText = "Session history is not available.";
            return;
        }
        int version = Interlocked.Increment(ref _loadVersion);
        IsLoading = true;
        _ = Task.Run(() => _archive.List()).ContinueWith(t => _ui.Post(() =>
        {
            if (version != _loadVersion)
            {
                return;
            }
            Sessions.Clear();
            if (t.IsCompletedSuccessfully)
            {
                foreach (var s in t.Result)
                {
                    Sessions.Add(new SessionSummaryViewModel(s));
                }
            }
            EmptyText = enabled
                ? "No saved sessions yet. When you stop a session, its transcript and answers are saved here (text only, encrypted for your Windows account)."
                : "Session history is turned off in Settings. New sessions are not saved.";
            IsLoading = false;
            OnPropertyChanged(nameof(EmptyVisibility));
        }), TaskScheduler.Default);
    }
}

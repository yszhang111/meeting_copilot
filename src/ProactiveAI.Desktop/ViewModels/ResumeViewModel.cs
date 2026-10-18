using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Desktop.Services;

namespace ProactiveAI.Desktop.ViewModels;

/// <summary>An entry of the résumé chooser; <see cref="Info"/> is null for "No résumé".</summary>
public sealed class ResumeOption(ResumeInfo? info)
{
    public ResumeInfo? Info { get; } = info;

    public string? Id => Info?.Id;

    public string Name => Info?.Name ?? "No résumé";

    public override string ToString() => Name;
}

/// <summary>
/// Saved résumé versions (text only, encrypted) and the one used in interview sessions. The last choice is pre-selected and
/// visible on the new-session page; it can be changed at any time, also during a session.
/// </summary>
public sealed partial class ResumeViewModel : ObservableObject
{
    private static readonly ResumeOption None = new(null);

    private readonly SessionCoordinator _session;
    private readonly IResumeLibrary _library;
    private readonly IDocumentTextExtractor _extractor;
    private readonly ISettingsStore _store;
    private readonly UiDispatcher _ui;
    private readonly ILogger _log;
    private bool _loading;

    public ResumeViewModel(SessionCoordinator session, IResumeLibrary library, IDocumentTextExtractor extractor, ISettingsStore store, UiDispatcher ui,
        ILogger<ResumeViewModel> log)
    {
        _session = session;
        _library = library;
        _extractor = extractor;
        _store = store;
        _ui = ui;
        _log = log;
        Reload(session.Settings.SelectedResumeId);
    }

    public ObservableCollection<ResumeOption> Options { get; } = new();

    [ObservableProperty]
    public partial ResumeOption? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; }

    [ObservableProperty]
    public partial string PreviewText { get; set; } = string.Empty;

    public bool HasResume => Selected?.Info is not null;

    public Visibility ResumeVisibility => HasResume ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StatusVisibility => string.IsNullOrEmpty(Status) ? Visibility.Collapsed : Visibility.Visible;

    public string Detail => Selected?.Info is { } i
        ? $"{i.Format.ToUpperInvariant()} · {i.Chars:N0} characters · added {i.AddedUtc.ToLocalTime():yyyy-MM-dd HH:mm}" + (i.Note is null ? string.Empty : $" · {i.Note}")
        : Options.Count > 1 ? "Answers will not use a résumé." : "No résumé saved yet. Upload a PDF or Word (.docx) file.";

    public string FileTypes => string.Join(", ", _extractor.SupportedExtensions);

    public IReadOnlyList<string> SupportedExtensions => _extractor.SupportedExtensions;

    /// <summary>Extracts the text locally, saves it encrypted and selects it.</summary>
    public async Task AddFileAsync(string path)
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        SetStatus(null);
        try
        {
            var extracted = await _extractor.ExtractAsync(path, CancellationToken.None);
            var info = new ResumeInfo(Guid.NewGuid().ToString("N"), Path.GetFileName(path), extracted.Format, extracted.Text.Length, DateTimeOffset.UtcNow, extracted.Note);
            await Task.Run(() => _library.Save(new Resume(info, extracted.Text)));
            _log.LogInformation("Résumé added: format={Format} chars={Chars}", info.Format, info.Chars);
            Reload(info.Id);
            SetStatus(extracted.Note is null ? "Résumé saved. Check the extracted text below." : extracted.Note,
                extracted.Note is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (DocumentReadException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Résumé could not be added: {Type}", ex.GetType().Name);
            SetStatus("The résumé could not be saved.", InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (Selected?.Id is not { } id)
        {
            return;
        }
        _library.Delete(id);
        _log.LogInformation("Résumé deleted");
        Reload(null);
        SetStatus("Résumé deleted from this PC.", InfoBarSeverity.Informational);
    }

    /// <summary>Removes all saved résumés (Settings).</summary>
    public void DeleteAll()
    {
        _library.DeleteAll();
        Reload(null);
    }

    public void ReportPickerFailure(Exception ex)
    {
        _log.LogWarning("File picker failed: {Type} 0x{HResult:X8}", ex.GetType().Name, ex.HResult);
        SetStatus("The file dialog could not be opened. You can drag the résumé file onto this section instead.", InfoBarSeverity.Error);
    }

    partial void OnSelectedChanged(ResumeOption? value)
    {
        OnPropertyChanged(nameof(HasResume));
        OnPropertyChanged(nameof(ResumeVisibility));
        OnPropertyChanged(nameof(Detail));
        if (_loading)
        {
            return;
        }
        Apply(value);
        var s = _session.Settings with { SelectedResumeId = value?.Id };
        _session.UpdateSettings(s);
        _store.Save(s);
    }

    partial void OnStatusChanged(string? value) => OnPropertyChanged(nameof(StatusVisibility));

    private void Reload(string? selectId)
    {
        _loading = true;
        try
        {
            Options.Clear();
            Options.Add(None);
            foreach (var info in _library.List())
            {
                Options.Add(new ResumeOption(info));
            }
            Selected = Options.FirstOrDefault(o => o.Id is not null && o.Id == selectId) ?? None;
        }
        finally
        {
            _loading = false;
        }
        Apply(Selected);
        if (_session.Settings.SelectedResumeId != Selected?.Id)
        {
            var s = _session.Settings with { SelectedResumeId = Selected?.Id };
            _session.UpdateSettings(s);
            _store.Save(s);
        }
        OnPropertyChanged(nameof(Detail));
    }

    private void Apply(ResumeOption? option)
    {
        var resume = option?.Id is { } id ? _library.Load(id) : null;
        if (option?.Id is not null && resume is null)
        {
            SetStatus("This résumé could not be read.", InfoBarSeverity.Error);
        }
        _session.Preparation.SetResume(resume);
        PreviewText = resume?.Text ?? string.Empty;
    }

    private void SetStatus(string? text, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusSeverity = severity;
        Status = text;
    }
}

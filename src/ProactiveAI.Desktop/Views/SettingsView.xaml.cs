using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProactiveAI.Desktop.ViewModels;
using ProactiveAI.Infrastructure.Logging;

namespace ProactiveAI.Desktop.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        ViewModel = App.Current!.GetService<SettingsViewModel>();
        InitializeComponent();
        LogPathText.Text = "Diagnostic log: " + App.Current.GetService<BoundedFileLoggerProvider>().LogPath;
        if (App.Current.GetService<Core.Context.IResumeLibrary>() is Infrastructure.Documents.EncryptedResumeLibrary library)
        {
            ResumeLocationText.Text = "Résumés (extracted text only, encrypted for your Windows account): " + library.Directory;
        }
    }

    public SettingsViewModel ViewModel { get; }

    private void ConfirmDeleteResumes_Click(object sender, RoutedEventArgs e)
    {
        DeleteResumesFlyout.Hide();
        App.Current!.GetService<ResumeViewModel>().DeleteAll();
        ResumeStatusText.Text = "All saved résumés deleted";
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveApiKey(ApiKeyBox.Password);
        ApiKeyBox.Password = string.Empty;
        App.Current!.GetService<LiveSessionViewModel>().RefreshCredentialState();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => ViewModel.RefreshDevices();

    private async void ConfirmDeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        DeleteHistoryFlyout.Hide();
        await ViewModel.DeleteAllHistoryAsync();
    }
}

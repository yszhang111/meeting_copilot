using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProactiveAI.Desktop.ViewModels;

namespace ProactiveAI.Desktop.Views;

/// <summary>
/// Shown when the app starts without an OpenAI API key: asks for the key, stores it in Credential Manager and checks it
/// once with OpenAI. "Later" closes it; the app then explains on the New session page that a key is needed.
/// </summary>
public static class ApiKeyPrompt
{
    public static async Task ShowAsync(XamlRoot root)
    {
        var settings = App.Current!.GetService<SettingsViewModel>();
        var keyBox = new PasswordBox
        {
            PlaceholderText = "sk-…",
            Header = "OpenAI API key",
        };
        var status = new InfoBar { IsClosable = false, IsOpen = false };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var content = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 460,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Meeting Copilot uses your own OpenAI API key for live transcription and answers. Usage is billed to your OpenAI account.",
                },
                new HyperlinkButton
                {
                    Content = "Create a key at platform.openai.com/api-keys",
                    NavigateUri = new Uri("https://platform.openai.com/api-keys"),
                    Padding = new Thickness(0),
                },
                keyBox,
                progress,
                status,
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["CaptionMutedStyle"],
                    Text = "The key is stored in Windows Credential Manager on this PC. It is never shown again or written to files or logs. You can replace or delete it later in Settings.",
                },
            },
        };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Add your OpenAI API key",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        keyBox.PasswordChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = keyBox.Password.Trim().Length > 0;
            status.IsOpen = false;
        };

        bool saved = false;
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            if (saved)
            {
                return; // "Done" after a warning
            }
            var deferral = e.GetDeferral();
            try
            {
                keyBox.IsEnabled = false;
                dialog.IsPrimaryButtonEnabled = false;
                progress.Visibility = Visibility.Visible;
                status.IsOpen = false;
                var result = await settings.SaveAndVerifyApiKeyAsync(keyBox.Password, CancellationToken.None);
                App.Current!.GetService<LiveSessionViewModel>().RefreshCredentialState();
                switch (result.Outcome)
                {
                    case ApiKeySaveOutcome.Saved:
                        saved = true;
                        keyBox.Password = string.Empty;
                        break;
                    case ApiKeySaveOutcome.SavedWithWarning:
                        saved = true;
                        e.Cancel = true; // keep it open so the warning can be read
                        keyBox.Password = string.Empty;
                        Show(status, InfoBarSeverity.Warning, result.Message);
                        dialog.PrimaryButtonText = "Done";
                        dialog.CloseButtonText = string.Empty;
                        dialog.IsPrimaryButtonEnabled = true;
                        break;
                    default:
                        e.Cancel = true;
                        Show(status, InfoBarSeverity.Error, result.Message);
                        keyBox.IsEnabled = true;
                        dialog.IsPrimaryButtonEnabled = keyBox.Password.Trim().Length > 0;
                        keyBox.Focus(FocusState.Programmatic);
                        break;
                }
            }
            catch (Exception)
            {
                e.Cancel = true;
                Show(status, InfoBarSeverity.Error, "The key could not be stored in Windows Credential Manager. You can try again or add it later in Settings.");
                keyBox.IsEnabled = true;
                dialog.IsPrimaryButtonEnabled = keyBox.Password.Trim().Length > 0;
            }
            finally
            {
                progress.Visibility = Visibility.Collapsed;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    private static void Show(InfoBar bar, InfoBarSeverity severity, string message)
    {
        bar.Severity = severity;
        bar.Message = message;
        bar.IsOpen = true;
    }
}

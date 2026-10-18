using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProactiveAI.Desktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace ProactiveAI.Desktop.Views;

public sealed partial class ResumePickerView : UserControl
{
    public ResumePickerView()
    {
        ViewModel = App.Current!.GetService<ResumeViewModel>();
        InitializeComponent();
    }

    public ResumeViewModel ViewModel { get; }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        string? path;
        try
        {
            // The Windows App SDK picker also works when the app runs elevated (the classic WinRT picker fails there).
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Current!.MainWindowId)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List,
                CommitButtonText = "Use this résumé",
            };
            foreach (var ext in ViewModel.SupportedExtensions)
            {
                picker.FileTypeFilter.Add(ext);
            }
            path = (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception ex)
        {
            ViewModel.ReportPickerFailure(ex);
            return;
        }
        if (!string.IsNullOrEmpty(path))
        {
            await ViewModel.AddFileAsync(path);
        }
    }

    private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        DeleteFlyout.Hide();
        ViewModel.DeleteSelectedCommand.Execute(null);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add résumé";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<StorageFile>().FirstOrDefault();
        if (file is not null)
        {
            await ViewModel.AddFileAsync(file.Path); // unsupported types are reported by the view model
        }
    }
}

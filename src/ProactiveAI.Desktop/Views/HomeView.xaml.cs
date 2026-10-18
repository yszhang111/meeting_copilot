using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProactiveAI.Desktop.ViewModels;

namespace ProactiveAI.Desktop.Views;

public sealed partial class HomeView : UserControl
{
    public HomeView()
    {
        ViewModel = App.Current!.GetService<HomeViewModel>();
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

    private async void SessionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SessionSummaryViewModel item)
        {
            await ViewModel.OpenAsync(item);
        }
    }

    private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SessionSummaryViewModel item } button)
        {
            // The confirmation flyout belongs to an item template: close it through the window's open popups.
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(button.XamlRoot))
            {
                if (popup.Child is FlyoutPresenter)
                {
                    popup.IsOpen = false;
                }
            }
            await ViewModel.DeleteAsync(item);
        }
    }
}

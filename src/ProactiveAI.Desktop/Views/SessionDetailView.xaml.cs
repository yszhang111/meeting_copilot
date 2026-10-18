using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProactiveAI.Desktop.ViewModels;

namespace ProactiveAI.Desktop.Views;

public sealed partial class SessionDetailView : UserControl
{
    public SessionDetailView()
    {
        ViewModel = App.Current!.GetService<SessionDetailViewModel>();
        InitializeComponent();
    }

    public SessionDetailViewModel ViewModel { get; }

    private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        DeleteFlyout.Hide();
        await ViewModel.DeleteAsync();
    }
}

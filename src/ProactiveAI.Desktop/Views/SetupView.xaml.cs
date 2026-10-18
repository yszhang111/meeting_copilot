using Microsoft.UI.Xaml.Controls;
using ProactiveAI.Desktop.ViewModels;

namespace ProactiveAI.Desktop.Views;

public sealed partial class SetupView : UserControl
{
    public SetupView()
    {
        ViewModel = App.Current!.GetService<SetupViewModel>();
        InitializeComponent();
    }

    public SetupViewModel ViewModel { get; }
}

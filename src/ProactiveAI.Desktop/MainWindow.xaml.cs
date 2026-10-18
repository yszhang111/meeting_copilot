using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProactiveAI.Core.Session;
using ProactiveAI.Desktop.Services;
using ProactiveAI.Desktop.ViewModels;
using WinRT.Interop;

namespace ProactiveAI.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else
        {
            RootGrid.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        }
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 560;
        }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min(area.Width - 40, (int)(1400 * Scale));
        int height = Math.Min(area.Height - 40, (int)(900 * Scale));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));

        _nav = App.Current!.GetService<AppNavigator>();
        _nav.Navigated += OnNavigated;
        _lastState = LiveView.ViewModel.State;
        LiveView.ViewModel.PropertyChanged += OnLivePropertyChanged;
    }

    private readonly AppNavigator _nav;
    private SessionState _lastState;

    public IntPtr Hwnd { get; }

    private double Scale => Interop.Win32.GetDpiForWindow(Hwnd) / 96.0;

    public void SetTitleState(string text) => TitleState.Text = text;

    public void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    public void ShowSettings() => _nav.Navigate(AppPage.Settings);

    // ItemInvoked (not SelectionChanged): Home stays highlighted on the pages reached from it, and clicking it must still go Home.
    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            _nav.Navigate(AppPage.Settings);
        }
        else if (args.InvokedItemContainer is NavigationViewItem { Tag: "home" })
        {
            _nav.Navigate(AppPage.Home);
        }
    }

    private void OnNavigated(AppPage previous, AppPage page)
    {
        if (previous == AppPage.Setup)
        {
            SetupPage.ViewModel.Deactivate();
        }
        HomePage.Visibility = Vis(page == AppPage.Home);
        SetupPage.Visibility = Vis(page == AppPage.Setup);
        LiveView.Visibility = Vis(page == AppPage.Live);
        DetailPage.Visibility = Vis(page == AppPage.Detail);
        SettingsPage.Visibility = Vis(page == AppPage.Settings);
        switch (page)
        {
            case AppPage.Home:
                HomePage.ViewModel.Refresh();
                break;
            case AppPage.Setup:
                SetupPage.ViewModel.Activate();
                break;
            case AppPage.Settings:
                SettingsPage.ViewModel.RefreshDevices();
                break;
        }
        Nav.SelectedItem = page == AppPage.Settings ? Nav.SettingsItem : HomeItem;
    }

    private void OnLivePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LiveSessionViewModel.State))
        {
            return;
        }
        var state = LiveView.ViewModel.State;
        // A session that starts (from the setup page, the tray or Retry) is shown on the live page.
        if (_lastState is SessionState.Stopped or SessionState.Error && state is SessionState.Starting or SessionState.Listening)
        {
            _nav.Navigate(AppPage.Live);
        }
        _lastState = state;
    }

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}

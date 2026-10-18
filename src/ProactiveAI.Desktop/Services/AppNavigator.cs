namespace ProactiveAI.Desktop.Services;

public enum AppPage
{
    Home,
    Setup,
    Live,
    Detail,
    Settings,
}

/// <summary>Page navigation of the main window, shared by view models (UI thread only).</summary>
public sealed class AppNavigator
{
    public AppPage Current { get; private set; } = AppPage.Home;

    public event Action<AppPage, AppPage>? Navigated;

    public void Navigate(AppPage page)
    {
        if (page == Current)
        {
            return;
        }
        var previous = Current;
        Current = page;
        Navigated?.Invoke(previous, page);
    }
}

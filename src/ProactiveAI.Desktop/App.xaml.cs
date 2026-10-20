using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Desktop.Interop;
using ProactiveAI.Desktop.Services;
using ProactiveAI.Desktop.ViewModels;
using ProactiveAI.Infrastructure;
using ProactiveAI.Infrastructure.Audio;

namespace ProactiveAI.Desktop;

public partial class App : Application
{
    private const int MenuOpen = 1, MenuStart = 2, MenuPause = 3, MenuResume = 4, MenuStop = 5, MenuExit = 9;

    private ServiceProvider? _services;
    private MainWindow? _main;
    private OverlayWindow? _overlay;
    private TrayIconService? _tray;
    private UiDispatcher? _ui;
    private ILogger? _log;
    private int _exiting;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            _log?.LogError("Unhandled UI exception: {Type} 0x{HResult:X8}", e.Exception.GetType().Name, e.Exception.HResult);
            e.Handled = true;
        };
    }

    public static new App? Current => Application.Current as App;

    public T GetService<T>() where T : notnull => _services!.GetRequiredService<T>();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = new UiDispatcher(DispatcherQueue.GetForCurrentThread());
        var services = new ServiceCollection();
        services.AddProactiveAI();
        services.AddSingleton(_ui);
        services.AddSingleton<AppNavigator>();
        services.AddSingleton<LiveSessionViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SessionDetailViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<ResumeViewModel>();
        _services = services.BuildServiceProvider();
        _log = GetService<ILoggerFactory>().CreateLogger("App");
        _log.LogInformation("Application started (version {Version})", typeof(App).Assembly.GetName().Version);

        SpeakerPalette.HighContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        var session = GetService<SessionCoordinator>();
        var settingsVm = GetService<SettingsViewModel>();
        GetService<ResumeViewModel>(); // applies the last chosen résumé before any session starts (also from the tray)

        _main = new MainWindow();
        _main.AppWindow.Closing += (sender, e) =>
        {
            if (Volatile.Read(ref _exiting) == 1)
            {
                return;
            }
            e.Cancel = true;
            if (session.Settings.CloseToTray)
            {
                _main.AppWindow.Hide(); // session continues; the tray shows its state
            }
            else
            {
                _ = ExitAsync();
            }
        };

        _overlay = new OverlayWindow(GetService<LiveSessionViewModel>(), settingsVm, session.Settings);
        // Hide the overlay while the main window (which shows the same cards) is in the foreground. Deactivation is
        // re-checked after a short delay so that the app's own popups (menus, dropdowns) do not make it flash.
        var foregroundCheck = DispatcherQueue.GetForCurrentThread().CreateTimer();
        foregroundCheck.IsRepeating = false;
        foregroundCheck.Interval = TimeSpan.FromMilliseconds(200);
        foregroundCheck.Tick += (_, _) => _overlay?.SetMainWindowForeground(_main is not null && Win32.IsForegroundOwner(_main.Hwnd));
        _main.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                foregroundCheck.Start();
            }
            else
            {
                foregroundCheck.Stop();
                _overlay?.SetMainWindowForeground(true);
            }
        };
        _tray = new TrayIconService(_main.Hwnd)
        {
            MenuProvider = BuildTrayMenu,
        };
        _tray.Activated += ShowMainWindow;
        _tray.MenuItemSelected += OnTrayMenu;
        _tray.Suspending += () =>
        {
            _log?.LogInformation("System suspend: pausing session");
            session.OnSystemSuspendAsync().Wait(TimeSpan.FromSeconds(2));
        };

        settingsVm.Applied += s =>
        {
            ApplyTheme(s.Theme);
            _overlay?.ApplySettings(s);
        };
        session.StatusChanged += snap => _ui.Post(() => UpdateShellState(snap));
        ApplyTheme(session.Settings.Theme);
        UpdateShellState(session.Snapshot);
        _main.Activate();
    }

    public void OnRedirectedActivation() => _ui?.Post(ShowMainWindow);

    /// <summary>Root for dialogs opened by views that may currently be hidden.</summary>
    public XamlRoot? MainXamlRoot => _main?.Content?.XamlRoot;

    /// <summary>Owner window for file pickers.</summary>
    public nint MainWindowHandle => _main?.Hwnd ?? 0;

    /// <summary>Owner window id for Windows App SDK pickers.</summary>
    public Microsoft.UI.WindowId MainWindowId => _main?.AppWindow.Id ?? default;

    public void ShowMainWindow()
    {
        if (_main is null)
        {
            return;
        }
        _main.AppWindow.Show();
        _main.Activate();
    }

    public async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1)
        {
            return;
        }
        _log?.LogInformation("Exiting");
        try
        {
            GetService<SetupViewModel>().Deactivate(); // releases the device-check captures
            var session = GetService<SessionCoordinator>();
            await Task.Run(async () => await session.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Shutdown did not complete cleanly: {Type}", ex.GetType().Name);
        }
        _tray?.Dispose();
        _tray = null;
        _overlay?.CloseForExit();
        GetService<WindowsAudioDeviceCatalog>().Dispose();
        await _services!.DisposeAsync();
        _main?.Close();
        Exit();
    }

    private void ApplyTheme(ThemePreference preference)
    {
        var theme = preference switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _main?.ApplyTheme(theme);
        _overlay?.ApplyTheme(theme);
    }

    private void UpdateShellState(SessionSnapshot snap)
    {
        string state = snap.State switch
        {
            SessionState.Listening => "Listening",
            SessionState.Paused => "Paused",
            SessionState.Reconnecting => "Reconnecting",
            SessionState.Starting => "Starting",
            SessionState.Stopping => "Stopping",
            SessionState.Error => "Error",
            _ => "Stopped",
        };
        var sources = new List<string>();
        if (snap.System.Active)
        {
            sources.Add("system audio");
        }
        if (snap.Microphone.Active)
        {
            sources.Add("microphone");
        }
        string detail = sources.Count > 0 ? $" ({string.Join(" + ", sources)})" : string.Empty;
        uint color = snap.State switch
        {
            SessionState.Listening => 0xFF16A34A,
            SessionState.Paused or SessionState.Reconnecting => 0xFFD97706,
            SessionState.Error => 0xFFDC2626,
            SessionState.Starting or SessionState.Stopping => 0xFF2563EB,
            _ => 0xFF6B7280,
        };
        _tray?.Update($"Meeting Copilot — {state}{detail}", color);
        _main?.SetTitleState(state == "Stopped" ? string.Empty : $"· {state}{detail}");
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        var state = GetService<SessionCoordinator>().State;
        var items = new List<TrayMenuItem> { new(MenuOpen, "Open Meeting Copilot"), new(-1, string.Empty) };
        switch (state)
        {
            case SessionState.Listening or SessionState.Reconnecting:
                items.Add(new(MenuPause, "Pause"));
                break;
            case SessionState.Paused:
                items.Add(new(MenuResume, "Resume"));
                break;
            default:
                // Starting is done on the session page (Home > New session > Continue > Start); the tray can start it
                // only while that page is waiting for Start.
                items.Add(new(MenuStart, "Start listening", GetService<LiveSessionViewModel>().CanStart));
                break;
        }
        items.Add(new(MenuStop, "Stop", state is not SessionState.Stopped));
        items.Add(new(-1, string.Empty));
        items.Add(new(MenuExit, "Exit"));
        return items;
    }

    private void OnTrayMenu(int id)
    {
        var vm = GetService<LiveSessionViewModel>();
        switch (id)
        {
            case MenuOpen:
                ShowMainWindow();
                break;
            case MenuStart:
                ShowMainWindow();
                if (vm.CanStart)
                {
                    vm.StartCommand.Execute(null);
                }
                break;
            case MenuPause:
                vm.PauseCommand.Execute(null);
                break;
            case MenuResume:
                vm.ResumeCommand.Execute(null);
                break;
            case MenuStop:
                vm.StopCommand.Execute(null);
                break;
            case MenuExit:
                _ = ExitAsync();
                break;
        }
    }
}

using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Desktop.Interop;
using ProactiveAI.Desktop.ViewModels;
using Windows.Graphics;
using WinRT.Interop;

namespace ProactiveAI.Desktop;

/// <summary>
/// Floating overlay. Automatic appearance, updates and dismissal never activate the window or take
/// keyboard focus: it is shown with AppWindow.Show(false) and carries WS_EX_NOACTIVATE.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    private readonly LiveSessionViewModel _vm;
    private readonly SettingsViewModel _settingsVm;
    private readonly IntPtr _hwnd;
    private readonly Win32.SubclassProc _proc;
    private readonly DispatcherQueueTimer _insightTimer;
    private readonly DispatcherQueueTimer _saveTimer;
    private AppSettings _settings;
    private CardViewModel? _insight;
    private string? _hiddenForCardId;
    private bool _pointerInside;
    private bool _programmaticMove;
    private bool _positioned;
    private bool _closing;
    private bool _mainInForeground;

    public OverlayWindow(LiveSessionViewModel vm, SettingsViewModel settingsVm, AppSettings settings)
    {
        _vm = vm;
        _settingsVm = settingsVm;
        _settings = settings;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        AnswerView.Host = vm;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        var presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = true;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.PreferredMinimumWidth = 300;
        presenter.PreferredMinimumHeight = 140;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        Win32.AddExtendedStyle(_hwnd, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW, Win32.WS_EX_APPWINDOW);
        _proc = WndProc;
        Win32.SetWindowSubclass(_hwnd, _proc, (UIntPtr)0x4F56, UIntPtr.Zero);

        if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        else
        {
            Root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        }

        var queue = DispatcherQueue.GetForCurrentThread();
        _insightTimer = queue.CreateTimer();
        _insightTimer.IsRepeating = false;
        _insightTimer.Tick += (_, _) => InsightExpired();
        _saveTimer = queue.CreateTimer();
        _saveTimer.IsRepeating = false;
        _saveTimer.Interval = TimeSpan.FromMilliseconds(800);
        _saveTimer.Tick += (_, _) => SaveBounds();

        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += (_, e) =>
        {
            if (!_closing)
            {
                e.Cancel = true;
                AppWindow.Hide();
            }
        };
        vm.PropertyChanged += Vm_PropertyChanged;
        vm.InsightArrived += OnInsightArrived;
        ApplySettings(settings);
    }

    public void ApplySettings(AppSettings settings)
    {
        bool positionReset = settings.OverlayX is null && _settings.OverlayX is not null || settings.OverlayPlacement != _settings.OverlayPlacement;
        _settings = settings;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = settings.OverlayTopmost;
        }
        if (positionReset)
        {
            _positioned = false;
            if (AppWindow.IsVisible)
            {
                Position();
            }
        }
        Refresh();
    }

    public void ApplyTheme(ElementTheme theme)
    {
        Root.RequestedTheme = theme;
    }

    /// <summary>
    /// The main window already shows the same cards: while it is the foreground window the overlay stays hidden and
    /// reappears (without taking focus) when the user switches to another application or minimizes the main window.
    /// </summary>
    public void SetMainWindowForeground(bool foreground)
    {
        if (_mainInForeground == foreground)
        {
            return;
        }
        _mainInForeground = foreground;
        Refresh();
    }

    public void CloseForExit()
    {
        _closing = true;
        _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm.InsightArrived -= OnInsightArrived;
        Win32.RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)0x4F56);
        Close();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LiveSessionViewModel.FocusedCard) or nameof(LiveSessionViewModel.State)
            or nameof(LiveSessionViewModel.NewerAnswerAvailable) or nameof(LiveSessionViewModel.ScenarioIndex)
            or nameof(LiveSessionViewModel.StateText))
        {
            Refresh();
        }
    }

    private void OnInsightArrived(CardViewModel card)
    {
        _insight = card;
        _hiddenForCardId = null;
        _insightTimer.Interval = TimeSpan.FromSeconds(_settings.InsightDisplaySeconds);
        _insightTimer.Start();
        Refresh();
    }

    private void InsightExpired()
    {
        if (_pointerInside)
        {
            _insightTimer.Start(); // keep while the user is reading it
            return;
        }
        _insight = null;
        Refresh();
    }

    private void Refresh()
    {
        bool sessionVisible = _vm.State is SessionState.Listening or SessionState.Paused or SessionState.Reconnecting or SessionState.Starting;
        var answer = _vm.FocusedCard;
        bool showAnswer = answer is not null && !answer.IsDismissed && (answer.IsAnswerLike) && (_vm.IsAnswerMode || answer.Model.Kind == Core.Assistant.CardKind.Answer);
        bool showInsight = !_vm.IsAnswerMode && _insight is not null && !_insight.IsDismissed;
        string? currentId = showInsight ? _insight!.Id : showAnswer ? answer!.Id : null;
        if (currentId is not null && _hiddenForCardId == currentId)
        {
            showAnswer = showInsight = false;
        }

        HeaderText.Text = $"Proactive AI · {_vm.StateText}";
        NewerButton.Visibility = _vm.NewerAnswerAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (showInsight)
        {
            InsightView.Visibility = Visibility.Visible;
            AnswerView.Visibility = Visibility.Collapsed;
            InsightTitle.Text = _insight!.Heading;
            InsightBody.Text = _insight.Suggestion ?? string.Empty;
            InsightQuestion.Text = _insight.SuggestedQuestion is { } q ? "Ask: " + q : string.Empty;
            InsightQuestion.Visibility = _insight.SuggestedQuestion is null ? Visibility.Collapsed : Visibility.Visible;
        }
        else if (showAnswer)
        {
            InsightView.Visibility = Visibility.Collapsed;
            AnswerView.Visibility = Visibility.Visible;
            if (!ReferenceEquals(AnswerView.Card, answer))
            {
                AnswerView.Card = answer;
            }
        }

        bool visible = _settings.OverlayEnabled && !_mainInForeground && sessionVisible && (showAnswer || showInsight);
        if (visible && !AppWindow.IsVisible)
        {
            if (!_positioned)
            {
                Position();
            }
            AppWindow.Show(activateWindow: false);
        }
        else if (!visible && AppWindow.IsVisible)
        {
            AppWindow.Hide();
        }
    }

    private void Position()
    {
        double scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }
        int width = (int)(_settings.OverlayWidth * scale);
        int height = (int)(_settings.OverlayHeight * scale);
        RectInt32 rect;
        if (_settings.OverlayX is { } x && _settings.OverlayY is { } y)
        {
            rect = new RectInt32(x, y, width, height);
            // Clamp restored coordinates to a currently connected monitor's work area.
            var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest).WorkArea;
            rect.Width = Math.Min(rect.Width, area.Width);
            rect.Height = Math.Min(rect.Height, area.Height);
            rect.X = Math.Clamp(rect.X, area.X, area.X + area.Width - rect.Width);
            rect.Y = Math.Clamp(rect.Y, area.Y, area.Y + area.Height - rect.Height);
        }
        else
        {
            var area = DisplayArea.Primary.WorkArea;
            int margin = (int)(24 * scale);
            width = Math.Min(width, area.Width - 2 * margin);
            height = Math.Min(height, area.Height - 2 * margin);
            bool right = _settings.OverlayPlacement is OverlayPlacement.TopRight or OverlayPlacement.BottomRight;
            bool top = _settings.OverlayPlacement is OverlayPlacement.TopRight or OverlayPlacement.TopLeft;
            rect = new RectInt32(
                right ? area.X + area.Width - width - margin : area.X + margin,
                top ? area.Y + margin : area.Y + area.Height - height - margin,
                width,
                height);
        }
        _programmaticMove = true;
        AppWindow.MoveAndResize(rect);
        _programmaticMove = false;
        _positioned = true;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_programmaticMove || !AppWindow.IsVisible)
        {
            return;
        }
        if (args.DidPositionChange || args.DidSizeChange)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    private void SaveBounds()
    {
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        double scale = Math.Max(0.5, Win32.GetDpiForWindow(_hwnd) / 96.0);
        int w = (int)(size.Width / scale), h = (int)(size.Height / scale);
        if (_settings.OverlayX == pos.X && _settings.OverlayY == pos.Y && _settings.OverlayWidth == w && _settings.OverlayHeight == h)
        {
            return;
        }
        _settingsVm.Save(s => s with { OverlayX = pos.X, OverlayY = pos.Y, OverlayWidth = w, OverlayHeight = h });
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == Win32.WM_MOUSEACTIVATE)
        {
            return (IntPtr)Win32.MA_NOACTIVATE; // clicks never steal focus from the app the user is typing in
        }
        return Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void Root_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => _pointerInside = true;

    private void Root_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => _pointerInside = false;

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        _hiddenForCardId = InsightView.Visibility == Visibility.Visible ? _insight?.Id : _vm.FocusedCard?.Id;
        AppWindow.Hide();
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e) => App.Current?.ShowMainWindow();

    private void Newer_Click(object sender, RoutedEventArgs e) => _vm.ShowNewestCommand.Execute(null);

    private void InsightCopy_Click(object sender, RoutedEventArgs e) => _vm.CopyCommand.Execute(_insight);

    private void InsightDismiss_Click(object sender, RoutedEventArgs e)
    {
        _vm.DismissCommand.Execute(_insight);
        _insight = null;
        Refresh();
    }
}

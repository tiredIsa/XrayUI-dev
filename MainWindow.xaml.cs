using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;
using WinUIEx.Messaging;
using XrayUI.Helpers;
using XrayUI.Services;

namespace XrayUI
{
    public sealed partial class MainWindow
    {
        private readonly FrameworkElement _rootElement;
        private readonly Border _miniDragRegion;
        private readonly Button _miniExpandButton;
        private readonly WindowMessageMonitor _windowMessageMonitor;
        private TrayIconService? _trayIcon;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _trayRetryTimer;
        private Views.TrayMenuHost? _trayMenuHost;
        private uint _taskbarCreatedMessage;
        // Connection-state icon variants (idle = blue, running = green). Both the tray icon
        // and the taskbar/window icon swap between them; see ApplyConnectionIcon.
        private string? _idleIconPath;
        private string? _runningIconPath;
        // Load a new HICON only when the connection state changes.
        private bool _trayShowsRunning;
        private bool _isSessionEnding;
        private bool _allowClose;
        private bool _initialized;
        private bool _isHiddenToTray;
        // Set when mini mode was entered from a maximized window, so expanding
        // returns to maximized instead of the plain windowed size.
        private bool _restoreMaximizedOnExpand;
        private bool _personalizeRealized;
        private bool _trayConfigured;
        private readonly bool _startMinimized;
        // Set when we parked the window off-screen at startup; cleared after
        // we re-center it on the first user-initiated show (tray click).
        private bool _needsCenterOnFirstShow;

        private const uint WmQueryEndSession = 0x0011;
        private const uint WmEndSession = 0x0016;
        private const uint WmHotkey = 0x0312;
        private const uint WmNclButtonDown   = 0x00A1;
        private const uint WmNclButtonDblClk = 0x00A3;
        private const int HtCaption = 0x0002;
        private const int FullWindowWidth = 950;
        private const int FullWindowHeight = 600;
        private const int TrafficMinHeight = 650;
        private const int FullModeMinWidth = 430;
        private const int FullModeMinHeight = 260;
        private const int MiniWindowWidth = 330;
        private const int MiniWindowHeight = 136;
        // Shell version 4 reports the icon id in HIWORD(lParam), so keep it 16-bit.
        private const uint TrayIconId = 0x5852;

        public MainViewModel ViewModel { get; }

        public MainWindow(bool startMinimized = false)
        {
            _startMinimized           = startMinimized;
            _needsCenterOnFirstShow   = startMinimized;

            // Build services before InitializeComponent so ViewModel is ready for x:Bind
            var settingsService = new SettingsService();
            var xrayService     = new XrayService();
            var tunService      = new TunService();
            var startupService  = new StartupService();
            var dialogService   = new DialogService(() => _initialized ? Content?.XamlRoot : null);
            var updateService   = new UpdateService();

            ViewModel = new MainViewModel(dialogService, settingsService, xrayService, tunService, startupService, updateService);

            InitializeComponent();

            LocalizationBindings.Bind(this, "Title", () => Title = L.MainWindow_Title);
            XrayUI.Helpers.LocalizationBindings.Bind(DockButton, "ToolTipService.SetToolTip", () => ToolTipService.SetToolTip(DockButton, L.MainWindow_ToggleMini));
            XrayUI.Helpers.LocalizationBindings.Bind(MiniExpandButton, "ToolTipService.SetToolTip", () => ToolTipService.SetToolTip(MiniExpandButton, L.MainWindow_ExpandFull));
            XrayUI.Helpers.LocalizationBindings.Bind(MinicloseButton, "ToolTipService.SetToolTip", () => ToolTipService.SetToolTip(MinicloseButton, L.MainWindow_Close));

            // Initial size and per-mode min-size constraints are established by
            // ApplyWindowMode(isMini: false) below. Get attaches WinUIEx window
            // management to this window for its side effects (min-size clamping,
            // placement); we don't keep the reference — the tray icon is owned
            // directly via _trayIcon so we can drive its tooltip from connection state.
            WindowManager.Get(this);
            _windowMessageMonitor = new WindowMessageMonitor(this);
            _windowMessageMonitor.WindowMessageReceived += OnWindowMessageReceived;
            GlobalHotkeyStore.HotkeysChanged += OnGlobalHotkeysChanged;

            _rootElement = (FrameworkElement)Content;
            ThemeHelper.RootElement = _rootElement;
            ThemeHelper.MainWindow = this;
            ThemeHelper.ApplyBackdrop("Mica");
            _rootElement.ActualThemeChanged += OnRootElementActualThemeChanged;

            // PersonalizeControl is heavy (5x ColorPicker, Expander, etc.). We
            // realize it lazily into PersonalizeHost on first show — saves the
            // entire subtree from cold-start construction. See OnViewModelPropertyChanged.
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            _miniDragRegion = (Border)_rootElement.FindName("MiniDragRegion");
            _miniExpandButton = (Button)_rootElement.FindName("MiniExpandButton");

            _miniDragRegion.PointerPressed += MiniDragRegion_PointerPressed;
            _miniExpandButton.Click += MiniExpandButton_Click;

            var miniCloseButton = (Button)_rootElement.FindName("MinicloseButton");
            miniCloseButton.Click += (_, _) =>
            {
                if (!HideToTray())
                    ExitApplication();
            };

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            ApplyWindowMode(isMini: false);
            UpdateCaptionButtonColors();

            Activated += (_, _) =>
            {
                try { Loc.RefreshSystemLanguage(); }
                catch (Exception ex) { Debug.WriteLine($"[Localization] System language refresh failed: {ex}"); }
            };
            Activated += OnFirstActivated;
            Closed += OnClosed;
        }

        private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
        {
            Activated -= OnFirstActivated;
            _initialized = true;

            EnsureTrayConfigured();
#if LOCALIZATION_SMOKE_TEST
            // The diagnostic executable must not start user network services.
            if (Environment.GetCommandLineArgs().Contains("--tun-takeover-probe") ||
                Environment.GetCommandLineArgs().Contains("--tray-probe")) return;
#endif

            // For --startup-minimized we only hide the window here so the XamlRoot
            // stays alive for any dialogs raised during InitializeAsync (e.g.
            // auto-connect errors). The full tray transition (ReleaseUiResources)
            // runs after init so resources are still freed in the minimized case.
            if (_startMinimized)
            {
                AppWindow.IsShownInSwitchers = false;
                AppWindow.Hide();
            }

            await ViewModel.InitializeAsync(isBootLaunch: _startMinimized);
            RegisterGlobalHotkeys();

            if (_startMinimized && !HideToTray())
            {
                RestoreFromTray();
            }
        }

        private void AppTitleBar_BackRequested(object sender, RoutedEventArgs e)
        {
            if (ViewModel.GoBackCommand.CanExecute(null))
            {
                ViewModel.GoBackCommand.Execute(null);
            }
        }

        private void DockButton_Click(object sender, RoutedEventArgs e)
        {
            SetMiniMode(!ViewModel.IsMiniMode);
        }

        private void ConfigureTray()
        {
            var iconsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "icons");
            _idleIconPath = Path.Combine(iconsDir, "output.ico");
            var runningPath = Path.Combine(iconsDir, "running.ico");
            // Fall back to the idle icon if the running variant is missing, so a bad deploy
            // degrades to "icon never changes" rather than no icon at all.
            _runningIconPath = File.Exists(runningPath) ? runningPath : _idleIconPath;

            _trayShowsRunning = ViewModel.TrayShowsRunning;
            var iconPath = _trayShowsRunning ? _runningIconPath : _idleIconPath;
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            _taskbarCreatedMessage = TrayIconInterop.RegisterWindowMessage("TaskbarCreated");
            if (_taskbarCreatedMessage != 0)
            {
                // TUN launches can be elevated; allow Explorer's lower-integrity broadcast.
                TrayIconInterop.ChangeWindowMessageFilterEx(this.GetWindowHandle(), _taskbarCreatedMessage, 1, IntPtr.Zero);
            }
            _trayRetryTimer = DispatcherQueue.CreateTimer();
            _trayRetryTimer.Interval = TimeSpan.FromSeconds(5);
            _trayRetryTimer.Tick += (_, _) => EnsureTrayConfigured();
            _trayRetryTimer.Start();

            AppWindow.Closing += (_, args) =>
            {
                if (_allowClose || _isSessionEnding)
                {
                    return;
                }

                if (HideToTray())
                {
                    args.Cancel = true;
                }
            };
        }

        internal void EnsureTrayConfigured()
        {
            if (_allowClose || _isSessionEnding) return;
            try
            {
                if (!_trayConfigured)
                {
                    ConfigureTray();
                    _trayConfigured = true;
                }
                if (_trayIcon is null)
                {
                    var path = ViewModel.TrayShowsRunning ? _runningIconPath : _idleIconPath;
                    if (path is null || !File.Exists(path)) return;
                    _trayIcon = new TrayIconService(this.GetWindowHandle(), TrayIconId, path, ViewModel.TrayTooltip);
                }
                _trayIcon.EnsureRegistered();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tray] Registration failed; will retry: {ex.Message}");
            }
        }

        // Swap both the tray icon and the taskbar/window icon to reflect connection state.
        // Called on the UI thread from the TrayTooltip PropertyChanged handler. SetIcon updates
        // the existing tray icon in place (NIM_MODIFY) — no dispose/recreate needed.
        private void ApplyConnectionIcon(bool running)
        {
            var path = running ? _runningIconPath : _idleIconPath;
            if (path is null || !File.Exists(path)) return;
            try
            {
                AppWindow.SetIcon(path);
                _trayIcon?.SetIcon(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tray] Icon update failed: {ex.Message}");
            }
        }

        private MenuFlyout BuildTrayContextMenu()
        {
            var flyout = new MenuFlyout();

            var openItem = XrayUI.Helpers.LocalizationBindings.BindValue(new MenuFlyoutItem { Text = L.Tray_Open }, "Text", localized => localized.Text = L.Tray_Open);
            openItem.Click += (_, _) => RestoreFromTray();
            flyout.Items.Add(openItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var exitItem = XrayUI.Helpers.LocalizationBindings.BindValue(new MenuFlyoutItem { Text = L.Tray_Exit }, "Text", localized => localized.Text = L.Tray_Exit);
            exitItem.Click += (_, _) => ExitApplication();
            flyout.Items.Add(exitItem);

            return flyout;
        }

        private bool HideToTray()
        {
            EnsureTrayConfigured();
            if (_trayIcon is null || !_trayIcon.EnsureRegistered())
            {
                Debug.WriteLine("[Tray] Hide requested but tray icon is unavailable.");
                return false;
            }

            if (_isHiddenToTray) return true;

            _isHiddenToTray = true;
            ControlPanel?.CloseLogWindow();
            ControlPanel?.CloseCustomRulesWindow();

            AppWindow.IsShownInSwitchers = false;
            AppWindow.Hide();

            // Collapse the root content so the compositor drops cached materials
            // for the visual tree while we're in the tray. Object graph stays
            // intact; restoring is one synchronous frame.
            _rootElement.Visibility = Visibility.Collapsed;

            ReleaseUiResources();
            return true;
        }

        internal void RestoreFromTray()
        {
            EnsureTrayConfigured();
            _isHiddenToTray = false;
            AppWindow.IsShownInSwitchers = true;
            _rootElement.Visibility = Visibility.Visible;

            if (_needsCenterOnFirstShow)
            {
                _needsCenterOnFirstShow = false;
                CenterOnPrimaryDisplay();
            }

            // Activate() does not reliably unhide an AppWindow that was parked with
            // AppWindow.Hide() during a minimized startup. Show first so a missing
            // tray icon cannot leave the boot-launched process stranded off-screen.
            AppWindow.Show();
            Activate();
        }

        private void HandleHotkeyMessage(int id)
        {
            if (id == GlobalHotkeyStore.ToggleId)
            {
                var cmd = ViewModel.ControlPanel.StartStopCommand;
                if (cmd.CanExecute(null))
                    _ = cmd.ExecuteAsync(null);
            }
            else if (id == GlobalHotkeyStore.RestoreId)
            {
                RestoreFromTray();
            }
        }

        private void OnGlobalHotkeysChanged(object? sender, EventArgs e) => RegisterGlobalHotkeys();

        /// <summary>
        /// Re-register after an elevation/process handoff. The outgoing process can still own
        /// the configured combinations when this window performs its normal startup registration;
        /// once App confirms that process has exited, retry on this window's UI thread.
        /// </summary>
        internal void RegisterGlobalHotkeysAfterProcessTakeover()
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                RegisterGlobalHotkeys();
                return;
            }

            if (!DispatcherQueue.TryEnqueue(RegisterGlobalHotkeys))
                Debug.WriteLine("[Hotkey] Failed to enqueue post-takeover hotkey registration.");
        }

        // Idempotent: always unregisters both ids first, then re-registers whichever have a
        // combo assigned (no separate enabled flag — presence of a combo means active). Safe to
        // call at startup and any time the Personalize page commits a hotkey change.
        private void RegisterGlobalHotkeys()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            foreach (var id in new[] { GlobalHotkeyStore.ToggleId, GlobalHotkeyStore.RestoreId })
            {
                HotkeyInterop.UnregisterHotKey(hWnd, id);

                // On failure, the combo is left in the store as-is (not cleared) — a conflict may
                // be temporary (another app releases it, or the system state changes), and the
                // next call to this method retries with the same combo. Nothing else reads a
                // "successfully registered" flag: the UI only reflects whether a combo is assigned,
                // and WM_HOTKEY simply never arrives for an id Windows didn't actually register.
                var (mods, vk) = GlobalHotkeyStore.GetCombo(id);
                if (vk != 0)
                    RegisterHotkeyOrLog(hWnd, id, mods, vk);
            }
        }

        private static void RegisterHotkeyOrLog(IntPtr hWnd, int id, uint modifiers, uint virtualKey)
        {
            if (HotkeyInterop.RegisterHotKey(hWnd, id, modifiers | GlobalHotkeyStore.ModNoRepeat, virtualKey)) return;

            Debug.WriteLine($"[Hotkey] Failed to register hotkey id={id} ({modifiers}:{virtualKey}). LastWin32Error={Marshal.GetLastWin32Error()}");
        }

        private void CenterOnPrimaryDisplay()
        {
            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            var size = AppWindow.Size;
            var x = workArea.X + (workArea.Width  - size.Width)  / 2;
            var y = workArea.Y + (workArea.Height - size.Height) / 2;
            AppWindow.Move(new PointInt32(x, y));
        }

        private void ExitApplication()
        {
            if (_allowClose)
            {
                return;
            }

            _allowClose = true;
            _isHiddenToTray = false;
            try
            {
                DisposeTray();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tray] Failed to remove tray icon during exit: {ex.Message}");
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(100);

                try
                {
                    if (Application.Current is App app)
                    {
                        app.RequestShutdown();
                        return;
                    }

                    SystemProxyService.ClearProxy();
                    StopBackgroundServicesOnExit();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Tray] RequestShutdown failed: {ex}");
                }

                Environment.Exit(0);
            });
        }




        private void OnRootElementActualThemeChanged(FrameworkElement sender, object args)
        {
            UpdateCaptionButtonColors();
        }

        private void SetMiniMode(bool isMini)
        {
            if (ViewModel.IsMiniMode == isMini) return;

            var incoming = isMini ? MiniModePanel : FullModePanel;

            // Snap to the new mode immediately so the click feels as instant as it
            // did before — no blocking fade-out. The only animation is a quick
            // entrance on the new content: stage it hidden so it doesn't flash when
            // its bound Visibility flips, switch modes + resize the HWND, then let
            // the new panel scale/fade in as a non-blocking flourish.
            WindowModeTransition.PrepareHidden(incoming);
            ViewModel.IsMiniMode = isMini;
            ApplyWindowMode(isMini);
            WindowModeTransition.FadeIn(incoming);
        }

        private void ApplyWindowMode(bool isMini)
        {
            var presenter = (OverlappedPresenter)AppWindow.Presenter;

            // Un-maximize first: resizing alone keeps the zoomed flag set, and
            // dragging a zoomed window restores it back to full-mode size.
            if (isMini)
            {
                _restoreMaximizedOnExpand = presenter.State == OverlappedPresenterState.Maximized;
                if (_restoreMaximizedOnExpand)
                    presenter.Restore();
            }

            var width  = isMini ? MiniWindowWidth  : FullWindowWidth;
            var height = isMini ? MiniWindowHeight : ViewModel.TrafficVisibility == Visibility.Visible ? TrafficMinHeight : FullWindowHeight;

            // The WinUIEx min-size clamp (WM_GETMINMAXINFO) applies to SetWindowSize
            // too, so the full-mode minimum must be relaxed before shrinking to mini.
            var windowManager = WindowManager.Get(this);
            windowManager.MinWidth  = isMini ? MiniWindowWidth  : FullModeMinWidth;
            windowManager.MinHeight = isMini ? MiniWindowHeight : ViewModel.TrafficVisibility == Visibility.Visible ? TrafficMinHeight : FullModeMinHeight;

            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: !isMini);
            presenter.IsResizable = !isMini;
            presenter.IsMaximizable = !isMini;
            AppTitleBar.Visibility = isMini ? Visibility.Collapsed : Visibility.Visible;
            this.SetWindowSize(width, height);

            // Maximize only after the normal rect above is in place, so the restore
            // rect the system captures is the windowed size, not the mini rect.
            if (!isMini && _restoreMaximizedOnExpand)
            {
                _restoreMaximizedOnExpand = false;
                presenter.Maximize();
            }
        }

        private void MiniExpandButton_Click(object sender, RoutedEventArgs e)
        {
            SetMiniMode(isMini: false);
        }

        private void MiniDragRegion_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!ViewModel.IsMiniMode) return;

            var currentPoint = e.GetCurrentPoint((UIElement)sender);
            if (!currentPoint.Properties.IsLeftButtonPressed) return;

            e.Handled = true;

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ReleaseCapture();
            SendMessage(hWnd, WmNclButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }

        private void UpdateCaptionButtonColors()
        {
            var tb = AppWindow.TitleBar;
            var isDarkTheme = _rootElement.ActualTheme == ElementTheme.Dark;

            var foregroundColor = isDarkTheme
                ? Colors.White
                : Color.FromArgb(230, 0, 0, 0);
            var inactiveForegroundColor = isDarkTheme
                ? Color.FromArgb(153, 255, 255, 255)
                : Color.FromArgb(138, 0, 0, 0);
            var hoverBackgroundColor = isDarkTheme
                ? Color.FromArgb(30, 255, 255, 255)
                : Color.FromArgb(18, 0, 0, 0);
            var pressedBackgroundColor = isDarkTheme
                ? Color.FromArgb(60, 255, 255, 255)
                : Color.FromArgb(36, 0, 0, 0);

            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonHoverBackgroundColor = hoverBackgroundColor;
            tb.ButtonPressedBackgroundColor = pressedBackgroundColor;
            tb.ButtonForegroundColor = foregroundColor;
            tb.ButtonInactiveForegroundColor = inactiveForegroundColor;
            tb.ButtonHoverForegroundColor = foregroundColor;
            tb.ButtonPressedForegroundColor = foregroundColor;
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            _allowClose = true;
            DisposeTray();
            ViewModel.StopSubscriptionRefreshScheduler();
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _rootElement.ActualThemeChanged -= OnRootElementActualThemeChanged;
            _windowMessageMonitor.WindowMessageReceived -= OnWindowMessageReceived;
            _windowMessageMonitor.Dispose();
            GlobalHotkeyStore.HotkeysChanged -= OnGlobalHotkeysChanged;
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            HotkeyInterop.UnregisterHotKey(hWnd, GlobalHotkeyStore.ToggleId);
            HotkeyInterop.UnregisterHotKey(hWnd, GlobalHotkeyStore.RestoreId);
            AppWindow.IsShownInSwitchers = true;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.TrayTooltip))
            {
                // All Shell updates include NIF_SHOWTIP to retain the standard tooltip.
                var running = ViewModel.TrayShowsRunning;
                if (running != _trayShowsRunning)
                {
                    _trayShowsRunning = running;
                    ApplyConnectionIcon(running);
                }
                if (_trayIcon is not null)
                    _trayIcon.SetTooltip(ViewModel.TrayTooltip);
                return;
            }

            if (e.PropertyName is nameof(MainViewModel.TrafficVisibility) or nameof(MainViewModel.IsMiniMode))
            {
                var trafficVisible = ViewModel.TrafficVisibility == Visibility.Visible && !ViewModel.IsMiniMode;
                WindowManager.Get(this).MinHeight = ViewModel.IsMiniMode ? MiniWindowHeight : trafficVisible ? TrafficMinHeight : FullModeMinHeight;
                if (trafficVisible)
                {
                    if (_rootElement.ActualHeight > 0 && _rootElement.ActualHeight < TrafficMinHeight &&
                        ((OverlappedPresenter)AppWindow.Presenter).State != OverlappedPresenterState.Maximized)
                        this.SetWindowSize(Math.Max(_rootElement.ActualWidth, FullModeMinWidth), TrafficMinHeight);
                    if (TrafficHost.Children.Count == 0)
                        TrafficHost.Children.Add(new Views.TrafficMonitorControl { ViewModel = ViewModel.Traffic });
                }
                else TrafficHost.Children.Clear();
            }

            if (_personalizeRealized) return;
            if (e.PropertyName != nameof(MainViewModel.PersonalizeVisibility)) return;
            if (ViewModel.PersonalizeVisibility != Visibility.Visible) return;

            _personalizeRealized = true;
            // Set ViewModel before adding to the visual tree so that, when the
            // host fires Loading, the UserControl's x:Bind initializers see a
            // non-null ViewModel and bind correctly the first time.
            PersonalizeHost.Children.Add(new Views.PersonalizeControl {
                ViewModel = ViewModel.Personalize,
            });
        }

        private void OnWindowMessageReceived(object? sender, WindowMessageEventArgs e)
        {
            if (_taskbarCreatedMessage != 0 && e.Message.MessageId == _taskbarCreatedMessage)
            {
                if (!_allowClose && !_isSessionEnding)
                {
                    _trayIcon?.OnTaskbarCreated();
                    EnsureTrayConfigured();
                }
                return;
            }
            if (e.Message.MessageId == TrayIconInterop.CallbackMessage)
            {
                if (!_allowClose && _trayIcon is not null &&
                    _trayIcon.TryGetAction(e.Message.WParam, e.Message.LParam, out var contextMenu))
                {
                    // Run outside the native callback: activation and flyouts send window messages.
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_allowClose) return;
                        if (contextMenu)
                        {
                            if (_trayIcon is null || !_trayIcon.TryGetRect(out var rect)) return;
                            _trayMenuHost ??= new Views.TrayMenuHost();
                            _trayMenuHost.Show(BuildTrayContextMenu(), rect, _rootElement.ActualTheme);
                        }
                        else RestoreFromTray();
                    });
                }
                e.Handled = true;
                return;
            }
            if (e.Message.MessageId == WmNclButtonDblClk && ViewModel.IsMiniMode)
            {
                e.Handled = true;
                return;
            }

            if (e.Message.MessageId == WmHotkey)
            {
                HandleHotkeyMessage(unchecked((int)e.Message.WParam));
                e.Handled = true;
                return;
            }

            if (e.Message.MessageId == WmQueryEndSession)
            {
                Debug.WriteLine("[Shutdown] WM_QUERYENDSESSION received");
                PrepareForSessionEnding();
                e.Result = new IntPtr(1);
                e.Handled = true;
                return;
            }

            if (e.Message.MessageId != WmEndSession)
            {
                return;
            }

            if (e.Message.WParam == 0)
            {
                Debug.WriteLine("[Shutdown] WM_ENDSESSION cancelled");
                RestoreAfterSessionEndingCancelled();
                return;
            }

            Debug.WriteLine("[Shutdown] WM_ENDSESSION received");
            PrepareForSessionEnding();

            var cleanupTask = Task.Run(() =>
            {
                try
                {
                    if (Application.Current is App app)
                    {
                        app.HandleSessionEnding();
                    }
                    else
                    {
                        SystemProxyService.ClearProxy();
                        StopBackgroundServicesOnExit(fastShutdown: true);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Shutdown] cleanup error: {ex}");
                }
            });
            cleanupTask.Wait(TimeSpan.FromMilliseconds(600));
            Environment.Exit(0);
        }

        private void PrepareForSessionEnding()
        {
            _isSessionEnding = true;
            _allowClose = true;
            _isHiddenToTray = false;
            _trayRetryTimer?.Stop();
        }

        private void RestoreAfterSessionEndingCancelled()
        {
            _isSessionEnding = false;
            _allowClose = false;
            _trayRetryTimer?.Start();
            EnsureTrayConfigured();
        }

        private void DisposeTray()
        {
            _trayRetryTimer?.Stop();
            _trayRetryTimer = null;
            _trayIcon?.Dispose();
            _trayIcon = null;
            _trayMenuHost?.Dispose();
            _trayMenuHost = null;
        }

#if LOCALIZATION_SMOKE_TEST
        internal async Task VerifyTrayAsync()
        {
            void Report(string step) => File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "localization-smoke.txt"), "\n" + step);
            async Task WaitForIcon()
            {
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (_trayIcon is null || !_trayIcon.TryGetRect(out _))
                {
                    if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("Tray icon is missing from the Shell.");
                    await Task.Delay(100);
                }
            }

            void RemoveShellIcon()
            {
                var data = new TrayIconInterop.IconData
                {
                    Size = (uint)Marshal.SizeOf<TrayIconInterop.IconData>(), Window = this.GetWindowHandle(), Id = TrayIconId
                };
                if (!TrayIconInterop.NotifyIcon(TrayIconInterop.Delete, ref data))
                    throw new InvalidOperationException("Could not remove the diagnostic tray icon.");
            }

            EnsureTrayConfigured();
            await WaitForIcon();
            Report("Registered; hiding window");
            if (!HideToTray()) throw new InvalidOperationException("Cannot hide to a registered tray icon.");
            await WaitForIcon();

            Report("Hidden; recreating taskbar icon");
            RemoveShellIcon();
            TrayIconInterop.SendMessage(this.GetWindowHandle(), _taskbarCreatedMessage, 0, 0);
            await WaitForIcon();

            Report("TaskbarCreated recovered; checking timer");
            // Recovery must also work if Explorer's broadcast was missed.
            RemoveShellIcon();
            await WaitForIcon();

            Report("Timer recovered; changing icon and selecting");
            ApplyConnectionIcon(true);
            await WaitForIcon();
            ApplyConnectionIcon(false);
            _trayIcon!.SetTooltip(ViewModel.TrayTooltip);
            TrayIconInterop.SendMessage(this.GetWindowHandle(), TrayIconInterop.CallbackMessage, 0,
                (nint)((TrayIconId << 16) | 0x0400));
            await Task.Delay(200);
            if (_isHiddenToTray || _rootElement.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Tray selection did not restore the window.");
            if (!HideToTray()) throw new InvalidOperationException("Cannot hide after restoring from tray.");

            Report("Selection restored window; opening context menu");
            TrayIconInterop.SendMessage(this.GetWindowHandle(), TrayIconInterop.CallbackMessage, 0,
                (nint)((TrayIconId << 16) | 0x007b));
            await Task.Delay(200);
            if (_trayMenuHost?.IsOpen != true || !_isHiddenToTray)
                throw new InvalidOperationException("Tray context menu did not open independently of the main window.");
            _trayMenuHost.Hide();
            Report("Context menu opened; disposing");
            DisposeTray();
            var identifier = new TrayIconInterop.IconIdentifier
            {
                Size = (uint)Marshal.SizeOf<TrayIconInterop.IconIdentifier>(), Window = this.GetWindowHandle(), Id = TrayIconId
            };
            if (TrayIconInterop.GetIconRect(ref identifier, out _) == 0)
                throw new InvalidOperationException("Tray icon survived disposal.");
        }
#endif

        private static void ReleaseUiResources()
        {
            try
            {
                // Compact LOH in this single GC pass — subscription/JSON paths
                // routinely allocate >85KB buffers that pin into LOH and never
                // compact under default settings.
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();

                using var process = Process.GetCurrentProcess();
                SetProcessWorkingSetSize(process.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tray] Failed to release UI resources: {ex.Message}");
            }
        }

        public void StopBackgroundServicesOnExit(bool fastShutdown = false)
        {
            ViewModel.StopSubscriptionRefreshScheduler();
            ViewModel.StopTrafficCollection();
            ViewModel.ControlPanel.XrayService.StopForShutdown();
            ViewModel.ControlPanel.CleanupTunOnExit(fastShutdown);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(
            IntPtr process,
            IntPtr minimumWorkingSetSize,
            IntPtr maximumWorkingSetSize);
    }
}

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using WinUIEx;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.Views
{
    public sealed partial class LogWindow
    {
        // UI-update throttle: burst traffic (many lines/sec) collapses into
        // at most 1 re-render per interval instead of one per line.
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

        private static readonly SolidColorBrush RunningBrush =
            new(Windows.UI.Color.FromArgb(255, 34, 197, 94));   // green
        private static readonly SolidColorBrush StoppedBrush =
            new(Windows.UI.Color.FromArgb(255, 156, 163, 175)); // grey

        private readonly XrayService     _xray;
        private readonly SettingsService _settings;
        private readonly Func<Task> _reapplyConfigAsync;
        private readonly DispatcherQueue _queue;
        private readonly DispatcherQueueTimer _flushTimer;

        // Set from background thread when new lines arrive; consumed on UI thread.
        private volatile bool _dirty;
        private int _linesReceivedSinceFlush; // Background-thread increments; UI thread reads + clears.
        private int _prevBufferCount;
        private IReadOnlyList<string> _renderedLines = Array.Empty<string>();

        // Timestamp brush, re-resolved (with a full re-render) when the theme changes.
        private Brush _timestampBrush = null!;

        // ScrollView.ScrollTo has no "keep current offset" sentinel like
        // ChangeView's null — the current offset is passed explicitly, and
        // jumps must disable animation to match the old instant behavior.
        private static readonly ScrollingScrollOptions JumpOptions =
            new(ScrollingAnimationMode.Disabled);

        public LogWindow(
            XrayService xray,
            SettingsService settings,
            Func<Task> reapplyConfigAsync)
        {
            this.InitializeComponent();
            _xray               = xray;
            _settings           = settings;
            _reapplyConfigAsync = reapplyConfigAsync;
            _queue              = DispatcherQueue.GetForCurrentThread();

            this.SetWindowSize(900, 600);
            XrayUI.Helpers.LocalizationBindings.Bind(AppWindow, "Title", () => AppWindow.Title = L.Log_Title);
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            XrayUI.Helpers.LocalizationBindings.Bind(AppTitleBar, "Title", () => AppTitleBar.Title = L.Log_Title);
            ThemeHelper.FollowAppTheme(this, WindowRoot);
            SystemBackdrop = new MicaBackdrop();

			XrayUI.Helpers.LocalizationBindings.Bind(LogPrivacyButton, "ToolTipService.SetToolTip", () => ToolTipService.SetToolTip(LogPrivacyButton, L.Log_PrivacyTooltip));
            XrayUI.Helpers.LocalizationBindings.Bind(MaskAddressSubMenu, "Text", () => MaskAddressSubMenu.Text = L.Log_IpMask);
            XrayUI.Helpers.LocalizationBindings.Bind(MaskOffMenuItem, "Text", () => MaskOffMenuItem.Text = L.Log_MaskOff);
            XrayUI.Helpers.LocalizationBindings.Bind(MaskQuarterMenuItem, "Text", () => MaskQuarterMenuItem.Text = Loc.GetString("Log_MaskQuarter"));
            XrayUI.Helpers.LocalizationBindings.Bind(MaskHalfMenuItem, "Text", () => MaskHalfMenuItem.Text = Loc.GetString("Log_MaskHalf"));
            XrayUI.Helpers.LocalizationBindings.Bind(MaskFullMenuItem, "Text", () => MaskFullMenuItem.Text = Loc.GetString("Log_MaskFull"));
            XrayUI.Helpers.LocalizationBindings.Bind(LogLevelDebugMenuItem, "Text", () => LogLevelDebugMenuItem.Text = Loc.GetString("Log_LevelDebug"));
            XrayUI.Helpers.LocalizationBindings.Bind(LogLevelInfoMenuItem, "Text", () => LogLevelInfoMenuItem.Text = Loc.GetString("Log_LevelInfo"));
            XrayUI.Helpers.LocalizationBindings.Bind(LogLevelWarningMenuItem, "Text", () => LogLevelWarningMenuItem.Text = Loc.GetString("Log_LevelWarning"));
            XrayUI.Helpers.LocalizationBindings.Bind(LogLevelSubMenu, "Text", () => LogLevelSubMenu.Text = L.Log_Level);
            XrayUI.Helpers.LocalizationBindings.Bind(DnsLogSubMenu, "Text", () => DnsLogSubMenu.Text = L.Log_DnsLog);
            XrayUI.Helpers.LocalizationBindings.Bind(DnsLogOffMenuItem, "Text", () => DnsLogOffMenuItem.Text = L.Log_MaskOff);
            XrayUI.Helpers.LocalizationBindings.Bind(DnsLogOnMenuItem, "Text", () => DnsLogOnMenuItem.Text = L.Log_DnsLogOn);
            XrayUI.Helpers.LocalizationBindings.Bind(AutoScrollToggle, "Content", () => AutoScrollToggle.Content = L.Log_AutoScroll);
            XrayUI.Helpers.LocalizationBindings.Bind(CopyButton, "Content", () => CopyButton.Content = L.Log_CopyAll);
            XrayUI.Helpers.LocalizationBindings.Bind(ClearButton, "Content", () => ClearButton.Content = L.Log_Clear);

            _xray.LogReceived     += OnLogReceived;
            _xray.RunningChanged  += OnRunningChanged;
            WindowRoot.ActualThemeChanged += OnActualThemeChanged;

            RefreshTimestampBrush();
            RenderLog();
            UpdateStatus();
            LocalizationBindings.Bind(this, "Status", UpdateStatus, apply: false);
            _ = InitializeLogSettingsMenuAsync();

            _flushTimer = _queue.CreateTimer();
            _flushTimer.Interval = FlushInterval;
            _flushTimer.IsRepeating = true;
            _flushTimer.Tick += OnFlushTick;
            _flushTimer.Start();

            this.Closed += OnClosed;
        }

        // ── Event handlers ─────────────────────────────────────────────────────

        private void OnClosed(object sender, WindowEventArgs args)
        {
            _flushTimer.Stop();
            _xray.LogReceived    -= OnLogReceived;
            _xray.RunningChanged -= OnRunningChanged;
            WindowRoot.ActualThemeChanged -= OnActualThemeChanged;
        }

        private void OnLogReceived(object? sender, string line)
        {
            // Called from background thread. Do NOT touch the UI here —
            // just mark dirty; the timer will re-render on the UI thread.
            Interlocked.Increment(ref _linesReceivedSinceFlush);
            _dirty = true;
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            // Enqueue so TimestampBrushSource's {ThemeResource} lookup has settled
            // before the brush is re-read; existing Runs keep their old brush
            // instance, so a full re-render is required.
            _queue.TryEnqueue(() =>
            {
                RefreshTimestampBrush();
                var offset = LogScrollViewer.VerticalOffset;
                RenderLog(forceRebuild: true);
                // Flush layout so ScrollableHeight reflects the rebuilt content
                // before ScrollTo clamps against it.
                LogScrollViewer.UpdateLayout();
                LogScrollViewer.ScrollTo(
                    LogScrollViewer.HorizontalOffset,
                    AutoScrollToggle.IsChecked == true ? LogScrollViewer.ScrollableHeight : offset,
                    JumpOptions);
            });
        }

        private void OnRunningChanged(object? sender, bool running)
        {
            _queue.TryEnqueue(UpdateStatus);
        }

        private void OnFlushTick(DispatcherQueueTimer sender, object args)
        {
            if (!_dirty) return;
            _dirty = false;

            var autoScroll = AutoScrollToggle.IsChecked == true;
            var prevOffset = LogScrollViewer.VerticalOffset;
            var prevExtent = LogScrollViewer.ExtentHeight;
            var prevCount  = _prevBufferCount;
            var received   = Interlocked.Exchange(ref _linesReceivedSinceFlush, 0);

            RenderLog();
            var newCount = _prevBufferCount; // RenderLog just updated this.

            if (autoScroll)
            {
                // Flush layout first so ScrollableHeight already includes the
                // lines just rendered — ScrollTo clamps at request time, unlike
                // ChangeView's late clamping.
                LogScrollViewer.UpdateLayout();
                LogScrollViewer.ScrollTo(
                    LogScrollViewer.HorizontalOffset,
                    LogScrollViewer.ScrollableHeight,
                    JumpOptions);
            }
            else
            {
                // Ring buffer evicted some lines: (received) − (net buffer growth) = lines pushed out.
                // Shift the scroll offset down by that height so visible content stays anchored.
                var evicted = Math.Max(0, received - (newCount - prevCount));
                if (evicted > 0 && prevCount > 0 && prevExtent > 0)
                {
                    var lineHeight = prevExtent / prevCount;
                    var target = Math.Max(0, prevOffset - evicted * lineHeight);
                    LogScrollViewer.ScrollTo(LogScrollViewer.HorizontalOffset, target, JumpOptions);
                }
            }
        }

        // ── Rendering ──────────────────────────────────────────────────────────

        private void RenderLog(bool forceRebuild = false)
        {
            // XrayService owns the single source of truth; we just render a snapshot.
            var lines = _xray.GetLogBuffer();
            var overlap = forceRebuild ? 0 : FindSuffixPrefixOverlap(_renderedLines, lines);
            var removed = _renderedLines.Count - overlap;

            if (forceRebuild)
            {
                LogTextBlock.Inlines.Clear();
            }
            else
            {
                for (var i = _renderedLines.Count; i > overlap; i--)
                {
                    LogTextBlock.Inlines.RemoveAt(0);
                }

                // Every non-first Span owns its leading line break. If the ring
                // buffer evicted the old first line, promote the retained Span.
                if (removed > 0 && overlap > 0 &&
                    LogTextBlock.Inlines[0] is Span firstLine &&
                    firstLine.Inlines.Count > 0 &&
                    firstLine.Inlines[0] is LineBreak)
                {
                    firstLine.Inlines.RemoveAt(0);
                }
            }

            for (var i = overlap; i < lines.Count; i++)
            {
                LogTextBlock.Inlines.Add(BuildLineSpan(
                    lines[i],
                    prependLineBreak: LogTextBlock.Inlines.Count > 0));
            }

            _renderedLines = lines;
            XrayUI.Helpers.LocalizationBindings.Bind(LineCountText, "Text", () => LineCountText.Text = Loc.Format("Log_Lines", lines.Count));
            _prevBufferCount = lines.Count;
        }

        private static int FindSuffixPrefixOverlap(
            IReadOnlyList<string> previous,
            IReadOnlyList<string> current)
        {
            for (var overlap = Math.Min(previous.Count, current.Count); overlap > 0; overlap--)
            {
                var previousStart = previous.Count - overlap;
                var matches = true;

                for (var i = 0; i < overlap; i++)
                {
                    if (!string.Equals(previous[previousStart + i], current[i], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return overlap;
                }
            }

            return 0;
        }

        private Span BuildLineSpan(string line, bool prependLineBreak)
        {
            var span = new Span();
            var timestampLength = LogLineParser.TimestampLength(line);

            if (prependLineBreak)
            {
                span.Inlines.Add(new LineBreak());
            }

            if (timestampLength > 0)
            {
                span.Inlines.Add(new Run { Text = line[..timestampLength], Foreground = _timestampBrush });
            }

            if (timestampLength < line.Length)
            {
                span.Inlines.Add(new Run { Text = line[timestampLength..] });
            }

            return span;
        }

        private void RefreshTimestampBrush()
        {
            _timestampBrush = TimestampBrushSource.Foreground;
        }

        private async Task InitializeLogSettingsMenuAsync()
        {
            try
            {
                var settings = await _settings.LoadSettingsAsync();
                SetMaskAddressSelection(LogMaskAddress.Normalize(settings.LogMaskAddress));
                SetLogLevelSelection(XrayLogLevel.Normalize(settings.XrayLogLevel));
                SetDnsLogSelection(settings.DnsLog);
            }
            catch
            {
                SetMaskAddressSelection(LogMaskAddress.Off);
                SetLogLevelSelection(XrayLogLevel.Warning);
                SetDnsLogSelection(false);
            }
        }

        private void SetMaskAddressSelection(string value)
        {
            MaskOffMenuItem.IsChecked     = value == LogMaskAddress.Off;
            MaskQuarterMenuItem.IsChecked = value == LogMaskAddress.Quarter;
            MaskHalfMenuItem.IsChecked    = value == LogMaskAddress.Half;
            MaskFullMenuItem.IsChecked    = value == LogMaskAddress.Full;
        }

        private void SetLogLevelSelection(string value)
        {
            LogLevelDebugMenuItem.IsChecked   = value == XrayLogLevel.Debug;
            LogLevelInfoMenuItem.IsChecked    = value == XrayLogLevel.Info;
            LogLevelWarningMenuItem.IsChecked = value == XrayLogLevel.Warning;
        }

        private void SetDnsLogSelection(bool value)
        {
            DnsLogOffMenuItem.IsChecked = !value;
            DnsLogOnMenuItem.IsChecked  = value;
        }

        private void UpdateStatus()
        {
            var running = _xray.IsRunning;
            XrayUI.Helpers.LocalizationBindings.Bind(StatusText, "Text", () => StatusText.Text = running ? L.Log_Running : L.Log_NotRunning);
            StatusDot.Fill  = running ? RunningBrush : StoppedBrush;
        }

        // ── Button handlers ────────────────────────────────────────────────────

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            var dp = new DataPackage();
            dp.SetText(string.Join('\n', _xray.GetLogBuffer()));
            Clipboard.SetContent(dp);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _xray.ClearLogBuffer();
            RenderLog();
        }

        private async void MaskAddressMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item)
            {
                return;
            }

            // The off item carries the sentinel tag "off" (an empty-string Tag round-trips
            // unreliably through XAML); Normalize maps it back to Off ("").
            var value = LogMaskAddress.Normalize(item.Tag as string);
            SetMaskAddressSelection(value);

            await ApplyLogSettingAsync(L.Log_PrivacyTitle, s =>
            {
                if (LogMaskAddress.Normalize(s.LogMaskAddress) == value)
                {
                    return false;
                }

                s.LogMaskAddress = value;
                return true;
            });
        }

        private async void LogLevelMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item)
            {
                return;
            }

            var value = XrayLogLevel.Normalize(item.Tag as string);
            SetLogLevelSelection(value);

            await ApplyLogSettingAsync(L.Log_Level, s =>
            {
                if (XrayLogLevel.Normalize(s.XrayLogLevel) == value)
                {
                    return false;
                }

                s.XrayLogLevel = value;
                return true;
            });
        }

        private async void DnsLogMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item)
            {
                return;
            }

            var value = (item.Tag as string) == "on";
            SetDnsLogSelection(value);

            await ApplyLogSettingAsync(L.Log_DnsLog, s =>
            {
                if (s.DnsLog == value)
                {
                    return false;
                }

                s.DnsLog = value;
                return true;
            });
        }

        /// <summary>
        /// Shared save path for the log-settings flyout: persists the mutation, then hot-reapplies
        /// the config (proxy mode) or tells the user it applies next session (TUN mode).
        /// <paramref name="apply"/> returns false when the stored value already matches.
        /// </summary>
        private async Task ApplyLogSettingAsync(string title, Func<AppSettings, bool> apply)
        {
            try
            {
                var settings = await _settings.LoadSettingsAsync();
                if (!apply(settings))
                {
                    return;
                }

                await _settings.SaveSettingsAsync(settings);

                if (!_xray.IsRunning)
                {
                    return;
                }

                if (settings.IsTunMode)
                {
                    await ShowInfoAsync(title, L.Log_PrivacySaved);
                    return;
                }

                await _reapplyConfigAsync();
            }
            catch (Exception ex)
            {
                await ShowInfoAsync(title, Loc.Format("Log_PrivacyFailed", ex.Message));
            }
        }

        private async Task ShowInfoAsync(string title, string message)
        {
            var dialog = XrayUI.Helpers.LocalizationBindings.BindValue(new ContentDialog {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = ThemeHelper.ActualTheme,
                Title = title,
                Content = message,
                CloseButtonText = L.Dialog_OK
            }, "CloseButtonText", localized => localized.CloseButtonText = L.Dialog_OK);

            await dialog.ShowAsync();
        }
    }
}

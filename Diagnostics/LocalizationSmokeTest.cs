#if LOCALIZATION_SMOKE_TEST
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.Models.Traffic;
using XrayUI.Services;
using XrayUI.Services.Traffic;
using XrayUI.ViewModels;
using XrayUI.Views;

namespace XrayUI.Diagnostics;

/// <summary>Opt-in code-driven test executable. Does not initialize network services or user settings.</summary>
internal static class LocalizationSmokeTest
{
    public static async Task RunAsync()
    {
        var report = Path.Combine(AppContext.BaseDirectory, "localization-smoke.txt");
        var temp = Path.Combine(Path.GetTempPath(), "XrayUI-Locale-" + Guid.NewGuid().ToString("N"));
        Application.Current.UnhandledException += (_, args) =>
        {
            File.WriteAllText(report, "FAIL (XAML): " + args.Exception);
            Environment.Exit(1);
        };
        try
        {
            if (Environment.GetCommandLineArgs().Contains("--tun-takeover-probe"))
            {
                await Task.Delay(200);
                var takeoverWindow = new MainWindow();
                takeoverWindow.ViewModel.ControlPanel.SetTunEnabledSilently(true);
                takeoverWindow.RegisterGlobalHotkeysAfterProcessTakeover();
                takeoverWindow.Activate();
                await Task.Delay(500);
                await File.WriteAllTextAsync(report, "PASS: TUN takeover window activated.");
                Environment.Exit(0);
                return;
            }
            var settings = new SettingsService(temp);
            var window = new Window();
            var host = new Grid();
            window.Content = host;
            var dialogs = new DialogService(() => host.XamlRoot);
            var main = new MainViewModel(dialogs, settings, new XrayService(), new TunService(), new StartupService(), new UpdateService());
            await settings.UpdateSettingsAsync(s => s.Subscriptions = new()
            {
                new SubscriptionEntry { Id = "leading", Name = "Leading group", Url = "https://example.test/leading" },
                new SubscriptionEntry { Id = "fixture", Name = "Test subscription", Url = "https://example.test/sub" },
                new SubscriptionEntry { Id = "empty", Name = "Empty subscription", Url = "https://example.test/empty" }
            });
            await settings.SaveServersAsync(new[]
            {
                new ServerEntry { Name = "London", Host = "example.test", Port = 443, Protocol = "vless", SubscriptionId = "fixture" },
                new ServerEntry { Name = "Manual", Host = "manual.test", Port = 443, Protocol = "vless" }
            }.Concat(Enumerable.Range(0, 40).Select(i => new ServerEntry
            { Name = "Leading " + i, Host = "example.test", Port = 443, Protocol = "vless", SubscriptionId = "leading" }))
             .Concat(Enumerable.Range(0, 60).Select(i => new ServerEntry
            { Name = "Fixture " + i, Host = "example.test", Port = 443, Protocol = "vless", SubscriptionId = "fixture" }))
             .Concat(Enumerable.Range(0, 40).Select(i => new ServerEntry
            { Name = "Manual " + i, Host = "example.test", Port = 443, Protocol = "vless" })));
            await main.ServerList.LoadServersAsync();
            main.Personalize.LoadLanguage(new AppSettings { Language = "ru-RU" });
            main.Personalize.LoadRegion(new AppSettings());
            main.Personalize.SelectedRegionIndex = 1;
            var personalize = new PersonalizeControl { ViewModel = main.Personalize };
            var traffic = new TrafficMonitorControl { ViewModel = main.Traffic };
            var panel = new ControlPanelControl { ViewModel = main.ControlPanel };
            var servers = new ServerListControl { ViewModel = main.ServerList };
            var details = new ServerDetailControl { ViewModel = main.ServerDetail };
            host.Children.Add(personalize); host.Children.Add(traffic);
            host.Children.Add(panel); host.Children.Add(servers); host.Children.Add(details);
            var ready = new TaskCompletionSource();
            host.Loaded += (_, _) => ready.TrySetResult();
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            window.Activate();
            window.AppWindow.Hide();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var group = main.ServerList.Groups.First(g => g.Id == "fixture");
            Check(main.ServerList.Groups.Count() == 4, "Missing group headers, including empty subscription");
            await Task.Delay(100);
            var repeater = (ItemsRepeater)servers.FindName("GroupRepeater");
            var listScroll = (ScrollViewer)servers.FindName("BrowserScroll");
            var headerContainer = (SubscriptionGroupControl)repeater.GetOrCreateElement(main.ServerList.Groups.IndexOf(group));
            var countryRow = group.Rows.First(r => r.Server.Name == "London");
            countryRow.Server.Host = "136.243.0.1";
            await countryRow.EnsureCountryAsync();
            Check(countryRow.CountryCode == "DE", "Local GeoIP lookup did not update the row");
            var countryControl = headerContainer.GetRow(group.Rows.IndexOf(countryRow));
            countryControl.UpdateLayout();
            var countryImage = (Image)countryControl.FindName("CountryFlag");
            Check(countryImage.Visibility == Visibility.Visible, "Country flag is hidden");
            var countrySource = (Microsoft.UI.Xaml.Media.Imaging.SvgImageSource)countryImage.Source;
            var countryOpened = new TaskCompletionSource();
            countrySource.Opened += (_, _) => countryOpened.TrySetResult();
            countrySource.OpenFailed += (_, e) => countryOpened.TrySetException(new InvalidOperationException("Country SVG failed: " + e.Status));
            countrySource.UriSource = null;
            countrySource.UriSource = countryRow.FlagUri;
            await countryOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            headerContainer.UpdateLayout();
            headerContainer.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            await Task.Delay(150);
            listScroll.ChangeView(null, Math.Max(0, listScroll.VerticalOffset - 40), null, true);
            await Task.Delay(150);
            var headerTop = headerContainer.HeaderAnchor.TransformToVisual(listScroll).TransformPoint(new Windows.Foundation.Point()).Y;
            var mutations = 0;
            group.Rows.CollectionChanged += (_, _) => mutations++;
            main.ServerList.Groups.CollectionChanged += (_, _) => mutations++;
            var selectedBeforeCollapse = main.ServerList.SelectedServer;
            double maximumHeaderMovement = 0; int sampledFrames = 0;
            void ObserveHeader(object? sender, object args)
            {
                sampledFrames++;
                var current = headerContainer.HeaderAnchor.TransformToVisual(listScroll).TransformPoint(new Windows.Foundation.Point()).Y;
                maximumHeaderMovement = Math.Max(maximumHeaderMovement, Math.Abs(current - headerTop));
            }
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += ObserveHeader;
            ((Expander)headerContainer.FindName("GroupExpander")).IsExpanded = false;
            Check(!group.IsExpanded, "Native Expander did not update group state");
            await Task.Delay(150);
            repeater.UpdateLayout();
            var collapsedTop = headerContainer.HeaderAnchor.TransformToVisual(listScroll).TransformPoint(new Windows.Foundation.Point()).Y;
            Check(Math.Abs(headerTop - collapsedTop) < 2, $"Collapse moved header: {headerTop} -> {collapsedTop}");
            main.ServerList.ToggleGroup(group);
            await Task.Delay(150);
            repeater.UpdateLayout();
            var expandedTop = headerContainer.HeaderAnchor.TransformToVisual(listScroll).TransformPoint(new Windows.Foundation.Point()).Y;
            Check(Math.Abs(headerTop - expandedTop) < 2, $"Expand moved header: {headerTop} -> {expandedTop}");
            for (var i = 0; i < 6; i++)
            {
                main.ServerList.ToggleGroup(group);
                await Task.Delay(30);
            }
            await Task.Delay(250);
            Check(group.IsExpanded, "Rapid toggles left stale expansion state");
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= ObserveHeader;
            Check(sampledFrames > 0, "No render frames sampled");
            Check(maximumHeaderMovement < 2, "Header moved during animation: " + maximumHeaderMovement);
            main.ServerList.ToggleGroup(group);
            await Task.Delay(150);
            Check(!group.IsExpanded && mutations == 0, "Collapse mutated the source collections");
            Check(ReferenceEquals(selectedBeforeCollapse, main.ServerList.SelectedServer), "Collapse lost selected server");
            main.ServerList.SearchQuery = "London";
            Check(main.ServerList.NavigableRows.Length == 1 && group.IsExpanded, "Search did not reveal group match");
            main.ServerList.SearchQuery = "";
            Check(!group.IsExpanded, "Search overwrote saved collapse state");
            await main.ServerList.FlushGroupStateAsync();
            Check((await settings.LoadSettingsAsync()).CollapsedServerGroups!.Contains("fixture"), "Collapse state was not saved");
            main.ServerList.ToggleGroup(group);
            await main.ServerList.FlushGroupStateAsync();

            var caption = new TextBlock(); Localize.SetText(caption, "Traffic_Title.Text");
            var draft = new TextBox { Text = "unsaved input" }; Localize.SetHeader(draft, "EditServer_Name");
            main.Personalize.ShowGroupInDetails = false;
            main.Traffic.IsPaused = true;
            var stale = await settings.LoadSettingsAsync();
            var importMessage = LocalizedText.Format("Personalize_ImportSuccessMsg", 1, 2, 3,
                LocalizedText.Key("Personalize_ImportAdvancedSuffix"));
            foreach (var (tag, expected) in new[] { ("ru-RU", "Трафик"), ("zh-CN", "流量监控"), ("en-US", "Traffic"), ("ru-RU", "Трафик") })
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await Loc.ChangeAsync(tag, settings);
                CheckLocalizedTree(host);
                Check(caption.Text == expected, $"Caption {tag}: {caption.Text}");
                Check(importMessage.Value.Contains(Loc.GetString("Personalize_ImportAdvancedSuffix")),
                    "Nested import caption retained its previous language");
                Check(draft.Text == "unsaved input", "Input was overwritten");
                Check(!main.Personalize.ShowGroupInDetails, "Draft settings were reset");
                Check(main.Traffic.IsPaused, "Traffic pause was reset");
                Check(main.Personalize.ShowRestartHint, "Pending routing region was lost");
                Check(!main.ControlPanel.IsRunning, "Language change started proxy");
                Check(main.ControlPanel.StartStopButtonContent == Loc.GetString("ControlPanel_Start"), "Stale Start caption");
                var navigationIcon = ((Button)panel.FindName("TrafficButton")).Content;
                // Native AOT can recreate an unnamed Viewbox's collected managed
                // wrapper as FrameworkElement. Verify the actual icon tree rather
                // than requiring one particular managed projection of that tree.
                Check(navigationIcon is FrameworkElement { Width: 16, Height: 16 } icon &&
                    Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(icon) > 0,
                    "Navigation icon content was replaced or removed");
            }
            stale.LocalMixedPort = 17999;
            await settings.SaveSettingsAsync(stale);
            var saved = await settings.LoadSettingsAsync();
            Check(saved.Language == "ru-RU" && saved.LocalMixedPort == 17999, "Stale settings overwrote language");

            var store = new TrafficMonitorStore(new TrafficMonitorOptions(["in"], ["out"]));
            var session = new TrafficCoreSession(Guid.NewGuid(), new Uri("http://127.0.0.1:10085"), DateTimeOffset.UtcNow);
            store.BeginSession(session);
            store.RecordAccess(session.Id, new(DateTimeOffset.UtcNow, null, new("example.test", 443), TrafficTransport.Tcp, TrafficAccessStatus.Accepted, "in", "out", TrafficRouteKind.Proxy));
            var data = new TrafficMonitorViewModel(store);
            data.SelectedRow = data.Rows[0]; data.IsPaused = true;
            var row = data.SelectedRow;
            var dataView = new TrafficMonitorControl { ViewModel = data };
            host.Children.Add(dataView);
            LocalizationBindings.Bind(data, "Presentation", data.RefreshLocalization, false);
            await Loc.ChangeAsync("en-US", settings);
            Check(ReferenceEquals(row, data.SelectedRow) && data.Rows.Count == 1 && data.IsPaused, "Traffic state changed");
            Check(data.SelectedRow.Route == "Proxy", "Traffic row translation failed");

            var dialog = new AddRuleDialog(WinRT.Interop.WindowNative.GetWindowHandle(window)) { XamlRoot = host.XamlRoot };
            var match = (TextBox)dialog.FindName("MatchTextBox");
            match.Text = "keep.example";
            var dialogTask = dialog.ShowAsync().AsTask();
            await Task.Delay(100);
            await Loc.ChangeAsync("ru-RU", settings);
            Check(dialog.Title?.ToString() == "Добавить правило", "Dialog title was not refreshed");
            Check(match.Text == "keep.example", "Dialog draft was overwritten");
            dialog.Hide();
            await dialogTask;
            Check(Loc.GetString("Personalize_Theme.Text") == "Тема", "Catalog property-key lookup failed");
            FindLanguageCombo(personalize)!.SelectedIndex = LanguageHelper.IndexOf("en-US");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!main.Personalize.CanChangeLanguage && DateTime.UtcNow < deadline) await Task.Delay(10);
            Check(Loc.Current.EffectiveTag == "en-US", "Language selection command failed");
            Check((await settings.LoadSettingsAsync()).Language == "en-US", "Language selection was not saved");
            await Loc.ChangeAsync("ru-RU", settings);
            using (var locked = File.Open(Path.Combine(temp, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { await Loc.ChangeAsync("zh-CN", settings); throw new InvalidOperationException("Locked settings should fail"); }
                catch (IOException) { Check(Loc.Current.EffectiveTag == "ru-RU", "Failed save changed active language"); }
            }
            await settings.UpdateSettingsAsync(s => { s.IsTunMode = true; s.LastConnectionWasTun = false; });
            var shutdownSnapshot = await settings.LoadSettingsAsync();
            await settings.UpdateSettingsAsync(s => s.LastConnectionWasTun = true);
            shutdownSnapshot.IsTunMode = false;
            await settings.SaveSettingsAsync(shutdownSnapshot);
            var nextBootSettings = await settings.LoadSettingsAsync();
            Check(nextBootSettings.LastConnectionWasTun == true && !nextBootSettings.IsTunMode,
                "Runtime cleanup overwrote the last successful connection mode");
            var stress = await CheckLargeBrowserAsync(host, dialogs);
            window.Close();
            var releasedElement = CreateDetachedLocalizedElement();
            for (var attempt = 0; attempt < 5 && releasedElement.IsAlive; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await Task.Delay(50);
            }
            Check(!releasedElement.IsAlive, "Localization retained a detached XAML element");
            await File.WriteAllTextAsync(report, "PASS: embedded catalogs with Russian startup override; actual XAML construction/loading; local GeoIP and bundled SVG country flag loaded; en/ru/zh live captions after GC; language ComboBox selection; preserved input/settings/pause/selection; stale settings merge; dialog captions; detached element collection. No proxy started. " + stress);
            File.Delete(Path.Combine(temp, "settings.json"));
            File.Delete(Path.Combine(temp, "servers.json"));
            Directory.Delete(temp);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(report, "FAIL: " + ex);
            Environment.Exit(1);
        }
    }
    private static async Task<string> CheckLargeBrowserAsync(Grid host, DialogService dialogs)
    {
        var path = Path.Combine(Path.GetTempPath(), "XrayUI-Repeater-" + Guid.NewGuid().ToString("N"));
        var settings = new SettingsService(path);
        var subscriptions = Enumerable.Range(0, 50).Select(i => new SubscriptionEntry { Id = "g" + i, Name = "Group " + i }).ToList();
        await settings.UpdateSettingsAsync(s => s.Subscriptions = subscriptions);
        await settings.SaveServersAsync(subscriptions.SelectMany(g => Enumerable.Range(0, 100).Select(i =>
            new ServerEntry { Name = g.Name + " Server " + i, Host = "example.test", Port = 443, Protocol = "vless", SubscriptionId = g.Id })));
        var main = new MainViewModel(dialogs, settings, new XrayService(), new TunService(), new StartupService(), new UpdateService());
        await main.ServerList.LoadServersAsync();
        var browser = new ServerListControl { ViewModel = main.ServerList, Width = 600, Height = 500 };
        host.Children.Clear(); host.Children.Add(browser);
        await Task.Delay(250);
        browser.UpdateLayout();
        var initial = CountVisual<ServerRowControl>(browser);
        Check(initial > 0 && initial < 200, "Production browser failed virtualization: " + initial);
        var target = main.ServerList.Groups[30].Rows[50];
        browser.ViewModel.SelectRow(target);
        browser.BringRowIntoView(target);
        await Task.Delay(250);
        var distant = CountVisual<ServerRowControl>(browser);
        Check(distant > 0 && distant < 200, "Production browser retained distant rows: " + distant);
        var repeater = (ItemsRepeater)browser.FindName("GroupRepeater");
        var groupControl = (SubscriptionGroupControl)repeater.TryGetElement(30);
        var rowControl = groupControl.GetRow(50);
        Check(ReferenceEquals(rowControl.Model, target) && ((ListViewItem)rowControl.FindName("Row")).IsSelected, "Recycled row lost selection binding");
        Check(FindVisual<TextBlock>(rowControl)!.Text == target.Server.Name, "Recycled row shows stale server");
        var group = main.ServerList.Groups[30];
        var next = group.Rows[51];
        main.ServerList.SelectRow(next, control: true);
        Check(target.IsSelected && next.IsSelected && main.ServerList.HasMultipleSelectedServers, "Ctrl selection bridge failed");
        main.ServerList.ToggleGroup(group);
        Check(!target.IsSelected && !next.IsSelected && ReferenceEquals(main.ServerList.SelectedServer, next.Server), "Collapse lost current details or retained mass selection");
        main.ServerList.ToggleGroup(group);
        main.ServerList.SelectRow(target);
        main.ServerList.SelectRow(group.Rows[54], shift: true);
        Check(group.Rows.Skip(50).Take(5).All(r => r.IsSelected), "Shift selection bridge failed");
        await main.ServerList.FlushGroupStateAsync();
        await Task.Delay(100);
        host.Children.Clear();
        File.Delete(Path.Combine(path, "settings.json")); File.Delete(Path.Combine(path, "servers.json")); Directory.Delete(path);
        return $"Production 5000-row browser: realized {initial}/{distant}; recycling, Ctrl/Shift and collapse selection passed.";
    }
    private static int CountVisual<T>(DependencyObject root) where T : DependencyObject
    {
        var count = root is T ? 1 : 0;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            count += CountVisual<T>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
        return count;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void CheckLocalizedTree(DependencyObject element)
    {
        if (element is TextBlock text && Localize.GetText(text) is { } key)
            Check(text.Text == Loc.GetString(key), $"Stale XAML caption {key}: {text.Text}");
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++)
            CheckLocalizedTree(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i));
    }
    private static ComboBox? FindLanguageCombo(DependencyObject element)
    {
        if (element is ComboBox combo && ReferenceEquals(combo.ItemsSource, LanguageHelper.SupportedLanguages)) return combo;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindLanguageCombo(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i)) is { } found) return found;
        return null;
    }
    private static T? FindVisual<T>(DependencyObject element) where T : DependencyObject
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateDetachedLocalizedElement()
    {
        var element = new TextBlock();
        Localize.SetText(element, "ServerList_HeaderText.Text");
        return new WeakReference(element);
    }
}
#endif

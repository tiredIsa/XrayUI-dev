#if LOCALIZATION_SMOKE_TEST
using System;
using System.IO;
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
            var settings = new SettingsService(temp);
            var window = new Window();
            var host = new Grid();
            window.Content = host;
            var dialogs = new DialogService(() => host.XamlRoot);
            var main = new MainViewModel(dialogs, settings, new XrayService(), new TunService(), new StartupService(), new UpdateService());
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
                Check(((Button)panel.FindName("TrafficButton")).Content is Viewbox, "Navigation icon lost");
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
            window.Close();
            var releasedElement = CreateDetachedLocalizedElement();
            for (var attempt = 0; attempt < 5 && releasedElement.IsAlive; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await Task.Delay(50);
            }
            Check(!releasedElement.IsAlive, "Localization retained a detached XAML element");
            await File.WriteAllTextAsync(report, "PASS: embedded catalogs with Russian startup override; actual XAML construction/loading; en/ru/zh live captions after GC; language ComboBox selection; preserved input/settings/pause/selection; stale settings merge; dialog captions; detached element collection. No proxy started.");
            File.Delete(Path.Combine(temp, "settings.json"));
            Directory.Delete(temp);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(report, "FAIL: " + ex);
            Environment.Exit(1);
        }
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
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateDetachedLocalizedElement()
    {
        var element = new TextBlock();
        Localize.SetText(element, "ServerList_HeaderText.Text");
        return new WeakReference(element);
    }
}
#endif

using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using XrayUI.Models;
using XrayUI.Services;
namespace XrayUI.ViewModels;
public sealed partial class ServerRowViewModel : ObservableObject, IDisposable
{
    [ObservableProperty] public partial ServerEntry Server { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCountry), nameof(FlagUri), nameof(CountryName))]
    public partial string? CountryCode { get; set; }
    public bool HasCountry => CountryCode != null;
    public Uri? FlagUri => CountryCode is { } code ? new Uri($"ms-appx:///Assets/Flags/{code.ToLowerInvariant()}.svg") : null;
    public string CountryName
    {
        get
        {
            if (CountryCode is not { } code) return "";
            try { return new RegionInfo(code).DisplayName + " (" + code + ")"; }
            catch (ArgumentException) { return code; }
        }
    }
    private bool _countryRequested;
    private bool _disposed;
    private readonly ServerCountryService _countries;
    public ServerRowViewModel(ServerEntry server, ServerCountryService? countries = null)
    { _countries = countries ?? ServerCountryService.Shared; Server = server; }
    partial void OnServerChanging(ServerEntry value)
    { if (Server != null) Server.PropertyChanged -= OnServerChanged; }
    partial void OnServerChanged(ServerEntry value)
    {
        value.PropertyChanged += OnServerChanged;
        CountryCode = null;
        if (_countryRequested) _ = EnsureCountryAsync();
    }
    private void OnServerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerEntry.Host) or nameof(ServerEntry.Protocol))
        {
            CountryCode = null;
            if (_countryRequested) _ = EnsureCountryAsync();
        }
    }
    // Called only for realized rows. Await resumes on the UI thread; stale DNS results
    // cannot paint a flag onto an edited server or a row with a replacement model.
    public async Task EnsureCountryAsync()
    {
        if (_disposed) return;
        _countryRequested = true;
        var server = Server;
        if (server.IsChain) { CountryCode = null; return; }
        var host = ServerCountryService.NormalizeHost(server.Host);
        var country = await _countries.GetCountryAsync(host);
        if (!_disposed && ReferenceEquals(Server, server) && !server.IsChain &&
            ServerCountryService.NormalizeHost(server.Host) == host) CountryCode = country;
    }
    public void RefreshLocalization() => OnPropertyChanged(nameof(CountryName));
    public void Dispose() { _disposed = true; Server.PropertyChanged -= OnServerChanged; }
}

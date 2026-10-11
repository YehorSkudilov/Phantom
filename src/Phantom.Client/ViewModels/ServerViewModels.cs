using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Client.Services;
using Phantom.Shared;

namespace Phantom.Client.ViewModels;

/// <summary>Torrents the server is downloading to its own disk.</summary>
public partial class ServerDownloadsViewModel(PhantomApi api, SettingsService settings) : BaseViewModel
{
    public ObservableCollection<ServerDownloadDto> Items { get; } = [];

    [ObservableProperty] private bool isAdmin;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private string totals = "";

    [RelayCommand]
    public Task Refresh() => RunAsync(async () =>
    {
        IsAdmin = settings.User?.IsAdmin == true;
        var items = await api.ServerDownloadsAsync();
        // Update in place so the list doesn't flicker on each poll.
        for (var i = 0; i < items.Count; i++)
        {
            if (i < Items.Count) Items[i] = items[i];
            else Items.Add(items[i]);
        }
        while (Items.Count > items.Count) Items.RemoveAt(Items.Count - 1);
        IsEmpty = Items.Count == 0;
        Totals = $"↓ {BytesConverter.Format(items.Sum(d => d.DownloadRate))}/s   ↑ {BytesConverter.Format(items.Sum(d => d.UploadRate))}/s   ·   {items.Count(d => d.Status == DownloadStatus.Completed)} completed";
    }, showBusy: false);

    [RelayCommand]
    private Task Toggle(ServerDownloadDto d) => RunAsync(async () =>
    {
        if (d.Status is DownloadStatus.Paused or DownloadStatus.Failed) await api.ResumeServerDownloadAsync(d.Id);
        else await api.PauseServerDownloadAsync(d.Id);
        await Refresh();
    }, showBusy: false);

    [RelayCommand]
    private Task Remove(ServerDownloadDto d) => RunAsync(async () =>
    {
        var choice = await Shell.Current.DisplayActionSheet(d.MovieTitle, "Cancel", "Remove and delete files", "Remove from list (keep files)");
        if (choice is null or "Cancel") return;
        await api.DeleteServerDownloadAsync(d.Id, deleteFiles: choice.Contains("delete"));
        await Refresh();
    }, showBusy: false);

    [RelayCommand]
    private Task Open(ServerDownloadDto d) => GoToAsync($"{nameof(Views.MovieDetailPage)}?id={d.MovieId}");
}

public partial class MoreViewModel(PhantomApi api, SettingsService settings) : BaseViewModel
{
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string serverText = "";
    [ObservableProperty] private bool isAdmin;
    [ObservableProperty] private string avatarColor = "#8B5CF6";
    [ObservableProperty] private string initial = "?";

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        var user = settings.User;
        Username = user?.Username ?? "";
        Initial = Username.Length > 0 ? Username[..1].ToUpperInvariant() : "?";
        AvatarColor = user?.AvatarColor ?? "#8B5CF6";
        IsAdmin = user?.IsAdmin == true;
        var info = await api.ServerInfoAsync();
        ServerText = $"{info.Name} · v{info.Version} · {info.MovieCount} movies";
    }, showBusy: false);

    [RelayCommand]
    private Task Navigate(string route) => GoToAsync(route);

    [RelayCommand]
    private Task SwitchProfile() => GoToAsync("//profiles");

    [RelayCommand]
    private async Task SignOut()
    {
        if (!await Shell.Current.DisplayAlert("Sign out", "Sign out of this server?", "Sign out", "Cancel")) return;
        settings.SignOut();
        await GoToAsync("//connect");
    }
}

public partial class SettingsViewModel(SettingsService settings) : BaseViewModel
{
    public IReadOnlyList<QualityOption> Qualities => QualityOption.All;
    public IReadOnlyList<int> SkipOptions { get; } = [5, 10, 15, 30, 60];
    public IReadOnlyList<string> Languages { get; } = ["en", "es", "fr", "de", "it", "pt", "ru", "ja", "zh", "ko", "uk", "pl"];

    [ObservableProperty] private QualityOption quality = settings.StreamingQuality;
    [ObservableProperty] private bool autoPlayNext = settings.AutoPlayNext;
    [ObservableProperty] private bool resumeAutomatically = settings.ResumeAutomatically;
    [ObservableProperty] private bool subtitlesByDefault = settings.SubtitlesByDefault;
    [ObservableProperty] private string subtitleLanguage = settings.PreferredSubtitleLanguage;
    [ObservableProperty] private double subtitleSize = settings.SubtitleSize;
    [ObservableProperty] private int skipForward = settings.SkipForwardSeconds;
    [ObservableProperty] private int skipBack = settings.SkipBackSeconds;
    [ObservableProperty] private bool wifiOnly = settings.DownloadOnWifiOnly;

    public string ServerUrl => settings.ServerUrl ?? "";
    public string AppVersion => $"Phantom {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";

    partial void OnQualityChanged(QualityOption value) => settings.StreamingQualityIndex = QualityOption.All.ToList().IndexOf(value);
    partial void OnAutoPlayNextChanged(bool value) => settings.AutoPlayNext = value;
    partial void OnResumeAutomaticallyChanged(bool value) => settings.ResumeAutomatically = value;
    partial void OnSubtitlesByDefaultChanged(bool value) => settings.SubtitlesByDefault = value;
    partial void OnSubtitleLanguageChanged(string value) => settings.PreferredSubtitleLanguage = value;
    partial void OnSubtitleSizeChanged(double value) => settings.SubtitleSize = value;
    partial void OnSkipForwardChanged(int value) => settings.SkipForwardSeconds = value;
    partial void OnSkipBackChanged(int value) => settings.SkipBackSeconds = value;
    partial void OnWifiOnlyChanged(bool value) => settings.DownloadOnWifiOnly = value;
}

/// <summary>Server dashboard for admins: activity, sources and server settings.</summary>
public partial class AdminViewModel(PhantomApi api) : BaseViewModel
{
    public ObservableCollection<NowPlayingDto> Sessions { get; } = [];
    public ObservableCollection<SourceDto> Sources { get; } = [];
    public IReadOnlyList<string> QualityChoices { get; } = ["2160p", "1080p", "720p", "SD"];

    [ObservableProperty] private ServerInfoDto? info;
    [ObservableProperty] private string serverName = "";
    [ObservableProperty] private int maxActiveDownloads;
    [ObservableProperty] private int maxDownloadKbps;
    [ObservableProperty] private int maxUploadKbps;
    [ObservableProperty] private bool seedAfterDownload;
    [ObservableProperty] private int streamCacheHours;
    [ObservableProperty] private bool autoDownloadNewReleases;
    [ObservableProperty] private string? preferredQuality;
    [ObservableProperty] private int minSeeders;

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        await RefreshActivity();
        Sources.Clear();
        foreach (var s in await api.SourcesAsync()) Sources.Add(s);

        var s2 = await api.ServerSettingsAsync();
        ServerName = s2.ServerName;
        MaxActiveDownloads = s2.MaxActiveDownloads;
        MaxDownloadKbps = s2.MaxDownloadKbps;
        MaxUploadKbps = s2.MaxUploadKbps;
        SeedAfterDownload = s2.SeedAfterDownload;
        StreamCacheHours = s2.StreamCacheHours;
        AutoDownloadNewReleases = s2.AutoDownloadNewReleases;
        PreferredQuality = s2.PreferredQuality;
        MinSeeders = s2.MinSeeders;
    });

    [RelayCommand]
    public Task RefreshActivity() => RunAsync(async () =>
    {
        Info = await api.ServerInfoAsync();
        var sessions = await api.SessionsAsync();
        Sessions.Clear();
        foreach (var s in sessions) Sessions.Add(s);
    }, showBusy: false);

    [RelayCommand]
    private Task SaveSettings() => RunAsync(async () =>
    {
        await api.SaveServerSettingsAsync(new ServerSettingsDto(ServerName, MaxActiveDownloads, MaxDownloadKbps, MaxUploadKbps,
            SeedAfterDownload, StreamCacheHours, AutoDownloadNewReleases, PreferredQuality, MinSeeders));
        await ToastAsync("Server settings saved");
    });

    [RelayCommand]
    private Task SyncAll() => RunAsync(async () =>
    {
        await api.SyncSourceAsync(0);
        await ToastAsync("Syncing all sources…");
    }, showBusy: false);

    [RelayCommand]
    private Task Sync(SourceDto s) => RunAsync(async () =>
    {
        await api.SyncSourceAsync(s.Id);
        await ToastAsync($"Syncing {s.Name}…");
    }, showBusy: false);

    [RelayCommand]
    private Task ToggleSource(SourceDto s) => RunAsync(async () =>
    {
        var updated = await api.UpdateSourceAsync(s.Id, new UpsertSourceRequest(s.Name, s.Type, s.BaseUrl, null, s.Categories, s.Query, !s.Enabled, s.SyncIntervalMinutes));
        Sources[Sources.IndexOf(s)] = updated;
    }, showBusy: false);

    [RelayCommand]
    private Task DeleteSource(SourceDto s) => RunAsync(async () =>
    {
        if (!await Shell.Current.DisplayAlert("Remove source", $"Remove {s.Name}? Movies already imported stay in the library.", "Remove", "Cancel")) return;
        await api.DeleteSourceAsync(s.Id);
        Sources.Remove(s);
    }, showBusy: false);

    /// <summary>Adds a Torznab indexer (Jackett/Prowlarr) the admin runs and is responsible for.</summary>
    [RelayCommand]
    private Task AddTorznab() => RunAsync(async () =>
    {
        var page = Shell.Current;
        var name = await page.DisplayPromptAsync("Torznab indexer", "Display name", initialValue: "Prowlarr");
        if (string.IsNullOrWhiteSpace(name)) return;
        var url = await page.DisplayPromptAsync("Torznab indexer", "Feed base URL (the part before /api)", initialValue: "http://localhost:9696/1", keyboard: Keyboard.Url);
        if (string.IsNullOrWhiteSpace(url)) return;
        var key = await page.DisplayPromptAsync("Torznab indexer", "API key");
        var cats = await page.DisplayPromptAsync("Torznab indexer", "Categories", initialValue: "2000");
        var query = await page.DisplayPromptAsync("Torznab indexer", "Optional search query (empty = latest)");
        var created = await api.CreateSourceAsync(new UpsertSourceRequest(name, SourceType.Torznab, url, key, cats, query, true, 360));
        Sources.Add(created);
    });

    [RelayCommand]
    private Task AddArchive() => RunAsync(async () =>
    {
        var query = await Shell.Current.DisplayPromptAsync("Internet Archive", "Archive.org search query",
            initialValue: "collection:(feature_films) AND mediatype:(movies)");
        if (string.IsNullOrWhiteSpace(query)) return;
        Sources.Add(await api.CreateSourceAsync(new UpsertSourceRequest("Internet Archive", SourceType.InternetArchive, null, null, "100", query, true, 720)));
    });
}

using Phantom.Controls;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Services;
using Phantom.Shared;

namespace Phantom.ViewModels;

/// <summary>Torrents the server is downloading to its own disk.</summary>
public partial class ServerDownloadsViewModel(PhantomApi api, SettingsService settings) : BaseViewModel
{
    public ObservableList<ServerDownloadDto> Items { get; } = [];

    [ObservableProperty] private bool isAdmin;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private string totals = "";

    protected override bool ShowsOfflineState => true;

    [RelayCommand]
    public Task Refresh() => RunAsync(async () =>
    {
        IsAdmin = settings.User?.IsAdmin == true;
        var items = await api.ServerDownloadsAsync();
        // Polled every couple of seconds: only rows whose progress or state changed are redrawn, and matched by id so
        // the list isn't reset (losing its scroll) when most of them have moved on.
        Items.Sync(items, key: d => d.Id);
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
        var choice = await Dialog.ChooseAsync(d.MovieTitle, "Remove and delete files", "Remove from list (keep files)");
        if (choice is null or "Cancel") return;
        await api.DeleteServerDownloadAsync(d.Id, deleteFiles: choice.Contains("delete"));
        await Refresh();
    }, showBusy: false);

    [RelayCommand]
    private Task Open(ServerDownloadDto d) => d.ShowId is { } showId
        ? GoToAsync($"{nameof(Views.ShowDetailPage)}?id={showId}")
        : GoToAsync($"{nameof(Views.MovieDetailPage)}?id={d.MovieId}");
}

public partial class MoreViewModel(PhantomApi api, SettingsService settings) : BaseViewModel
{
    /// <summary>The profile in use.</summary>
    [ObservableProperty] private string username = "";

    /// <summary>The account it belongs to (its owner's username).</summary>
    [ObservableProperty] private string? accountName;
    [ObservableProperty] private bool isAdmin;
    [ObservableProperty] private string avatarColor = "#8B5CF6";
    [ObservableProperty] private string initial = "?";
    [ObservableProperty] private string? avatar;

    public string AppVersion => $"Phantom {AppInfo.Current.VersionString}";

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        var user = settings.User;
        Username = user?.Username ?? "";
        Initial = Username.Length > 0 ? Username[..1].ToUpperInvariant() : "?";
        AvatarColor = user?.AvatarColor ?? "#8B5CF6";
        Avatar = user?.Avatar;
        IsAdmin = user?.IsAdmin == true;
        // A managed profile's account is its owner, the one profile that isn't managed.
        AccountName = user is { IsManaged: false } ? user.Username : null;
        var profiles = await api.ProfilesAsync();
        AccountName = profiles.FirstOrDefault(p => !p.IsManaged)?.Username ?? AccountName;
    }, showBusy: false);

    [RelayCommand]
    private Task Navigate(string route) => GoToAsync(route);

    [RelayCommand]
    private Task SwitchProfile() => GoToAsync("//profiles");

    /// <summary>Signs a TV in to this account with the code it shows.</summary>
    [RelayCommand]
    private Task SignInTv() => RunAsync(async () =>
    {
        var code = await Dialog.PromptAsync("Sign in a TV", "Enter the code shown on the TV", "Sign in", "Cancel",
            maxLength: 7, keyboard: Keyboard.Create(KeyboardFlags.CapitalizeCharacter));
        if (string.IsNullOrWhiteSpace(code)) return;
        try
        {
            var approved = await api.ApprovePairingAsync(code);
            await Dialog.AlertAsync("Sign in a TV", $"{approved.DeviceName} is now signed in to your account.");
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            await Dialog.AlertAsync("Sign in a TV", ex.Message);
        }
    }, showBusy: false);

    [RelayCommand]
    private async Task SignOut()
    {
        if (!await Dialog.ConfirmAsync("Sign out", "Sign out of this server?", "Sign out")) return;
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
    [ObservableProperty] private bool subtitlesByDefault = settings.SubtitlesByDefault;
    [ObservableProperty] private string subtitleLanguage = settings.PreferredSubtitleLanguage;
    [ObservableProperty] private double subtitleSize = settings.SubtitleSize;
    [ObservableProperty] private int skipForward = settings.SkipForwardSeconds;
    [ObservableProperty] private int skipBack = settings.SkipBackSeconds;

    partial void OnQualityChanged(QualityOption value) => settings.StreamingQuality = value;
    partial void OnAutoPlayNextChanged(bool value) => settings.AutoPlayNext = value;
    partial void OnSubtitlesByDefaultChanged(bool value) => settings.SubtitlesByDefault = value;
    partial void OnSubtitleLanguageChanged(string value) => settings.PreferredSubtitleLanguage = value;
    partial void OnSubtitleSizeChanged(double value) => settings.SubtitleSize = value;
    partial void OnSkipForwardChanged(int value) => settings.SkipForwardSeconds = value;
    partial void OnSkipBackChanged(int value) => settings.SkipBackSeconds = value;
}

/// <summary>An account in Manage server's list, with the profiles under it (its default one first).</summary>
public record AccountRow(UserDto Account, string Profiles);

/// <summary>Server dashboard for admins: activity, sources and server settings.</summary>
public partial class AdminViewModel(PhantomApi api) : BaseViewModel
{
    public ObservableList<NowPlayingDto> Sessions { get; } = [];
    public ObservableList<SourceDto> Sources { get; } = [];
    public ObservableList<AccountRow> Accounts { get; } = [];

    public IReadOnlyList<string> Tabs { get; } = ["Overview", "Sources", "Accounts", "Server settings", "Storage"];
    [ObservableProperty] private int selectedTab;

    /// <summary>The Storage tab's index in <see cref="Tabs"/>.</summary>
    public const int StorageTab = 4;

    partial void OnSelectedTabChanged(int value)
    {
        if (value == StorageTab) _ = RunAsync(LoadStorageAsync, showBusy: false);
    }

    // ---------- Storage ----------

    public ObservableList<StoredTorrentDto> StoredItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiskText), nameof(DiskUsed), nameof(BreakdownText), nameof(CacheLimitText), nameof(DiskLow))]
    private StorageDto? storage;

    public string DiskText => Storage is not { DiskTotalBytes: > 0 } s ? ""
        : $"{BytesConverter.Format(s.DiskFreeBytes)} free of {BytesConverter.Format(s.DiskTotalBytes)}";

    /// <summary>How full the disk is, 0-1, for the bar.</summary>
    public double DiskUsed => Storage is { DiskTotalBytes: > 0 } s ? 1 - (double)s.DiskFreeBytes / s.DiskTotalBytes : 0;

    /// <summary>Below the free space the server keeps (it clears stream caches to get it back).</summary>
    public bool DiskLow => Storage is { DiskTotalBytes: > 0 } s && s.DiskFreeBytes < (long)s.MinFreeDiskGb << 30;

    public string BreakdownText => Storage is not { } s ? "" : string.Join("\n",
        $"Saved on the server: {BytesConverter.Format(s.SavedBytes)}",
        $"Stream cache: {BytesConverter.Format(s.StreamCacheBytes)}",
        $"Seek previews: {BytesConverter.Format(s.PreviewBytes)}",
        $"Library database: {BytesConverter.Format(s.DatabaseBytes)}");

    public string CacheLimitText => Storage is not { } s ? ""
        : (s.MaxStreamCacheGb > 0 ? $"The stream cache is kept under {s.MaxStreamCacheGb} GB" : "The stream cache has no size limit")
          + $", and cleared when fewer than {s.MinFreeDiskGb} GB are free. A stream stops downloading 10 minutes after it was last watched. Change the limit in Server settings.";

    private async Task LoadStorageAsync()
    {
        ShowStorage(await api.StorageAsync());
    }

    private void ShowStorage(StorageDto storage)
    {
        Storage = storage;
        StoredItems.Sync(storage.Items);
    }

    [RelayCommand]
    private Task RefreshStorage() => RunAsync(LoadStorageAsync, showBusy: false);

    /// <summary>Clears a stream cache; for a copy saved on the server, removes it as Downloads would.</summary>
    [RelayCommand]
    private Task RemoveStored(StoredTorrentDto item) => RunAsync(async () =>
    {
        if (item.SavedOnServer)
        {
            if (item.DownloadId is not { } id)
            {
                await Dialog.AlertAsync("Saved on the server", "This copy isn't from a download Phantom knows about, so it can't be removed here.");
                return;
            }
            if (!await Dialog.ConfirmAsync("Remove from the server", $"Delete the saved copy of {item.Title} ({BytesConverter.Format(item.SizeBytes)})? It can still be streamed.", "Delete", destructive: true)) return;
            await api.DeleteServerDownloadAsync(id, deleteFiles: true);
        }
        else
        {
            await api.ClearStreamCacheAsync(item.InfoHash);
        }
        await LoadStorageAsync();
    }, showBusy: false);

    [RelayCommand]
    private Task ClearIdleCaches() => RunAsync(async () =>
    {
        if (!await Dialog.ConfirmAsync("Clear stream cache", "Delete the cached parts of everything nobody is watching right now? They download again when played.", "Clear", destructive: true)) return;
        ShowStorage(await api.ClearIdleCachesAsync());
        await ToastAsync("Stream cache cleared");
    }, showBusy: false);

    [RelayCommand]
    private Task ClearPreviews() => RunAsync(async () =>
    {
        ShowStorage(await api.ClearPreviewsAsync());
        await ToastAsync("Seek previews cleared; they're made again as needed");
    }, showBusy: false);

    /// <summary>Re-reads the sources (sync status, catalogue progress) in place, for the page's live update.</summary>
    public async Task RefreshSourcesAsync()
    {
        try
        {
            var sources = await api.SourcesAsync();
            for (var i = 0; i < sources.Count; i++)
            {
                if (i < Sources.Count) { if (Sources[i] != sources[i]) Sources[i] = sources[i]; }
                else Sources.Add(sources[i]);
            }
            while (Sources.Count > sources.Count) Sources.RemoveAt(Sources.Count - 1);
        }
        catch (Exception ex) when (ex is HttpRequestException or ApiException)
        {
            // Keep what's shown; the next tick tries again.
        }
    }
    public IReadOnlyList<string> QualityChoices { get; } = ["2160p", "1080p", "720p", "SD"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeaturesText))]
    private ServerInfoDto? info;

    /// <summary>The optional server features, in words.</summary>
    public string FeaturesText => Info is null ? "" : string.Join("   ·   ",
        Info.TranscoderAvailable ? "Transcoding on" : "Transcoding off (ffmpeg not installed)",
        Info.MetadataProviderConfigured ? "TMDB artwork on" : "TMDB artwork off (no API key)");

    [ObservableProperty] private string serverName = "";
    [ObservableProperty] private int maxActiveDownloads;
    [ObservableProperty] private int maxDownloadKbps;
    [ObservableProperty] private int maxUploadKbps;
    [ObservableProperty] private bool seedAfterDownload;
    [ObservableProperty] private int streamCacheHours;
    [ObservableProperty] private bool autoDownloadNewReleases;
    [ObservableProperty] private string? preferredQuality;
    [ObservableProperty] private int minSeeders;
    [ObservableProperty] private int keepServerDownloadsDays = 7;
    [ObservableProperty] private int maxStreamCacheGb;
    [ObservableProperty] private string metadataText = "";

    private async Task LoadMetadataAsync()
    {
        var m = await api.MetadataStatusAsync();
        MetadataText = !m.Configured
            ? "No TMDB API key on the server: movies get no artwork or cast (shows still get theirs from TVmaze)."
            : m.Pending == 0
                ? $"All {m.Total:N0} movies and shows are up to date. Each is refreshed again after 30 days (shows still airing, every 3)."
                : $"Refreshing {m.Pending:N0} of {m.Total:N0} movies and shows in the background.";
    }

    [RelayCommand]
    private Task RefreshAllMetadata() => RunAsync(async () =>
    {
        if (!await Dialog.ConfirmAsync("Refresh metadata", "Fetch posters, cast and details again for every movie? It runs in the background.", "Refresh all")) return;
        await api.RefreshAllMetadataAsync();
        await LoadMetadataAsync();
    }, showBusy: false);

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        await RefreshActivity();
        Sources.Sync(await api.SourcesAsync());
        await LoadAccountsAsync();
        await LoadMetadataAsync();

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
        KeepServerDownloadsDays = s2.KeepServerDownloadsDays;
        MaxStreamCacheGb = s2.MaxStreamCacheGb;
        if (SelectedTab == StorageTab) await LoadStorageAsync();
    });

    [RelayCommand]
    public Task RefreshActivity() => RunAsync(async () =>
    {
        Info = await api.ServerInfoAsync();
        var sessions = await api.SessionsAsync();
        Sessions.Sync(sessions);
    }, showBusy: false);

    private async Task LoadAccountsAsync()
    {
        var users = await api.UsersAsync();
        // The account's own profile (its default one) first, then the ones added under it.
        Accounts.ReplaceAll(users.Where(u => !u.IsManaged).Select(account => new AccountRow(account, string.Join(", ",
            [$"{account.Username} (default)", .. users.Where(u => u.IsManaged && u.AccountId == account.Id).Select(u => u.Username)]))));
    }

    /// <summary>A new account with its own sign-in; its owner adds profiles under it after signing in.</summary>
    [RelayCommand]
    private Task AddAccount() => RunAsync(async () =>
    {
        var page = Shell.Current;
        var name = await Dialog.PromptAsync("New account", "Username (what they sign in with)");
        if (string.IsNullOrWhiteSpace(name)) return;
        var password = await Dialog.PromptAsync("New account", "Password (at least 6 characters)", password: true);
        if (string.IsNullOrEmpty(password)) return;
        await api.CreateAccountAsync(name.Trim(), password);
        await LoadAccountsAsync();
        await ToastAsync($"{name.Trim()} can now sign in");
    }, showBusy: false);

    [RelayCommand]
    private Task ToggleAdmin(AccountRow row) => RunAsync(async () =>
    {
        var user = row.Account;
        var makeAdmin = !user.IsAdmin;
        var question = makeAdmin ? $"Let {user.Username} manage the server?" : $"Stop {user.Username} managing the server?";
        if (!await Dialog.ConfirmAsync("Server admin", question, makeAdmin ? "Make admin" : "Remove admin", "Cancel")) return;
        await api.SetAdminAsync(user.Id, makeAdmin);
        await LoadAccountsAsync();
    }, showBusy: false);

    [RelayCommand]
    private Task DeleteAccount(AccountRow row) => RunAsync(async () =>
    {
        var user = row.Account;
        if (!await Dialog.ConfirmAsync("Delete account", $"Delete {user.Username}, its profiles and their watch history?", "Delete", destructive: true)) return;
        await api.DeleteUserAsync(user.Id);
        Accounts.Remove(row);
    }, showBusy: false);

    [RelayCommand]
    private Task SaveSettings() => RunAsync(async () =>
    {
        await api.SaveServerSettingsAsync(new ServerSettingsDto(ServerName, MaxActiveDownloads, MaxDownloadKbps, MaxUploadKbps,
            SeedAfterDownload, StreamCacheHours, AutoDownloadNewReleases, PreferredQuality, MinSeeders, KeepServerDownloadsDays, MaxStreamCacheGb));
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
        var updated = await api.SetSourceEnabledAsync(s.Id, !s.Enabled);
        Sources[Sources.IndexOf(s)] = updated;
    }, showBusy: false);




}

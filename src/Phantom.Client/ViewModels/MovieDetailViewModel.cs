using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Services;
using Phantom.Shared;

namespace Phantom.ViewModels;

public partial class MovieDetailViewModel(PhantomApi api, SettingsService settings, DownloadManager downloads) : BaseViewModel, IQueryAttributable
{
    private int _id;

    public ObservableList<MovieSummaryDto> Related { get; } = [];
    public ObservableList<ReleaseDto> Releases { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Meta), nameof(CanResume), nameof(PlayLabel), nameof(Directors), nameof(Writers), nameof(GenreText), nameof(IsWatched), nameof(IsOnWatchlist), nameof(WatchedLabel), nameof(WatchlistLabel), nameof(WatchlistGlyph), nameof(IsRated), nameof(RateLabel))]
    private MovieDetailDto? movie;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReleaseDetails))]
    private ReleaseDto? selectedRelease;

    /// <summary>The selected version's quality, codec, size, seeders and source - whichever it has.</summary>
    public string ReleaseDetails => SelectedRelease is not { } r ? "" : string.Join("  ·  ", new[]
    {
        r.Quality, r.Codec, r.SizeBytes > 0 ? BytesConverter.Format(r.SizeBytes) : null,
        r.Seeders > 0 ? $"{r.Seeders} seeders" : null, r.Source,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
    [ObservableProperty] private OfflineDownload? offlineItem;
    [ObservableProperty] private bool isAdmin;

    public bool CanResume => Movie?.Playback is { PositionMs: > 0, Watched: false };
    public string PlayLabel => CanResume ? $"Resume from {TimeSpanConverter.Format(TimeSpan.FromMilliseconds(Movie!.Playback.PositionMs))}" : "Play";
    public bool IsWatched => Movie?.Playback.Watched == true;
    public bool IsOnWatchlist => Movie?.OnWatchlist == true;
    public string WatchedLabel => IsWatched ? "Watched" : "Mark watched";
    public string WatchlistLabel => IsOnWatchlist ? "On watchlist" : "Watchlist";
    public string WatchlistGlyph => IsOnWatchlist ? "bookmark_added" : "bookmark_add";
    public bool IsRated => Movie?.UserRating is not null;
    public string RateLabel => Movie?.UserRating is { } r ? $"Rated {r:0.#}" : "Rate";
    public string GenreText => Movie is null ? "" : string.Join(" · ", Movie.Genres);
    public string Directors => Movie is null ? "" : string.Join(", ", Movie.Crew.Where(c => c.Job == "Director").Select(c => c.Name));
    public string Writers => Movie is null ? "" : string.Join(", ", Movie.Crew.Where(c => c.Job == "Writer").Select(c => c.Name));

    public string Meta
    {
        get
        {
            if (Movie is null) return "";
            var parts = new List<string>();
            if (Movie.Year is { } y) parts.Add(y.ToString());
            if (MinutesConverter.Format(Movie.RuntimeMinutes) is { Length: > 0 } rt) parts.Add(rt);
            if (!string.IsNullOrEmpty(Movie.ContentRating)) parts.Add(Movie.ContentRating);
            if (Movie.Rating is { } r) parts.Add($"★ {r:0.0}");
            if (Movie.UserRating is { } ur) parts.Add($"Your rating {ur:0.#}");
            return string.Join("   ·   ", parts);
        }
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var id) && int.TryParse(id.ToString(), out var movieId))
        {
            _id = movieId;
            LoadCommand.Execute(null);
        }
    }

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        if (_id == 0) return;
        IsAdmin = settings.User?.IsAdmin == true;
        OfflineItem = downloads.Find(_id);
        // The movie and its More Like This row in parallel: one round trip's wait instead of two.
        var related = api.RelatedAsync(_id);
        try
        {
            Movie = await api.MovieAsync(_id);
        }
        catch (HttpRequestException) when (OfflineItem is { IsComplete: true } offline)
        {
            _ = related.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted); // offline too: nothing to show
            // Offline: build a minimal page from the downloaded copy.
            Movie = new MovieDetailDto(offline.MovieId, offline.Title, null, offline.Year, null, offline.Overview, offline.PosterSource, null,
                null, null, offline.RuntimeMinutes, null, null, null, null, null, [], [], [], [],
                new PlaybackStateDto(offline.OfflinePositionMs, offline.OfflineDurationMs, false, 0, null), false, false, offline.AddedAt);
            return;
        }
        Title = Movie.Title;
        // Reloaded on coming back from the player: keep the version that was picked if it's still there.
        var picked = SelectedRelease?.Id;
        Releases.Sync(Movie.Releases);
        SelectedRelease = Releases.FirstOrDefault(r => r.Id == picked) ?? Releases.FirstOrDefault();
        OnPropertyChanged(nameof(HasPlayChoices));

        Related.Sync(await related);
    });

    /// <summary>Plays, picking up where this profile left off (Start over beside it plays from the beginning).</summary>
    [RelayCommand]
    private Task Play() => Movie is null ? Task.CompletedTask : StartPlayback(CanResume);

    [RelayCommand]
    private Task PlayFromStart() => StartPlayback(resume: false);

    /// <summary>Plays the best copy there is: this device's download, then the server's saved copy, then streaming.</summary>
    private Task StartPlayback(bool resume) =>
        OfflineItem is { IsComplete: true } ? Open(resume, offline: true, SelectedRelease)
        : Open(resume, offline: false, Releases.FirstOrDefault(r => r.AvailableOffline) ?? SelectedRelease);

    private Task Open(bool resume, bool offline, ReleaseDto? release) =>
        GoToAsync($"{nameof(Views.PlayerPage)}?id={Movie!.Id}&resume={resume}"
                  + (release is { } r ? $"&releaseId={r.Id}" : "") + (offline ? "&offline=true" : ""));

    private const string FromDevice = "Downloaded copy on this device";
    private const string FromServer = "Saved copy on the server";

    /// <summary>More than one way to play: a downloaded or server-saved copy as well as streaming.</summary>
    public bool HasPlayChoices => OfflineItem is { IsComplete: true } || Releases.Any(r => r.AvailableOffline);

    partial void OnOfflineItemChanged(OfflineDownload? value) => OnPropertyChanged(nameof(HasPlayChoices));

    [RelayCommand]
    private async Task ChoosePlaySource()
    {
        if (Movie is null) return;
        var stream = Releases.FirstOrDefault(r => !r.AvailableOffline && r == SelectedRelease)
                     ?? Releases.FirstOrDefault(r => !r.AvailableOffline) ?? SelectedRelease;
        var streamLabel = stream?.Quality is { } q ? $"Stream it ({q})" : "Stream it";
        var choices = new List<string>();
        if (OfflineItem is { IsComplete: true }) choices.Add(FromDevice);
        if (Releases.Any(r => r.AvailableOffline)) choices.Add(FromServer);
        choices.Add(streamLabel);

        var choice = await Shell.Current.DisplayActionSheetAsync("Play from", "Cancel", null, [.. choices]);
        if (choice == FromDevice) await Open(CanResume, offline: true, SelectedRelease);
        else if (choice == FromServer) await Open(CanResume, offline: false, Releases.First(r => r.AvailableOffline));
        else if (choice == streamLabel) await Open(CanResume, offline: false, stream);
    }

    /// <summary>Shows the change straight away, as the server will make it, then tells the server (undone if that fails).</summary>
    [RelayCommand]
    private Task ToggleWatched() => RunAsync(async () =>
    {
        if (Movie is not { } before) return;
        var watched = !before.Playback.Watched;
        Movie = before with
        {
            Playback = before.Playback with
            {
                Watched = watched, PositionMs = 0,
                ViewCount = watched ? before.Playback.ViewCount + 1 : before.Playback.ViewCount,
            },
        };
        try
        {
            await api.MarkWatchedAsync(_id, watched);
        }
        catch
        {
            Movie = before;
            throw;
        }
    }, showBusy: false);

    [RelayCommand]
    private Task ToggleWatchlist() => RunAsync(async () =>
    {
        var on = Movie?.OnWatchlist != true;
        await api.SetWatchlistAsync(_id, on);
        Movie = Movie! with { OnWatchlist = on };
        await ToastAsync(on ? "Added to Watchlist" : "Removed from Watchlist");
    }, showBusy: false);

    [RelayCommand]
    private Task Rate() => RunAsync(async () =>
    {
        string[] options = ["★★★★★  10", "★★★★½  9", "★★★★  8", "★★★½  7", "★★★  6", "★★½  5", "★★  4", "★½  3", "★  2", "½  1", "Clear rating"];
        var choice = await Shell.Current.DisplayActionSheetAsync("Rate this movie", "Cancel", null, options);
        if (choice is null or "Cancel") return;
        double? rating = choice == "Clear rating" ? null : double.Parse(choice.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1]);
        await api.RateAsync(_id, rating);
        Movie = Movie! with { UserRating = rating };
    }, showBusy: false);

    [RelayCommand]
    private Task AddToPlaylist() => RunAsync(async () =>
    {
        var playlists = await api.PlaylistsAsync();
        const string newPlaylist = "+ New playlist";
        var choice = await Shell.Current.DisplayActionSheetAsync("Add to playlist", "Cancel", null, [.. playlists.Select(p => p.Name), newPlaylist]);
        if (choice is null or "Cancel") return;
        PlaylistDto? target = playlists.FirstOrDefault(p => p.Name == choice);
        if (choice == newPlaylist)
        {
            var name = await Shell.Current.DisplayPromptAsync("New playlist", "Name");
            if (string.IsNullOrWhiteSpace(name)) return;
            target = await api.CreatePlaylistAsync(name);
        }
        await api.AddToPlaylistAsync(target!.Id, _id);
        await ToastAsync($"Added to {target.Name}");
    }, showBusy: false);

    /// <summary>Client download: copies the movie to this device for offline viewing.</summary>
    [RelayCommand]
    private async Task DownloadToDevice()
    {
        if (Movie is null) return;
        if (OfflineItem is { } existing)
        {
            var action = await Shell.Current.DisplayActionSheetAsync(Movie.Title, "Cancel", "Delete download",
                existing.Status is OfflineStatus.Paused or OfflineStatus.Failed ? "Resume download" : "Go to Downloads");
            if (action == "Delete download") await downloads.DeleteAsync(existing);
            else if (action == "Resume download") downloads.Resume(existing);
            else if (action == "Go to Downloads") await Views.MainPage.ShowTab(Views.AppTab.Downloads);
            OfflineItem = downloads.Find(_id);
            return;
        }
        await downloads.EnqueueAsync(Movie, SelectedRelease?.Id);
        OfflineItem = downloads.Find(_id);
        await ToastAsync(settings.DownloadOnWifiOnly ? "Downloading to this device (Wi-Fi only)" : "Downloading to this device");
    }

    /// <summary>Server download: the server fetches the full torrent and keeps it on its disk.</summary>
    [RelayCommand]
    private Task DownloadOnServer() => RunAsync(async () =>
    {
        var d = await api.QueueServerDownloadAsync(_id, SelectedRelease?.Id);
        await ToastAsync(d.Status == DownloadStatus.Completed ? "Already on the server" : "Queued on the server");
    }, showBusy: false);

    [RelayCommand]
    private Task PlayTrailer()
    {
        if (Movie?.TrailerUrl is not { } url) return Task.CompletedTask;
        // YouTube trailers play in the app; anything else opens in the browser.
        return Views.TrailerPage.VideoId(url) is null
            ? Launcher.Default.OpenAsync(new Uri(url))
            : GoToAsync($"{nameof(Views.TrailerPage)}?url={Uri.EscapeDataString(url)}&title={Uri.EscapeDataString(Movie.Title)}");
    }

    [RelayCommand]
    private Task OpenPerson(string name) => Views.MainPage.ShowLibrary($"actor={Uri.EscapeDataString(name)}");

    [RelayCommand]
    private Task OpenDirector() =>
        Directors.Split(", ").FirstOrDefault() is { Length: > 0 } d ? Views.MainPage.ShowLibrary($"director={Uri.EscapeDataString(d)}") : Task.CompletedTask;

    [RelayCommand]
    private Task OpenGenre(string genre) => Views.MainPage.ShowLibrary($"genre={Uri.EscapeDataString(genre)}");
}

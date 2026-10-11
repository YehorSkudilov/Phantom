using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Services;
using Phantom.Shared;

namespace Phantom.ViewModels;

/// <summary>A show's page: its details, Play (the next episode), and a season's episodes to pick from.</summary>
public partial class ShowDetailViewModel(PhantomApi api) : BaseViewModel, IQueryAttributable
{
    private int _id;

    public ObservableList<SeasonDto> Seasons { get; } = [];
    public ObservableList<EpisodeDto> Episodes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Meta), nameof(GenreText), nameof(Creators), nameof(PlayLabel), nameof(CanPlay), nameof(NextProgress),
        nameof(IsOnWatchlist), nameof(WatchlistLabel), nameof(WatchlistGlyph), nameof(IsWatched), nameof(WatchedLabel))]
    private ShowDetailDto? show;

    [ObservableProperty] private SeasonDto? selectedSeason;

    public bool CanPlay => Show?.NextEpisode is not null;

    /// <summary>"Play S1:E1", "Resume S2:E5", or "Play S3:E1" once a season is done.</summary>
    public string PlayLabel => Show?.NextEpisode is not { } next ? "Nothing to play yet"
        : $"{(InProgress(next) ? "Resume" : "Play")} S{next.SeasonNumber}:E{next.EpisodeNumber}";

    public double NextProgress => Show?.NextEpisode is { } next && InProgress(next) ? next.Playback.ProgressPercent / 100 : 0;

    public bool IsOnWatchlist => Show?.OnWatchlist == true;
    public string WatchlistLabel => IsOnWatchlist ? "On watchlist" : "Watchlist";
    public string WatchlistGlyph => IsOnWatchlist ? "bookmark_added" : "bookmark_add";

    /// <summary>Every episode there is to play has been watched.</summary>
    public bool IsWatched => Show is { Seasons.Count: > 0 } s && s.Seasons.All(x => x.WatchedCount >= x.AvailableCount && x.AvailableCount > 0);
    public string WatchedLabel => IsWatched ? "Watched" : "Mark watched";

    public string GenreText => Show is null ? "" : string.Join(" · ", Show.Genres);
    public string Creators => Show is null ? "" : string.Join(", ", Show.Crew.Select(c => c.Name));

    public string Meta
    {
        get
        {
            if (Show is null) return "";
            var parts = new List<string>();
            if (Show.Year is { } y) parts.Add(Show.Status == "Ended" ? $"{y} · Ended" : y.ToString());
            var seasons = Show.Seasons.Count(s => s.Number > 0);
            if (seasons > 0) parts.Add(seasons == 1 ? "1 season" : $"{seasons} seasons");
            if (!string.IsNullOrEmpty(Show.ContentRating)) parts.Add(Show.ContentRating);
            if (Show.Rating is { } r) parts.Add($"★ {r:0.0}");
            return string.Join("   ·   ", parts);
        }
    }

    private static bool InProgress(EpisodeDto e) => e.Playback is { PositionMs: > 0, Watched: false };

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var id) && int.TryParse(id.ToString(), out var showId))
        {
            _id = showId;
            LoadCommand.Execute(null);
        }
    }

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        if (_id == 0) return;
        // Reloading (back from the player, or pulled down): the season being looked at is known, so its episodes load
        // alongside the show. The first time, the season to open on comes from the show (that of the episode Play starts).
        var known = SelectedSeason?.Number;
        var episodes = known is { } n ? api.SeasonAsync(_id, n) : null;
        Show = await api.ShowAsync(_id);
        Title = Show.Title;

        var season = known ?? Show.NextEpisode?.SeasonNumber ?? Show.Seasons.FirstOrDefault()?.Number;
        // The picker's list is only rebuilt when the seasons themselves change (that resets it, and redraws the page).
        if (!Seasons.Select(s => s.Number).SequenceEqual(Show.Seasons.Select(s => s.Number)))
        {
            _loadingSeasons = true;
            Seasons.ReplaceAll(Show.Seasons);
            SelectedSeason = Seasons.FirstOrDefault(s => s.Number == season) ?? Seasons.FirstOrDefault();
            _loadingSeasons = false;
        }
        if (episodes is not null && known is { } same && SelectedSeason?.Number == same) ShowEpisodes(same, await episodes);
        else await LoadEpisodesAsync();
    });

    private bool _loadingSeasons;

    partial void OnSelectedSeasonChanged(SeasonDto? value)
    {
        if (!_loadingSeasons) _ = RunAsync(LoadEpisodesAsync, showBusy: false);
    }

    private async Task LoadEpisodesAsync()
    {
        if (SelectedSeason is not { } season) return;
        ShowEpisodes(season.Number, await api.SeasonAsync(_id, season.Number));
    }

    /// <summary>
    /// Puts a season's episodes on the page. The same season again (a reload) only redraws the rows that changed;
    /// another season replaces the list in one go.
    /// </summary>
    private void ShowEpisodes(int season, IReadOnlyList<EpisodeDto> episodes)
    {
        if (SelectedSeason?.Number != season) return; // another season was picked meanwhile
        if (Episodes.Count > 0 && Episodes[0].SeasonNumber == season) Episodes.Sync(episodes);
        else Episodes.ReplaceAll(episodes);
    }

    /// <summary>Plays the next episode for this profile, from where it was left off.</summary>
    [RelayCommand]
    private Task Play() => Show?.NextEpisode is { } next ? PlayEpisode(next) : Task.CompletedTask;

    [RelayCommand]
    private async Task PlayEpisode(EpisodeDto? episode)
    {
        if (episode is null) return;
        if (!episode.Available)
        {
            await ToastAsync("No copy of this episode has been found yet.");
            return;
        }
        await GoToAsync($"{nameof(Views.PlayerPage)}?episode={episode.Id}&resume={InProgress(episode)}");
    }

    /// <summary>
    /// Ticks an episode off (or back on) straight away: its row and the season's counts change as the server will change
    /// them, then the server is told. Only the show's header (what Play starts next) is fetched again afterwards; the
    /// episode list isn't reloaded and redrawn.
    /// </summary>
    // Concurrent, so ticking off several episodes in a row isn't held up by each one's round trip.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleEpisodeWatched(EpisodeDto? episode) => episode is null ? Task.CompletedTask : RunAsync(async () =>
    {
        var watched = !episode.Playback.Watched;
        SetWatchedLocally([episode], watched);
        try
        {
            await api.MarkEpisodeWatchedAsync(episode.Id, watched);
        }
        catch
        {
            // Only this episode goes back: others ticked meanwhile keep their change.
            if (IndexOfEpisode(episode.Id) is var i and >= 0) Episodes[i] = episode;
            RecountSeasons(wholeShow: null);
            throw;
        }
        await RefreshHeaderAsync();
    }, showBusy: false);

    /// <summary>The whole show watched (or not), shown at once like an episode.</summary>
    [RelayCommand]
    private Task ToggleWatched() => RunAsync(async () =>
    {
        var watched = !IsWatched;
        var before = (Show, Episodes: Episodes.ToList());
        SetWatchedLocally(Episodes.ToList(), watched, wholeShow: true);
        try
        {
            await api.MarkShowWatchedAsync(_id, watched);
        }
        catch
        {
            Show = before.Show;
            Episodes.Sync(before.Episodes);
            throw;
        }
        await RefreshHeaderAsync();
    }, showBusy: false);

    /// <summary>What marking does on the server, done to what's on screen: the rows, and the seasons' watched counts.</summary>
    private void SetWatchedLocally(IReadOnlyList<EpisodeDto> changed, bool watched, bool wholeShow = false)
    {
        foreach (var e in changed)
        {
            var i = IndexOfEpisode(e.Id);
            if (i < 0 || e.Playback.Watched == watched && e.Playback.PositionMs == 0) continue;
            Episodes[i] = e with
            {
                Playback = e.Playback with
                {
                    Watched = watched, PositionMs = 0,
                    ViewCount = watched && !e.Playback.Watched ? e.Playback.ViewCount + 1 : e.Playback.ViewCount,
                },
            };
        }
        RecountSeasons(wholeShow ? watched : null);
    }

    /// <summary>
    /// The seasons' watched counts after a change on screen: every season's for the whole show (all or none), else the
    /// season on screen's, counted from its rows.
    /// </summary>
    private void RecountSeasons(bool? wholeShow)
    {
        if (Show is not { } show) return;
        var seasons = show.Seasons.Select(s =>
            wholeShow is { } all ? s with { WatchedCount = all ? s.EpisodeCount : 0 }
            : s.Number == SelectedSeason?.Number ? s with { WatchedCount = Episodes.Count(e => e.Playback.Watched) }
            : s).ToList();
        Show = show with { Seasons = seasons };
    }

    private int IndexOfEpisode(int id)
    {
        for (var i = 0; i < Episodes.Count; i++)
            if (Episodes[i].Id == id) return i;
        return -1;
    }

    /// <summary>The show's header again (Play's next episode and its progress), leaving the season picker and rows be.</summary>
    private async Task RefreshHeaderAsync()
    {
        var show = await api.ShowAsync(_id);
        if (show.Id == _id) Show = show;
    }

    [RelayCommand]
    private Task ToggleWatchlist() => RunAsync(async () =>
    {
        if (Show is null) return;
        await api.SetShowWatchlistAsync(_id, !IsOnWatchlist);
        Show = Show with { OnWatchlist = !IsOnWatchlist };
    }, showBusy: false);

    [RelayCommand]
    private Task PlayTrailer()
    {
        if (Show?.TrailerUrl is not { } url) return Task.CompletedTask;
        return Views.TrailerPage.VideoId(url) is null
            ? Launcher.Default.OpenAsync(new Uri(url))
            : GoToAsync($"{nameof(Views.TrailerPage)}?url={Uri.EscapeDataString(url)}&title={Uri.EscapeDataString(Show.Title)}");
    }

    [RelayCommand]
    private Task OpenPerson(string name) => Views.MainPage.ShowLibrary($"kind=Show&actor={Uri.EscapeDataString(name)}");

    [RelayCommand]
    private Task OpenGenre(string genre) => Views.MainPage.ShowLibrary($"kind=Show&genre={Uri.EscapeDataString(genre)}");
}

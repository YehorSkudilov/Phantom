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

    public ObservableCollection<SeasonDto> Seasons { get; } = [];
    public ObservableCollection<EpisodeDto> Episodes { get; } = [];

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
        Show = await api.ShowAsync(_id);
        Title = Show.Title;

        // Stay on the season being looked at; the first time, open on the season of the episode Play would start.
        var season = SelectedSeason?.Number ?? Show.NextEpisode?.SeasonNumber ?? Show.Seasons.FirstOrDefault()?.Number;
        Seasons.Clear();
        foreach (var s in Show.Seasons) Seasons.Add(s);
        _loadingSeasons = true;
        SelectedSeason = Seasons.FirstOrDefault(s => s.Number == season) ?? Seasons.FirstOrDefault();
        _loadingSeasons = false;
        await LoadEpisodesAsync();
    });

    private bool _loadingSeasons;

    partial void OnSelectedSeasonChanged(SeasonDto? value)
    {
        if (!_loadingSeasons) _ = RunAsync(LoadEpisodesAsync, showBusy: false);
    }

    private async Task LoadEpisodesAsync()
    {
        if (SelectedSeason is not { } season) return;
        var episodes = await api.SeasonAsync(_id, season.Number);
        if (SelectedSeason?.Number != season.Number) return; // another season was picked meanwhile
        Episodes.Clear();
        foreach (var e in episodes) Episodes.Add(e);
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

    [RelayCommand]
    private Task ToggleEpisodeWatched(EpisodeDto? episode) => episode is null ? Task.CompletedTask : RunAsync(async () =>
    {
        await api.MarkEpisodeWatchedAsync(episode.Id, !episode.Playback.Watched);
        await Load();
    }, showBusy: false);

    [RelayCommand]
    private Task ToggleWatched() => RunAsync(async () =>
    {
        await api.MarkShowWatchedAsync(_id, !IsWatched);
        await Load();
    }, showBusy: false);

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

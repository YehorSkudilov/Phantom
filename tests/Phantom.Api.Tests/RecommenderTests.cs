using Phantom.Api.Services;

namespace Phantom.Api.Tests;

public class RecommenderTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Candidate Movie(int id, string[] genres, string[]? people = null, int year = 2010, double rating = 7, int seeders = 10,
        int viewers = 0, int addedDaysAgo = 100) =>
        new(new TitleRef(false, id), $"Movie {id}", genres, people ?? [], year, rating, Now.AddDays(-addedDaysAgo), seeders, viewers);

    [Fact]
    public void Recommends_titles_like_what_was_watched_over_better_rated_ones_in_other_genres()
    {
        var watched = Movie(1, ["Crime", "Drama"], ["Jane Director"]);
        var crime = Movie(2, ["Crime", "Thriller"], ["Jane Director"], rating: 6.5);
        var romance = Movie(3, ["Romance"], rating: 9);
        var titles = new[] { watched, crime, romance }.ToDictionary(c => c.Ref);
        var taste = Recommender.BuildTaste([new TasteSignal(watched.Ref, 1, Now.AddDays(-3))], titles, Now);

        var picks = Recommender.ForYou([crime, romance], taste, Now);
        Assert.Equal([2, 3], picks.Select(p => p.Ref.Id));
    }

    [Fact]
    public void A_low_rating_steers_away_from_titles_like_it()
    {
        var hated = Movie(1, ["Horror"]);
        var liked = Movie(2, ["Comedy"]);
        var horror = Movie(3, ["Horror"], rating: 8);
        var comedy = Movie(4, ["Comedy"], rating: 6);
        var titles = new[] { hated, liked, horror, comedy }.ToDictionary(c => c.Ref);
        var taste = Recommender.BuildTaste([new TasteSignal(hated.Ref, -1.5, Now), new TasteSignal(liked.Ref, 1, Now)], titles, Now);

        Assert.Equal(4, Recommender.ForYou([horror, comedy], taste, Now)[0].Ref.Id);
    }

    [Fact]
    public void Picks_are_spread_across_genres()
    {
        // Mostly action, some comedy: the comedy still makes the first few, rather than action after action.
        var watched = Movie(1, ["Action"]);
        var watchedComedy = Movie(2, ["Comedy"]);
        var actions = Enumerable.Range(10, 6).Select(i => Movie(i, ["Action"])).ToList();
        var comedy = Movie(20, ["Comedy"], rating: 6);
        var titles = actions.Append(watched).Append(watchedComedy).Append(comedy).ToDictionary(c => c.Ref);
        var taste = Recommender.BuildTaste([new TasteSignal(watched.Ref, 1, Now), new TasteSignal(watchedComedy.Ref, 0.6, Now)], titles, Now);

        var picks = Recommender.ForYou([.. actions, comedy], taste, Now, take: 4);
        Assert.Contains(picks, p => p.Ref.Id == 20);
    }

    [Fact]
    public void Popular_now_puts_what_people_here_are_watching_first()
    {
        var watchedHere = Movie(1, ["Drama"], seeders: 1, viewers: 3, year: 1990);
        var bigTorrent = Movie(2, ["Drama"], seeders: 5000, year: 2000);
        var nothing = Movie(3, ["Drama"], seeders: 0, year: 1980);
        var popular = Recommender.PopularNow([nothing, bigTorrent, watchedHere], Now);
        Assert.Equal([1, 2], popular.Select(p => p.Ref.Id));
    }

    [Fact]
    public void More_like_needs_something_in_common()
    {
        var seed = Movie(1, ["Sci-Fi"], ["Ridley Scott"]);
        var sameDirector = Movie(2, ["Drama"], ["Ridley Scott"]);
        var sameGenre = Movie(3, ["Sci-Fi"]);
        var unrelated = Movie(4, ["Romance"], rating: 9.5);
        var like = Recommender.MoreLike(seed, [unrelated, sameDirector, sameGenre], Now);
        Assert.Equal([3, 2], like.Select(c => c.Ref.Id));
    }
}

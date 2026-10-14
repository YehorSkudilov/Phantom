using Phantom.Api.Controllers;
using Phantom.Api.Services;
using Phantom.Api.Services.Sources;

namespace Phantom.Api.Tests;

public class ReleaseNameParserTests
{
    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-GROUP", "The Matrix", 1999, "1080p", "H.264")]
    [InlineData("Blade Runner 2049 (2017) [2160p] [HEVC]", "Blade Runner 2049", 2017, "2160p", "HEVC")]
    [InlineData("2012.2009.720p.WEB-DL.mkv", "2012", 2009, "720p", null)]
    [InlineData("Nosferatu_1922_DVDRip_XviD", "Nosferatu", 1922, null, "MPEG-4")]
    [InlineData("Some Movie", "Some Movie", null, null, null)]
    public void Parses_scene_names(string input, string title, int? year, string? quality, string? codec)
    {
        var parsed = ReleaseNameParser.Parse(input);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(quality, parsed.Quality);
        Assert.Equal(codec, parsed.Codec);
    }

    [Fact]
    public void Normalized_keys_ignore_punctuation_and_accents()
    {
        Assert.Equal(ReleaseNameParser.NormalizeKey("Amélie", 2001), ReleaseNameParser.NormalizeKey("Amelie!", 2001));
        Assert.NotEqual(ReleaseNameParser.NormalizeKey("Dune", 1984), ReleaseNameParser.NormalizeKey("Dune", 2021));
    }

    [Theory]
    [InlineData("The Matrix", "matrix")]
    [InlineData("A Bug's Life", "bug's life")]
    [InlineData("Theory", "theory")]
    public void Sort_titles_drop_leading_articles(string title, string expected) =>
        Assert.Equal(expected, ReleaseNameParser.SortTitle(title));

    [Fact]
    public void Content_rating_ladder_is_inclusive()
    {
        var allowed = ContentRatings.AllowedUpTo("PG");
        Assert.Contains("G", allowed);
        Assert.Contains("PG", allowed);
        Assert.DoesNotContain("PG-13", allowed);
        Assert.DoesNotContain("R", allowed);
    }

    [Theory]
    [InlineData("1:29:45", 89)]
    [InlineData("72:10", 72)]
    [InlineData("90 min", 90)]
    [InlineData(null, null)]
    public void Archive_runtime_parsing(string? input, int? expected) =>
        Assert.Equal(expected, InternetArchiveSource.ParseRuntime(input));
}

public class YtsSourceTests
{
    private const string Page = """
        {"status":"ok","data":{"movie_count":1,"movies":[{
          "title":"Night of the Living Dead","title_english":"Night of the Living Dead","year":1968,"runtime":96,
          "imdb_code":"tt0063350","genres":["Horror","Thriller"],"summary":"Ghouls.","large_cover_image":"https://img/x.jpg",
          "torrents":[
            {"hash":"ABCDEF0123456789ABCDEF0123456789ABCDEF01","quality":"1080p","type":"bluray","video_codec":"x264","seeds":120,"peers":8,"size_bytes":1825361101},
            {"hash":"","quality":"720p"}]}]}}
        """;

    [Fact]
    public void Parses_movies_and_their_torrents()
    {
        var (releases, count) = YtsSource.Parse(Page);
        Assert.Equal(1, count);
        var r = Assert.Single(releases);
        Assert.Equal("Night of the Living Dead", r.Title);
        Assert.Equal(1968, r.Year);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", r.InfoHash);
        Assert.StartsWith("magnet:?xt=urn:btih:abcdef0123456789abcdef0123456789abcdef01", r.MagnetUri);
        Assert.Equal("1080p", r.Quality);
        Assert.Equal(120, r.Seeders);
        Assert.Equal("tt0063350", r.ImdbId);
        Assert.Equal(["Horror", "Thriller"], r.Genres);
        Assert.Equal(96, r.RuntimeMinutes);
    }

    [Fact]
    public void Empty_page_has_no_movies() =>
        Assert.Empty(YtsSource.Parse("""{"status":"ok","data":{"movie_count":0}}""").Releases);
}

public class PirateBaySourceTests
{
    [Fact]
    public void Parses_search_results_with_string_numbers()
    {
        var r = Assert.Single(PirateBaySource.Parse("""
            [{"id":"1","name":"Night.of.the.Living.Dead.1968.1080p.BluRay.x264","info_hash":"ABCDEF0123456789ABCDEF0123456789ABCDEF01",
              "seeders":"42","leechers":"3","size":"2147483648","imdb":"tt0063350"}]
            """));
        Assert.Equal("Night of the Living Dead", r.Title);
        Assert.Equal(1968, r.Year);
        Assert.Equal(42, r.Seeders);
        Assert.Equal(2147483648, r.SizeBytes);
        Assert.Equal("tt0063350", r.ImdbId);
    }

    [Fact]
    public void Skips_the_no_results_placeholder() =>
        Assert.Empty(PirateBaySource.Parse("""
            [{"id":"0","name":"No results returned","info_hash":"0000000000000000000000000000000000000000","seeders":"0"}]
            """));
}

public class KnabenSourceTests
{
    [Fact]
    public void Keeps_movies_and_skips_episodes_and_other_categories()
    {
        var (releases, hits, total) = KnabenSource.Parse("""
            {"total":{"relation":"eq","value":5604},"hits":[
              {"title":"Fight Club (1999) 1080p BrRip x264 -YIFY","hash":"ABCDEF0123456789ABCDEF0123456789ABCDEF01","categoryId":[3001000,3000000],
               "bytes":1825361101,"seeders":700,"peers":720,"magnetUrl":"magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789ABCDEF01"},
              {"title":"Some Show S01E02 1080p","hash":"BBCDEF0123456789ABCDEF0123456789ABCDEF01","categoryId":[3000000],"seeders":5},
              {"title":"Album (1999) FLAC","hash":"CBCDEF0123456789ABCDEF0123456789ABCDEF01","categoryId":[1000000],"seeders":5}
            ]}
            """);
        Assert.Equal(3, hits);
        Assert.Equal(5604, total);
        var r = Assert.Single(releases);
        Assert.Equal("Fight Club", r.Title);
        Assert.Equal(1999, r.Year);
        Assert.Equal("1080p", r.Quality);
        Assert.Equal(700, r.Seeders);
        Assert.Equal(20, r.Leechers);
    }
}

public class PirateBayCategoryTests
{
    [Fact]
    public void Search_results_are_filtered_to_movie_categories()
    {
        var releases = PirateBaySource.Parse("""
            [{"name":"The Matrix (1999) 1080p","info_hash":"ABCDEF0123456789ABCDEF0123456789ABCDEF01","category":"207","seeders":"9"},
             {"name":"The Matrix soundtrack (1999)","info_hash":"BBCDEF0123456789ABCDEF0123456789ABCDEF01","category":"101","seeders":"9"}]
            """, new HashSet<string> { "201", "207" });
        Assert.Equal("The Matrix", Assert.Single(releases).Title);
    }
}

public class CatalogueCrawlTests
{
    [Fact]
    public void Reports_progress_through_the_walk_and_restarts_after_a_week()
    {
        var dir = Directory.CreateTempSubdirectory("phantom-crawl-").FullName;
        try
        {
            var crawl = new CatalogueCrawl(dir, "test");
            Assert.Equal((0, 0), crawl.Next());
            Assert.Equal(0, crawl.Progress(100));

            crawl.Save(50, 3);
            Assert.Equal((50, 3), crawl.Next());
            Assert.Equal(50, crawl.Progress(100));

            crawl.Finish();
            Assert.False(crawl.HasMore);
            Assert.Equal(100, crawl.Progress(100));

            File.WriteAllText(Path.Combine(dir, "crawl-test.txt"), $"done {DateTime.UtcNow.AddDays(-8).Ticks}");
            Assert.Equal((0, 0), crawl.Next()); // due again
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class SubtitleConverterTests
{
    [Fact]
    public void Converts_srt_timestamps_to_vtt()
    {
        var vtt = SubtitleConverter.SrtToVtt("1\r\n00:00:01,500 --> 00:00:03,000\r\nHello, world\r\n");
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:01.500 --> 00:00:03.000", vtt);
        Assert.Contains("Hello, world", vtt); // commas in dialogue are untouched
    }

    [Fact]
    public void Trickplay_takes_a_keyframe_every_step_in_one_pass()
    {
        var args = Transcoder.TrickplayArguments("in.mkv", 10, 320, "out/%d.jpg");
        Assert.Equal("nokey", args[args.ToList().IndexOf("-skip_frame") + 1]);
        Assert.Equal("fps=1/10,scale=320:-2", args[args.ToList().IndexOf("-vf") + 1]);
        Assert.Equal("out/%d.jpg", args[^1]);
    }

    [Fact]
    public void Trickplay_lists_the_frames_made_so_far_in_order()
    {
        var dir = Directory.CreateTempSubdirectory("phantom-trick-").FullName;
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new Phantom.Api.PhantomOptions { DataDirectory = dir, FfmpegPath = "/nonexistent" });
            var service = new TrickplayService(new Transcoder(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<Transcoder>.Instance),
                options, Microsoft.Extensions.Logging.Abstractions.NullLogger<TrickplayService>.Instance);
            Directory.CreateDirectory(service.Folder("7"));
            foreach (var name in new[] { "20.jpg", "0.jpg", "10.jpg", "notes.txt" }) File.WriteAllText(Path.Combine(service.Folder("7"), name), "x");

            Assert.Equal([0, 10, 20], service.Frames("7").Select(f => f.Seconds));
            Assert.False(service.IsComplete("7"));
            service.Request("7", "in.mkv"); // no ffmpeg: nothing is queued, and nothing throws
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Transcoder_arguments_seek_and_cap_bitrate()
    {
        var args = Transcoder.BuildArguments("in.mkv", 90.5, 2000, 720);
        Assert.Equal("90.5", args[args.ToList().IndexOf("-ss") + 1]);
        Assert.Contains("2000k", args);
        Assert.Equal("1000000", args[args.ToList().IndexOf("-frag_duration") + 1]);
        Assert.Equal("pipe:1", args[^1]);
    }
}

public class EroticGenreTests
{
    [Theory]
    [InlineData("""{"keywords":{"keywords":[{"id":1,"name":"erotic movie"}]}}""", true)]
    [InlineData("""{"keywords":{"results":[{"id":1,"name":"Erotic Thriller"}]}}""", true)]
    [InlineData("""{"keywords":{"keywords":[{"id":1,"name":"softcore"}]}}""", true)]
    [InlineData("""{"keywords":{"keywords":[{"id":1,"name":"heist"},{"id":2,"name":"based on novel"}]}}""", false)]
    [InlineData("""{"title":"No keywords"}""", false)]
    public void Tmdb_keywords_mark_erotic_films(string json, bool erotic)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(erotic, Phantom.Api.Services.Metadata.TmdbMetadataProvider.IsErotic(doc.RootElement));
    }
}

public class WorldCinemaTests
{
    private static readonly Phantom.Api.Services.Sources.WorldCinemaSource.Film Brother = new(20992, "Brother", "Брат", 1997);

    private static string Hits(params string[] names) =>
        "{\"hits\":[" + string.Join(',', names.Select((n, i) =>
            $"{{\"hash\":\"{new string((char)('a' + i), 40)}\",\"title\":{System.Text.Json.JsonSerializer.Serialize(n)},\"seeders\":5,\"peers\":7,\"bytes\":1000}}")) + "]}";

    [Fact]
    public void Torrents_with_either_title_and_the_year_match_the_film()
    {
        var json = Hits(
            "Брат / Brother (Алексей Балабанов) [1997, Россия, драма, BDRip 1080p]",
            "Brother.1997.1080p.BluRay.x264",
            "Brother.2000.1080p.BluRay.x264", // Kitano's, another year
            "Big.Brother.1997.720p"); // has the title words, but inside another title too: still counts (rare enough)
        var matches = Phantom.Api.Services.Sources.WorldCinemaSource.Matches(json, Brother).ToList();
        Assert.Equal(3, matches.Count);
        Assert.All(matches, m => Assert.Equal(20992, m.TmdbId));
        Assert.All(matches, m => Assert.Equal("Brother", m.Title));
        Assert.Equal("1080p", matches[0].Quality);
        Assert.Equal(2, matches[0].Leechers);
    }

    [Fact]
    public void Chinese_titles_match_by_their_characters()
    {
        var film = new Phantom.Api.Services.Sources.WorldCinemaSource.Film(1, "Let the Bullets Fly", "让子弹飞", 2010);
        var matches = Phantom.Api.Services.Sources.WorldCinemaSource.Matches(Hits("让子弹飞.2010.1080p.WEB-DL", "Let.the.Bullets.Fly.2010.720p"), film);
        Assert.Equal(2, matches.Count());
    }

    [Fact]
    public void Discover_results_need_a_release_date()
    {
        var (films, pages) = Phantom.Api.Services.Sources.WorldCinemaSource.ParseDiscover(
            """{"page":1,"total_pages":12,"results":[{"id":5,"title":"Hero","original_title":"英雄","release_date":"2002-12-19"},{"id":6,"title":"Unreleased","release_date":""}]}""");
        Assert.Equal(12, pages);
        var hero = Assert.Single(films);
        Assert.Equal(("Hero", "英雄", 2002), (hero.Title, hero.OriginalTitle, hero.Year));
    }

    [Fact]
    public void Magnets_get_the_trackers_they_lack()
    {
        var first = Phantom.Api.Services.Sources.Magnets.Trackers[0];
        var magnet = $"magnet:?xt=urn:btih:abc&tr={Uri.EscapeDataString(first)}";
        var full = Phantom.Api.Services.Sources.Magnets.WithTrackers(magnet);
        Assert.Equal(Phantom.Api.Services.Sources.Magnets.Trackers.Length, full.Split("&tr=").Length - 1);
    }

    [Fact]
    public void Countries_come_from_origin_and_production()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            """{"origin_country":["CN"],"production_countries":[{"iso_3166_1":"HK"},{"iso_3166_1":"cn"}]}""");
        Assert.Equal("CN,HK", Phantom.Api.Services.Metadata.TmdbMetadataProvider.Countries(doc.RootElement));
    }

    [Fact]
    public void Region_names()
    {
        Assert.Equal("Russian", Phantom.Shared.Regions.Language("ru"));
        Assert.Equal("Chinese (Cantonese)", Phantom.Shared.Regions.Language("cn"));
        Assert.Equal("China", Phantom.Shared.Regions.Country("CN"));
        var countries = Phantom.Shared.Regions.Countries(["CN,HK", "RU", null, "CN"]);
        Assert.Equal(["China", "Hong Kong", "Russia"], countries.Select(c => c.Name));
    }
}

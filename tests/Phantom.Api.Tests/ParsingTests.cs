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

public class TorznabSourceTests
{
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel>
            <item>
              <title>Night.of.the.Living.Dead.1968.1080p.BluRay.x264</title>
              <link>magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789ABCDEF01&amp;dn=notld</link>
              <size>2147483648</size>
              <torznab:attr name="seeders" value="42" />
              <torznab:attr name="peers" value="50" />
              <torznab:attr name="imdbid" value="0063350" />
            </item>
            <item>
              <title>No hash here</title>
              <link>https://example.invalid/file.torrent</link>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void Parses_items_with_magnets_and_attributes()
    {
        var release = Assert.Single(TorznabSource.Parse(Feed));
        Assert.Equal("Night of the Living Dead", release.Title);
        Assert.Equal(1968, release.Year);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", release.InfoHash);
        Assert.Equal("1080p", release.Quality);
        Assert.Equal(2147483648, release.SizeBytes);
        Assert.Equal(42, release.Seeders);
        Assert.Equal(8, release.Leechers);
        Assert.Equal("tt0063350", release.ImdbId);
    }

    [Fact]
    public void Indexer_errors_are_surfaced() =>
        Assert.Throws<InvalidOperationException>(() =>
            TorznabSource.Parse("""<?xml version="1.0"?><error code="100" description="Invalid API Key" />"""));
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
    public void Transcoder_arguments_seek_and_cap_bitrate()
    {
        var args = Transcoder.BuildArguments("in.mkv", 90.5, 2000, 720);
        Assert.Equal("90.5", args[args.ToList().IndexOf("-ss") + 1]);
        Assert.Contains("2000k", args);
        Assert.Equal("pipe:1", args[^1]);
    }
}

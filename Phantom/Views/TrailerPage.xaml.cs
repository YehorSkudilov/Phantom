using System.Text.RegularExpressions;
using Phantom.Services;

namespace Phantom.Views;

/// <summary>Plays a YouTube trailer in place, through the server's /trailer page (see TrailerController).</summary>
[QueryProperty(nameof(TrailerUrl), "url")]
[QueryProperty(nameof(MovieTitle), "title")]
public partial class TrailerPage : ContentPage
{
    public TrailerPage()
    {
        InitializeComponent();
    }

    public string TrailerUrl
    {
        set
        {
            var id = VideoId(Uri.UnescapeDataString(value));
            if (id is not null) Player.Source = $"{ApiConfig.ServerUrl}/trailer/{id}";
        }
    }

    public string MovieTitle
    {
        set => TitleLabel.Text = $"{Uri.UnescapeDataString(value)} · Trailer";
    }

    /// <summary>The video id from youtube.com/watch?v=..., youtu.be/... or youtube.com/embed/... links.</summary>
    public static string? VideoId(string url)
    {
        var m = Regex.Match(url, @"(?:[?&]v=|youtu\.be/|/embed/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    // Stops the video (and its sound) when leaving, however the page is closed.
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Player.Source = new HtmlWebViewSource { Html = "<html><body style='background:#000'></body></html>" };
    }

    private async void OnClose(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}

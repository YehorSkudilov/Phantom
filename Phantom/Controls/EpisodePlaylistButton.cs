using Phantom.Services;

namespace Phantom.Controls;

/// <summary>An episode's Playlist button: says (and shows, in colour) whether the episode is in a playlist already.</summary>
public class EpisodePlaylistButton : Button
{
    public static readonly BindableProperty MembershipProperty =
        BindableProperty.Create(nameof(Membership), typeof(PlaylistMembership), typeof(EpisodePlaylistButton),
            propertyChanged: (b, o, n) => ((EpisodePlaylistButton)b).Watch(o as PlaylistMembership, n as PlaylistMembership));

    public static readonly BindableProperty EpisodeIdProperty =
        BindableProperty.Create(nameof(EpisodeId), typeof(int), typeof(EpisodePlaylistButton), 0,
            propertyChanged: (b, _, _) => ((EpisodePlaylistButton)b).Update());

    public PlaylistMembership? Membership { get => (PlaylistMembership?)GetValue(MembershipProperty); set => SetValue(MembershipProperty, value); }
    public int EpisodeId { get => (int)GetValue(EpisodeIdProperty); set => SetValue(EpisodeIdProperty, value); }

    private bool? _in;
    private bool _onScreen;

    public EpisodePlaylistButton()
    {
        // Only listens while on screen, so rows of a closed page aren't kept alive.
        Loaded += (_, _) =>
        {
            _onScreen = true;
            Watch(null, Membership);
        };
        Unloaded += (_, _) =>
        {
            Watch(Membership, null);
            _onScreen = false;
        };
        Update();
    }

    private void Watch(PlaylistMembership? before, PlaylistMembership? after)
    {
        if (before is not null) before.Changed -= OnChanged;
        if (after is not null && _onScreen)
        {
            after.Changed -= OnChanged;
            after.Changed += OnChanged;
        }
        Update();
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(Update);

    private void Update()
    {
        var playlists = Membership?.PlaylistsOf($"e{EpisodeId}") ?? [];
        var isIn = playlists.Count > 0;
        if (isIn == _in) return;
        _in = isIn;
        var resources = Application.Current!.Resources;
        var color = (Color)resources[isIn ? "PrimaryLight" : "TextPrimary"];
        ImageSource = IconExtension.Create(isIn ? "playlist_add_check" : "playlist_add", color, size: 18);
        TextColor = color;
        Text = isIn ? "In playlist" : "Playlist";
        SemanticProperties.SetDescription(this, isIn ? $"In {string.Join(", ", playlists)}" : "Add to a playlist");
    }
}

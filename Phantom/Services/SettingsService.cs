using System.Text.Json;
using Phantom.Shared;

namespace Phantom.Services;

/// <summary>Streaming quality presets offered in settings and the player, like Plex's quality menu.</summary>
public record QualityOption(string Name, int? MaxKbps, int? MaxHeight, bool IsAuto = false)
{
    public bool IsOriginal => !IsAuto && MaxKbps is null && MaxHeight is null;
    public override string ToString() => Name;

    public static readonly QualityOption Auto = new("Auto (by connection)", null, null, IsAuto: true);

    public static readonly IReadOnlyList<QualityOption> All =
    [
        Auto,
        new("Original (Direct Play)", null, null),
        new("1080p · 8 Mbps", 8000, 1080),
        new("720p · 4 Mbps", 4000, 720),
        new("480p · 1.5 Mbps", 1500, 480),
        new("360p · 0.7 Mbps", 700, 360),
    ];

    /// <summary>
    /// What Auto means on the current connection: the original on Wi-Fi or a wired network, 720p on mobile data, 480p
    /// on a metered or limited connection.
    /// </summary>
    public static QualityOption ForConnection()
    {
        var access = Connectivity.Current.NetworkAccess;
        var profiles = Connectivity.Current.ConnectionProfiles;
        if (access == NetworkAccess.ConstrainedInternet) return All.First(q => q.MaxHeight == 480);
        if (profiles.Any(p => p is ConnectionProfile.WiFi or ConnectionProfile.Ethernet) || !profiles.Any()) return All.First(q => q.IsOriginal);
        return All.First(q => q.MaxHeight == 720);
    }

    /// <summary>This option, with Auto turned into the concrete quality for the current connection.</summary>
    public QualityOption Resolve() => IsAuto ? ForConnection() : this;
}

/// <summary>Device-local preferences and the signed-in session.</summary>
public class SettingsService
{
    private const string UserKey = "user";
    private readonly IPreferences _prefs = Preferences.Default;

    public string ServerUrl => ApiConfig.ServerUrl;

    public string? Token
    {
        get => _prefs.Get<string?>(nameof(Token), null);
        set => _prefs.Set(nameof(Token), value);
    }

    public UserDto? User
    {
        get
        {
            var json = _prefs.Get<string?>(UserKey, null);
            return json is null ? null : JsonSerializer.Deserialize<UserDto>(json);
        }
        set
        {
            if (value is null) _prefs.Remove(UserKey);
            else _prefs.Set(UserKey, JsonSerializer.Serialize(value));
        }
    }

    /// <summary>The player's volume (0-1), kept between movies.</summary>
    public double PlayerVolume
    {
        get => Math.Clamp(_prefs.Get(nameof(PlayerVolume), 1.0), 0, 1);
        set => _prefs.Set(nameof(PlayerVolume), Math.Clamp(value, 0, 1));
    }

    /// <summary>The player's screen brightness (0-1), kept between movies; null until it's been set (the system's).</summary>
    public double? PlayerBrightness
    {
        get => _prefs.Get(nameof(PlayerBrightness), -1.0) is var b and >= 0 ? Math.Clamp(b, 0, 1) : null;
        set => _prefs.Set(nameof(PlayerBrightness), value is { } b ? Math.Clamp(b, 0, 1) : -1.0);
    }

    /// <summary>Kept by name (not position) so new choices like Auto don't shift what people picked.</summary>
    public QualityOption StreamingQuality
    {
        get => QualityOption.All.FirstOrDefault(q => q.Name == _prefs.Get<string?>("StreamingQualityName", null)) ?? QualityOption.Auto;
        set => _prefs.Set("StreamingQualityName", value.Name);
    }

    public bool AutoPlayNext
    {
        get => _prefs.Get(nameof(AutoPlayNext), true);
        set => _prefs.Set(nameof(AutoPlayNext), value);
    }

    public bool SubtitlesByDefault
    {
        get => _prefs.Get(nameof(SubtitlesByDefault), false);
        set => _prefs.Set(nameof(SubtitlesByDefault), value);
    }

    public string PreferredSubtitleLanguage
    {
        get => _prefs.Get(nameof(PreferredSubtitleLanguage), "en");
        set => _prefs.Set(nameof(PreferredSubtitleLanguage), value);
    }

    public double SubtitleSize
    {
        get => _prefs.Get(nameof(SubtitleSize), 20.0);
        set => _prefs.Set(nameof(SubtitleSize), value);
    }

    public int SkipForwardSeconds
    {
        get => _prefs.Get(nameof(SkipForwardSeconds), 30);
        set => _prefs.Set(nameof(SkipForwardSeconds), value);
    }

    public int SkipBackSeconds
    {
        get => _prefs.Get(nameof(SkipBackSeconds), 10);
        set => _prefs.Set(nameof(SkipBackSeconds), value);
    }

    public bool DownloadOnWifiOnly
    {
        get => _prefs.Get(nameof(DownloadOnWifiOnly), true);
        set => _prefs.Set(nameof(DownloadOnWifiOnly), value);
    }

    public string LibraryView
    {
        get => _prefs.Get(nameof(LibraryView), "grid");
        set => _prefs.Set(nameof(LibraryView), value);
    }

    public string ClientName => $"{DeviceInfo.Current.Name} ({DeviceInfo.Current.Platform})";

    public bool IsSignedIn => !string.IsNullOrEmpty(Token);

    public void SignOut()
    {
        Token = null;
        User = null;
    }
}

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

    private UserDto? _user;
    private bool _userRead;

    public UserDto? User
    {
        get
        {
            if (!_userRead)
            {
                var json = _prefs.Get<string?>(UserKey, null);
                _user = json is null ? null : JsonSerializer.Deserialize<UserDto>(json);
                _userRead = true;
            }
            return _user;
        }
        set
        {
            var before = User?.Id;
            if (value is null) _prefs.Remove(UserKey);
            else _prefs.Set(UserKey, JsonSerializer.Serialize(value));
            _user = value;
            _userRead = true;
            if (before != value?.Id) ProfileChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Another profile is in use (or none): what's kept per profile (downloads, these settings) is its now.</summary>
    public event EventHandler? ProfileChanged;

    // ---------- Per profile ----------
    // Each profile is a user of its own: the settings below (and the device's downloads) are kept for each, by its
    // id. A profile that hasn't set one yet starts from what the device had before settings were kept per profile.

    private string Key(string name) => User is { } user ? $"p{user.Id}.{name}" : name;

    private T Get<T>(string name, T fallback)
    {
        var key = Key(name);
        return _prefs.ContainsKey(key) ? _prefs.Get(key, fallback) : _prefs.Get(name, fallback);
    }

    private void Set<T>(string name, T value) => _prefs.Set(Key(name), value);

    /// <summary>The player's volume (0-1), kept between movies.</summary>
    public double PlayerVolume
    {
        get => Math.Clamp(Get(nameof(PlayerVolume), 1.0), 0, 1);
        set => Set(nameof(PlayerVolume), Math.Clamp(value, 0, 1));
    }

    /// <summary>The player's screen brightness (0-1), kept between movies; null until it's been set (the system's).</summary>
    public double? PlayerBrightness
    {
        get => Get(nameof(PlayerBrightness), -1.0) is var b and >= 0 ? Math.Clamp(b, 0, 1) : null;
        set => Set(nameof(PlayerBrightness), value is { } b ? Math.Clamp(b, 0, 1) : -1.0);
    }

    /// <summary>Kept by name (not position) so new choices like Auto don't shift what people picked.</summary>
    public QualityOption StreamingQuality
    {
        get => QualityOption.All.FirstOrDefault(q => q.Name == Get<string?>("StreamingQualityName", null)) ?? QualityOption.Auto;
        set => Set("StreamingQualityName", value.Name);
    }

    public bool AutoPlayNext
    {
        get => Get(nameof(AutoPlayNext), true);
        set => Set(nameof(AutoPlayNext), value);
    }

    public bool SubtitlesByDefault
    {
        get => Get(nameof(SubtitlesByDefault), false);
        set => Set(nameof(SubtitlesByDefault), value);
    }

    public string PreferredSubtitleLanguage
    {
        get => Get(nameof(PreferredSubtitleLanguage), "en");
        set => Set(nameof(PreferredSubtitleLanguage), value);
    }

    public double SubtitleSize
    {
        get => Get(nameof(SubtitleSize), 20.0);
        set => Set(nameof(SubtitleSize), value);
    }

    public int SkipForwardSeconds
    {
        get => Get(nameof(SkipForwardSeconds), 30);
        set => Set(nameof(SkipForwardSeconds), value);
    }

    public int SkipBackSeconds
    {
        get => Get(nameof(SkipBackSeconds), 10);
        set => Set(nameof(SkipBackSeconds), value);
    }

    public bool DownloadOnWifiOnly
    {
        get => Get(nameof(DownloadOnWifiOnly), true);
        set => Set(nameof(DownloadOnWifiOnly), value);
    }

    public string LibraryView
    {
        get => Get(nameof(LibraryView), "grid");
        set => Set(nameof(LibraryView), value);
    }

    public string ClientName => $"{DeviceInfo.Current.Name} ({DeviceInfo.Current.Platform})";

    public bool IsSignedIn => !string.IsNullOrEmpty(Token);

    public void SignOut()
    {
        Token = null;
        User = null;
    }
}

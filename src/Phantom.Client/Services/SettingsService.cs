using System.Text.Json;
using Phantom.Shared;

namespace Phantom.Services;

/// <summary>Streaming quality presets offered in settings and the player, like Plex's quality menu.</summary>
public record QualityOption(string Name, int? MaxKbps, int? MaxHeight)
{
    public bool IsOriginal => MaxKbps is null && MaxHeight is null;
    public override string ToString() => Name;

    public static readonly IReadOnlyList<QualityOption> All =
    [
        new("Original (Direct Play)", null, null),
        new("1080p · 8 Mbps", 8000, 1080),
        new("720p · 4 Mbps", 4000, 720),
        new("480p · 1.5 Mbps", 1500, 480),
        new("360p · 0.7 Mbps", 700, 360),
    ];
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

    public int StreamingQualityIndex
    {
        get => Math.Clamp(_prefs.Get(nameof(StreamingQualityIndex), 0), 0, QualityOption.All.Count - 1);
        set => _prefs.Set(nameof(StreamingQualityIndex), value);
    }

    public QualityOption StreamingQuality => QualityOption.All[StreamingQualityIndex];

    public bool AutoPlayNext
    {
        get => _prefs.Get(nameof(AutoPlayNext), true);
        set => _prefs.Set(nameof(AutoPlayNext), value);
    }

    public bool ResumeAutomatically
    {
        get => _prefs.Get(nameof(ResumeAutomatically), false);
        set => _prefs.Set(nameof(ResumeAutomatically), value);
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

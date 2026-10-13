namespace Phantom.Services;

/// <summary>
/// The player's volume and brightness sliders on the device itself. Android: the volume is the device's media volume
/// (what its own volume keys change), and the brightness the app window's (the system's comes back once the player
/// closes). iOS: the screen's brightness, put back as it was on closing; the system volume can't be set by an app,
/// so the volume stays the player's own. Elsewhere (Windows) both stay the player's: there's no screen brightness.
/// </summary>
public static class DeviceControls
{
#if ANDROID
    public static bool HasDeviceVolume => true;
    // A TV's picture is its own settings' business; a window's brightness isn't honoured there.
    public static bool HasBrightness => DeviceInfo.Idiom != DeviceIdiom.TV;

    private static Android.Media.AudioManager? Audio =>
        Platform.AppContext.GetSystemService(Android.Content.Context.AudioService) as Android.Media.AudioManager;

    /// <summary>The device's media volume, 0 to 1.</summary>
    public static double GetVolume()
    {
        if (Audio is not { } audio) return 1;
        var max = audio.GetStreamMaxVolume(Android.Media.Stream.Music);
        return max > 0 ? (double)audio.GetStreamVolume(Android.Media.Stream.Music) / max : 1;
    }

    public static void SetVolume(double volume)
    {
        if (Audio is not { } audio) return;
        var max = audio.GetStreamMaxVolume(Android.Media.Stream.Music);
        var level = (int)Math.Round(Math.Clamp(volume, 0, 1) * max);
        if (level != audio.GetStreamVolume(Android.Media.Stream.Music))
            audio.SetStreamVolume(Android.Media.Stream.Music, level, Android.Media.VolumeNotificationFlags.RemoveSoundAndVibrate);
    }

    /// <summary>The window's brightness, 0 to 1: the one set here, else the system's.</summary>
    public static double GetBrightness()
    {
        var set = Platform.CurrentActivity?.Window?.Attributes?.ScreenBrightness ?? -1;
        if (set >= 0) return set;
        try
        {
            return Android.Provider.Settings.System.GetInt(Platform.AppContext.ContentResolver, Android.Provider.Settings.System.ScreenBrightness) / 255.0;
        }
        catch (Exception)
        {
            return 0.5;
        }
    }

    /// <summary>Sets the window's brightness, or with null gives it back to the system's.</summary>
    public static void SetBrightness(double? brightness)
    {
        if (Platform.CurrentActivity?.Window is not { Attributes: { } attributes } window) return;
        // Never quite 0: a black screen with no way to see the slider back.
        attributes.ScreenBrightness = brightness is { } b ? (float)Math.Clamp(b, 0.02, 1) : -1f;
        window.Attributes = attributes;
    }
#elif IOS || MACCATALYST
    public static bool HasDeviceVolume => false;
    public static bool HasBrightness => DeviceInfo.Platform == DevicePlatform.iOS;

    private static double? _systemBrightness;

    public static double GetVolume() => 1;
    public static void SetVolume(double volume) { }

    public static double GetBrightness() => (double)UIKit.UIScreen.MainScreen.Brightness;

    /// <summary>Sets the screen's brightness, or with null puts back what it was before the player set it.</summary>
    public static void SetBrightness(double? brightness)
    {
        if (brightness is { } b)
        {
            _systemBrightness ??= GetBrightness();
            UIKit.UIScreen.MainScreen.Brightness = (nfloat)Math.Clamp(b, 0.02, 1);
        }
        else if (_systemBrightness is { } before)
        {
            UIKit.UIScreen.MainScreen.Brightness = (nfloat)before;
            _systemBrightness = null;
        }
    }
#else
    public static bool HasDeviceVolume => false;
    public static bool HasBrightness => false;
    public static double GetVolume() => 1;
    public static void SetVolume(double volume) { }
    public static double GetBrightness() => 1;
    public static void SetBrightness(double? brightness) { }
#endif
}

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace Phantom;

// ResizeableActivity + SupportsPictureInPicture let the player keep running in a PiP window.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask,
    ResizeableActivity = true, SupportsPictureInPicture = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Also in the Android TV launcher (MainLauncher already adds the phone launcher's filter).
[IntentFilter([Intent.ActionMain], Categories = [Intent.CategoryLeanbackLauncher])]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>
    /// The open page's own say over keys (a TV remote's, a keyboard's) before the focused view gets them: the player's
    /// play/pause and skipping. True when it used the key. Pop-ups are windows of their own, so it doesn't see theirs.
    /// </summary>
    public static Func<KeyEvent, bool>? KeyHandler { get; set; }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is not null && (KeyHandler?.Invoke(e) == true || Droid.TvNavigation.HandleKey(this, e))) return true;
        return base.DispatchKeyEvent(e);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Droid.TvNavigation.Attach(this);
    }
}

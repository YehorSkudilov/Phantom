using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Phantom;

// ResizeableActivity + SupportsPictureInPicture let the player keep running in a PiP window.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask,
    ResizeableActivity = true, SupportsPictureInPicture = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Also in the Android TV launcher (MainLauncher already adds the phone launcher's filter).
[IntentFilter([Intent.ActionMain], Categories = [Intent.CategoryLeanbackLauncher])]
public class MainActivity : MauiAppCompatActivity
{
}

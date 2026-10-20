using Foundation;

namespace Phantom;

// iOS 27 terminates apps at launch that still use the app-delegate-only lifecycle, so the app runs in a scene (see
// UIApplicationSceneManifest in Info.plist); MAUI's scene delegate creates the window there.
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}

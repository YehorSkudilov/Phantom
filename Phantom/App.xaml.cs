using Phantom.Services;

namespace Phantom;

public partial class App : Application
{
    private readonly SettingsService _settings;

    public App(SettingsService settings, PhantomApi api)
    {
        InitializeComponent();
        _settings = settings;

        // An expired or revoked token sends the user back to sign-in.
        api.Unauthorized += (_, _) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            _settings.SignOut();
            await Shell.Current.GoToAsync("//connect");
        });
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var shell = new AppShell(_settings);
        // If the app crashed last time, say so once it's up, with the details to copy into feedback.
        shell.Loaded += async (_, _) => await CrashLog.OfferLastAsync();
        var window = new Window(shell) { Title = "Phantom" };
        // Whether it's on screen: requests cut off while it's away aren't errors to show (see NetworkState).
        window.Stopped += (_, _) => NetworkState.InBackground = true;
        window.Resumed += (_, _) =>
        {
            NetworkState.InBackground = false;
            NetworkState.ResumedAt = DateTime.UtcNow;
        };
        return window;
    }
}

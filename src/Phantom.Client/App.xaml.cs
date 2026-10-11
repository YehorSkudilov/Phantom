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
        shell.Loaded += async (_, _) => await CrashLog.OfferLastAsync(shell);
        return new Window(shell) { Title = "Phantom" };
    }
}

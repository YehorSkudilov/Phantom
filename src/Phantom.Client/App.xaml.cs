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

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new AppShell(_settings)) { Title = "Phantom" };
}

using Phantom.Services;
using Phantom.Views;

namespace Phantom;

public partial class AppShell : Shell
{
    public AppShell(SettingsService settings)
    {
        InitializeComponent();
        CurrentItem = settings.IsSignedIn ? MainItem : ConnectItem;

        // Pushed (non-tab) pages.
        Routing.RegisterRoute(nameof(MovieDetailPage), typeof(MovieDetailPage));
        Routing.RegisterRoute(nameof(ShowDetailPage), typeof(ShowDetailPage));
        Routing.RegisterRoute(nameof(PlayerPage), typeof(PlayerPage));
        Routing.RegisterRoute(nameof(TrailerPage), typeof(TrailerPage));
        Routing.RegisterRoute(nameof(MovieListPage), typeof(MovieListPage));
        Routing.RegisterRoute(nameof(PlaylistPage), typeof(PlaylistPage));
        Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
        Routing.RegisterRoute(nameof(AccountPage), typeof(AccountPage));
        Routing.RegisterRoute(nameof(AdminPage), typeof(AdminPage));
    }
}

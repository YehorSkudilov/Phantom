using Phantom.Services;
using Phantom.Views;

namespace Phantom;

public partial class AppShell : Shell
{
    public AppShell(SettingsService settings)
    {
        InitializeComponent();
        CurrentItem = settings.IsSignedIn ? MainTabs : ConnectItem;

        // Pushed (non-tab) pages.
        Routing.RegisterRoute(nameof(MovieDetailPage), typeof(MovieDetailPage));
        Routing.RegisterRoute(nameof(PlayerPage), typeof(PlayerPage));
        Routing.RegisterRoute(nameof(MovieListPage), typeof(MovieListPage));
        Routing.RegisterRoute(nameof(CollectionsPage), typeof(CollectionsPage));
        Routing.RegisterRoute(nameof(PlaylistsPage), typeof(PlaylistsPage));
        Routing.RegisterRoute(nameof(PlaylistPage), typeof(PlaylistPage));
        Routing.RegisterRoute(nameof(ServerDownloadsPage), typeof(ServerDownloadsPage));
        Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
        Routing.RegisterRoute(nameof(AdminPage), typeof(AdminPage));
    }
}

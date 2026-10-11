using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Phantom.Client.Services;
using Phantom.Client.ViewModels;
using Phantom.Client.Views;

namespace Phantom.Client;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .UseMauiCommunityToolkitMediaElement(_ => { })
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Shared HttpClient: long timeout because torrent-backed requests can wait on peers.
        builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(3) });
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<PhantomApi>();
        builder.Services.AddSingleton<DownloadManager>();

        // Tab pages keep their state; pushed pages are created per navigation.
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<LibraryViewModel>();
        builder.Services.AddSingleton<SearchViewModel>();
        builder.Services.AddSingleton<DownloadsViewModel>();
        builder.Services.AddSingleton<MoreViewModel>();
        builder.Services.AddTransient<ConnectViewModel>();
        builder.Services.AddTransient<ProfilesViewModel>();
        builder.Services.AddTransient<MovieDetailViewModel>();
        builder.Services.AddTransient<PlayerViewModel>();
        builder.Services.AddTransient<MovieListViewModel>();
        builder.Services.AddTransient<CollectionsViewModel>();
        builder.Services.AddTransient<PlaylistsViewModel>();
        builder.Services.AddTransient<PlaylistViewModel>();
        builder.Services.AddTransient<ServerDownloadsViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<AdminViewModel>();

        builder.Services.AddSingleton<HomePage>();
        builder.Services.AddSingleton<LibraryPage>();
        builder.Services.AddSingleton<SearchPage>();
        builder.Services.AddSingleton<DownloadsPage>();
        builder.Services.AddSingleton<MorePage>();
        builder.Services.AddTransient<ConnectPage>();
        builder.Services.AddTransient<ProfilesPage>();
        builder.Services.AddTransient<MovieDetailPage>();
        builder.Services.AddTransient<PlayerPage>();
        builder.Services.AddTransient<MovieListPage>();
        builder.Services.AddTransient<CollectionsPage>();
        builder.Services.AddTransient<PlaylistsPage>();
        builder.Services.AddTransient<PlaylistPage>();
        builder.Services.AddTransient<ServerDownloadsPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<AdminPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}

using AppSkeleton;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Phantom.Services;
using Phantom.ViewModels;
using Phantom.Views;

namespace Phantom;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        Services.CrashLog.Install();
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseAppSkeleton()
            .UseMauiCommunityToolkit()
            // Foreground service keeps audio going with the screen off and shows media controls in the notification shade.
            .UseMauiCommunityToolkitMediaElement(isAndroidForegroundServiceEnabled: true)
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                // Material icons ("OutlinedIcons" / "RegularIcons") for the nav bar.
                fonts.AddCIcons();
            });

        // Shared HttpClient: long timeout because torrent-backed requests can wait on peers. Compressed replies are
        // unpacked (the server gzips its JSON: the home screen's rows go from ~75 KB to ~5 KB); video isn't compressed.
        builder.Services.AddSingleton(new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        })
        { Timeout = TimeSpan.FromMinutes(3) });
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<PhantomApi>();
        builder.Services.AddSingleton<DownloadManager>();
        builder.Services.AddTransient<PlaylistMembership>();

        // Tab pages keep their state; pushed pages are created per navigation.
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<LibraryViewModel>();
        builder.Services.AddSingleton<MyStuffViewModel>();
        builder.Services.AddSingleton<SearchViewModel>();
        builder.Services.AddSingleton<DownloadsViewModel>();
        builder.Services.AddSingleton<MoreViewModel>();
        builder.Services.AddTransient<ConnectViewModel>();
        builder.Services.AddTransient<ProfilesViewModel>();
        builder.Services.AddTransient<MovieDetailViewModel>();
        builder.Services.AddTransient<ShowDetailViewModel>();
        builder.Services.AddTransient<PlayerViewModel>();
        builder.Services.AddTransient<MovieListViewModel>();
        builder.Services.AddTransient<CollectionsViewModel>();
        builder.Services.AddTransient<PlaylistsViewModel>();
        builder.Services.AddTransient<PlaylistViewModel>();
        builder.Services.AddTransient<ServerDownloadsViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<AccountViewModel>();
        builder.Services.AddTransient<AdminViewModel>();

        // MainPage hosts the tab views for the app's lifetime, so they're singletons together.
        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<HomePage>();
        builder.Services.AddSingleton<LibraryPage>();
        builder.Services.AddSingleton<MyStuffPage>();
        builder.Services.AddSingleton<DownloadsPage>();
        builder.Services.AddSingleton<MorePage>();
        builder.Services.AddTransient<ConnectPage>();
        builder.Services.AddTransient<ProfilesPage>();
        builder.Services.AddTransient<MovieDetailPage>();
        builder.Services.AddTransient<ShowDetailPage>();
        builder.Services.AddTransient<PlayerPage>();
        builder.Services.AddTransient<TrailerPage>();
        builder.Services.AddTransient<MovieListPage>();
        builder.Services.AddTransient<PlaylistPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<AccountPage>();
        builder.Services.AddTransient<AdminPage>();

#if ANDROID
        // Every sideways list scrolls sideways, rather than the drag swiping to the next tab (see KeepHorizontalSwipes).
        Microsoft.Maui.Controls.Handlers.Items.CollectionViewHandler.Mapper.AppendToMapping("KeepHorizontalSwipes", (handler, view) =>
        {
            if (view is CollectionView { ItemsLayout: LinearItemsLayout { Orientation: ItemsLayoutOrientation.Horizontal } })
                Phantom.Droid.KeepHorizontalSwipes.Attach(handler.PlatformView);
        });
#endif

#if WINDOWS
        // Every sideways list: the mouse wheel scrolls the page, Shift+wheel scrolls the row (see ShiftWheelScrolling).
        Microsoft.Maui.Controls.Handlers.Items.CollectionViewHandler.Mapper.AppendToMapping("ShiftWheelScrolling", (handler, view) =>
        {
            if (view is CollectionView { ItemsLayout: LinearItemsLayout { Orientation: ItemsLayoutOrientation.Horizontal } })
                Phantom.WinUI.ShiftWheelScrolling.Attach(handler.PlatformView);
        });
        // Ctrl + wheel down or Ctrl + Down refreshes, as pulling down does on a phone (see CtrlRefresh).
        Microsoft.Maui.Handlers.RefreshViewHandler.Mapper.AppendToMapping("CtrlRefresh", (handler, view) =>
            Phantom.WinUI.CtrlRefresh.Attach(handler.PlatformView, view));
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}

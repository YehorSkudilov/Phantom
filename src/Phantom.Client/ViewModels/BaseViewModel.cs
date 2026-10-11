using Phantom.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Services;
using Phantom.Shared;

namespace Phantom.ViewModels;

public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool isBusy;

    /// <summary>What went wrong, shown in a pop-up (the same everywhere in the app) as soon as it's set.</summary>
    [ObservableProperty]
    private string? errorMessage;

    partial void OnErrorMessageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _ = Dialog.ErrorAsync(value);
    }

    [ObservableProperty]
    private string title = "";

    /// <summary>The server couldn't be reached the last time this tried (cleared by the next thing that works).</summary>
    [ObservableProperty]
    private bool isOffline;

    /// <summary>The first load has finished (well or not): before that a page is "loading", not "empty".</summary>
    [ObservableProperty]
    private bool hasLoaded;

    /// <summary>
    /// The page shows an offline state of its own (see StateView), so not reaching the server is said there, and with a
    /// toast, instead of in an error pop-up.
    /// </summary>
    protected virtual bool ShowsOfflineState => false;

    public bool IsNotBusy => !IsBusy;

    /// <summary>A RefreshView's spinner (two-way bound): set by pulling, cleared when the load it started finishes.</summary>
    [ObservableProperty]
    private bool isRefreshing;

    /// <summary>The spinner stays at least this long, so a quick refresh is still seen to happen.</summary>
    private static readonly TimeSpan MinRefreshTime = TimeSpan.FromSeconds(1);

    private DateTime _refreshStarted;

    partial void OnIsRefreshingChanged(bool value)
    {
        if (value) _refreshStarted = DateTime.UtcNow;
    }

    /// <summary>Clears the spinner, once it has shown for <see cref="MinRefreshTime"/>.</summary>
    protected async Task EndRefreshAsync()
    {
        if (!IsRefreshing) return;
        var remaining = MinRefreshTime - (DateTime.UtcNow - _refreshStarted);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        IsRefreshing = false;
    }

    /// <summary>Runs an operation with busy/error handling so pages never crash on network errors.</summary>
    protected async Task RunAsync(Func<Task> action, bool showBusy = true)
    {
        if (showBusy && IsBusy)
        {
            // Already loading: that load will do. A pull-to-refresh spinner mustn't be left spinning, though.
            await EndRefreshAsync();
            return;
        }
        try
        {
            if (showBusy) IsBusy = true;
            ErrorMessage = null;
            await action();
            IsOffline = false;
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            // Said once, not on every retry (some pages poll).
            var wasOffline = IsOffline;
            IsOffline = true;
            if (ShowsOfflineState) { if (!wasOffline) await ToastAsync("Can't reach the Phantom server"); }
            else ErrorMessage = "Can't reach the Phantom server. Check your connection.";
        }
        catch (OperationCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            // A request that really ran out of time: the server is down or unreachable, said as being offline is.
            var wasOffline = IsOffline;
            IsOffline = true;
            if (ShowsOfflineState) { if (!wasOffline) await ToastAsync("The Phantom server isn't answering"); }
            else ErrorMessage = "The Phantom server isn't answering. Check your connection and try again.";
        }
        catch (OperationCanceledException)
        {
            // Cancelled on purpose (the filters changed, the page closed, a newer load replaced this one): not an error.
        }
        catch (Exception ex)
        {
            // Anything else (an unexpected reply, a platform hiccup) is shown on the page: commands run from async void
            // code, where an exception that gets away ends the app on iOS.
            System.Diagnostics.Debug.WriteLine(ex);
            ErrorMessage = $"Something went wrong: {ex.Message}";
        }
        finally
        {
            if (showBusy) IsBusy = false;
            HasLoaded = true;
            await EndRefreshAsync();
        }
    }

    protected static Task GoToAsync(string route, IDictionary<string, object>? parameters = null) =>
        parameters is null ? Shell.Current.GoToAsync(route) : Shell.Current.GoToAsync(route, parameters);

    /// <summary>Shows a toast. Best effort: on Windows the toolkit posts an app notification, which throws
    /// ("Element not found") in unpackaged local builds that have no package identity.</summary>
    protected static async Task ToastAsync(string message)
    {
        try
        {
            await CommunityToolkit.Maui.Alerts.Toast.Make(message).Show();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    /// <summary>Opens a row or grid item's page: a movie's, or a show's.</summary>
    [RelayCommand]
    protected static Task OpenMovie(MovieSummaryDto? movie) => movie switch
    {
        null => Task.CompletedTask,
        { Kind: MediaKind.Show } => GoToAsync($"{nameof(Views.ShowDetailPage)}?id={movie.Id}"),
        _ => GoToAsync($"{nameof(Views.MovieDetailPage)}?id={movie.Id}"),
    };
}

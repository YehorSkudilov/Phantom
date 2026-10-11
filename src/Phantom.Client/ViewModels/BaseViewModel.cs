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

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private string title = "";

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
        if (showBusy && IsBusy) return;
        try
        {
            if (showBusy) IsBusy = true;
            ErrorMessage = null;
            await action();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Can't reach the Phantom server. Check your connection.";
        }
        catch (TaskCanceledException)
        {
            ErrorMessage = "The server took too long to respond.";
        }
        finally
        {
            if (showBusy) IsBusy = false;
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

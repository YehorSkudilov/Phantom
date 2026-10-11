using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Client.Services;
using Phantom.Shared;

namespace Phantom.Client.ViewModels;

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
        }
    }

    protected static Task GoToAsync(string route, IDictionary<string, object>? parameters = null) =>
        parameters is null ? Shell.Current.GoToAsync(route) : Shell.Current.GoToAsync(route, parameters);

    protected static Task ToastAsync(string message) =>
        CommunityToolkit.Maui.Alerts.Toast.Make(message).Show();

    [RelayCommand]
    protected static Task OpenMovie(MovieSummaryDto? movie) =>
        movie is null ? Task.CompletedTask : GoToAsync($"{nameof(Views.MovieDetailPage)}?id={movie.Id}");
}

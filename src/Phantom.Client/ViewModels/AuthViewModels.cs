using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Client.Services;
using Phantom.Shared;

namespace Phantom.Client.ViewModels;

public partial class ConnectViewModel(PhantomApi api, SettingsService settings) : BaseViewModel
{
    [ObservableProperty] private string serverUrl = settings.ServerUrl ?? ApiConfig.DefaultServerUrl ?? "http://192.168.1.10:5180";
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool isFirstRun;
    [ObservableProperty] private string? serverName;

    [RelayCommand]
    private Task Connect() => RunAsync(async () =>
    {
        var url = ServerUrl.Trim().TrimEnd('/');
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;
        var info = await api.ProbeAsync(url);
        settings.ServerUrl = ServerUrl = url;
        ServerName = $"{info.Name} · {info.MovieCount} movies";
        IsFirstRun = await api.SetupRequiredAsync();
        IsConnected = true;
    });

    [RelayCommand]
    private Task SignIn() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Enter your username and password.";
            return;
        }
        var auth = IsFirstRun ? await api.RegisterAsync(Username.Trim(), Password) : await api.LoginAsync(Username.Trim(), Password);
        settings.Token = auth.Token;
        settings.User = auth.User;
        Password = "";

        var profiles = await api.ProfilesAsync();
        await GoToAsync(profiles.Count > 1 ? "//profiles" : "//main/home");
    });

    [RelayCommand]
    private void ChangeServer()
    {
        IsConnected = false;
        ErrorMessage = null;
    }
}

/// <summary>"Who's watching?" — Plex Home profile picker.</summary>
public partial class ProfilesViewModel(PhantomApi api, SettingsService settings) : BaseViewModel
{
    public ObservableCollection<UserDto> Profiles { get; } = [];

    [ObservableProperty] private bool canManage;

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        var profiles = await api.ProfilesAsync();
        Profiles.Clear();
        foreach (var p in profiles) Profiles.Add(p);
        CanManage = settings.User is { IsManaged: false };
    });

    [RelayCommand]
    private Task Select(UserDto profile) => RunAsync(async () =>
    {
        string? pin = null;
        var current = settings.User;
        if (current?.Id == profile.Id)
        {
            await GoToAsync("//main/home");
            return;
        }
        var needsSecret = profile.HasPin || (!profile.IsManaged && current?.IsManaged == true);
        if (needsSecret)
        {
            pin = await Shell.Current.DisplayPromptAsync(profile.Username,
                profile.IsManaged ? "Enter PIN" : "Enter the account password",
                keyboard: profile.IsManaged ? Keyboard.Numeric : Keyboard.Default, maxLength: profile.IsManaged ? 8 : 128);
            if (pin is null) return;
        }

        // The server checks the PIN, and requires the owner's password to leave a managed profile.
        var auth = await api.SwitchProfileAsync(profile.Id, pin);
        settings.Token = auth.Token;
        settings.User = auth.User;
        await GoToAsync("//main/home");
    });

    [RelayCommand]
    private Task AddProfile() => RunAsync(async () =>
    {
        var name = await Shell.Current.DisplayPromptAsync("New profile", "Name");
        if (string.IsNullOrWhiteSpace(name)) return;
        var pin = await Shell.Current.DisplayPromptAsync("New profile", "Optional PIN (leave empty for none)", keyboard: Keyboard.Numeric, maxLength: 8);
        var rating = await Shell.Current.DisplayActionSheetAsync("Content restriction", "Cancel", null, "No restriction", "G", "PG", "PG-13", "R");
        if (rating is null or "Cancel") return;

        var colors = new[] { "#8B5CF6", "#22D3EE", "#F472B6", "#34D399", "#FBBF24", "#60A5FA" };
        var created = await api.CreateProfileAsync(new CreateProfileRequest(
            name.Trim(), string.IsNullOrWhiteSpace(pin) ? null : pin,
            rating == "No restriction" ? null : rating, colors[Profiles.Count % colors.Length]));
        Profiles.Add(created);
    });

    [RelayCommand]
    private Task DeleteProfile(UserDto profile) => RunAsync(async () =>
    {
        if (!profile.IsManaged) return;
        if (!await Shell.Current.DisplayAlertAsync("Delete profile", $"Delete {profile.Username} and its watch history?", "Delete", "Cancel")) return;
        await api.DeleteProfileAsync(profile.Id);
        Profiles.Remove(profile);
    });
}

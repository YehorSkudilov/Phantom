using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Services;
using Phantom.Shared;

namespace Phantom.ViewModels;

public partial class ConnectViewModel(PhantomApi api, SettingsService settings) : BaseViewModel
{
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool isFirstRun;
    [ObservableProperty] private string? serverName;

    /// <summary>Reaches the server baked into this build; the page runs it on appearing and on Retry.</summary>
    [RelayCommand]
    public Task Connect() => RunAsync(async () =>
    {
        var info = await api.ProbeAsync(settings.ServerUrl);
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
        await GoToAsync(profiles.Count > 1 ? "//profiles" : "//main");
    });
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
            await GoToAsync("//main");
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
        await GoToAsync("//main");
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

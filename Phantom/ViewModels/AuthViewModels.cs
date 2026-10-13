using Phantom.Controls;
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

    /// <summary>On a TV, sign-in starts with a code to approve from a phone instead of typing on a remote.</summary>
    public bool IsTv => DeviceInfo.Idiom == DeviceIdiom.TV;

    [ObservableProperty] private bool usePairing = DeviceInfo.Idiom == DeviceIdiom.TV;
    [ObservableProperty] private string? pairingCode;
    private CancellationTokenSource? _pairing;

    /// <summary>The username/password form: always on first run (creating the owner), otherwise unless pairing.</summary>
    public bool ShowPasswordForm => IsFirstRun || !UsePairing;

    partial void OnIsConnectedChanged(bool value) => UpdatePairing();
    partial void OnIsFirstRunChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPasswordForm));
        UpdatePairing();
    }
    partial void OnUsePairingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPasswordForm));
        UpdatePairing();
    }

    [RelayCommand]
    private void UseCode() => UsePairing = true;

    [RelayCommand]
    private void UsePassword() => UsePairing = false;

    /// <summary>Called when the page goes away.</summary>
    public void StopPairing()
    {
        _pairing?.Cancel();
        _pairing = null;
    }

    // A code only makes sense once the server is reached and has accounts to sign in to.
    private void UpdatePairing()
    {
        if (UsePairing && IsConnected && !IsFirstRun)
        {
            if (_pairing is null) _ = PairAsync((_pairing = new CancellationTokenSource()).Token);
        }
        else StopPairing();
    }

    /// <summary>Shows a code and waits for it to be approved, with a fresh code whenever one expires.</summary>
    private async Task PairAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var start = await api.StartPairingAsync(DeviceInfo.Name is { Length: > 0 } n ? n : "TV", ct);
                PairingCode = $"{start.Code[..3]} {start.Code[3..]}";
                ErrorMessage = null;
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    if (await api.PollPairingAsync(start.PairingId, ct) is { } auth)
                    {
                        StopPairing();
                        await FinishSignInAsync(auth);
                        return;
                    }
                }
            }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
            {
                // Expired: loop round for a new code.
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "Can't reach the Phantom server. Retrying…";
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

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
        Password = "";
        await FinishSignInAsync(auth);
    });

    private async Task FinishSignInAsync(AuthResponse auth)
    {
        settings.Token = auth.Token;
        settings.User = auth.User;

        var profiles = await api.ProfilesAsync();
        await GoToAsync(profiles.Count > 1 ? "//profiles" : "//main");
    }
}

/// <summary>"Who's watching?" — Plex Home profile picker.</summary>
public partial class ProfilesViewModel(PhantomApi api, SettingsService settings) : BaseViewModel
{
    public ObservableList<UserDto> Profiles { get; } = [];

    [ObservableProperty] private bool canManage;

    /// <summary>The profile in use, which a TV remote starts on.</summary>
    public int? CurrentProfileId => settings.User?.Id;

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        var profiles = await api.ProfilesAsync();
        Profiles.Sync(profiles);
        CanManage = settings.User is { IsManaged: false };
    });

    // Not held up by the list reloading (each time the page appears): a tap then used to do nothing.
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
            pin = await Dialog.PromptAsync(profile.Username,
                profile.IsManaged ? "Enter PIN" : "Enter the account password",
                keyboard: profile.IsManaged ? Keyboard.Numeric : Keyboard.Default, maxLength: profile.IsManaged ? 8 : 128, password: true);
            if (pin is null) return;
        }

        // The server checks the PIN, and requires the owner's password to leave a managed profile.
        var auth = await api.SwitchProfileAsync(profile.Id, pin);
        settings.Token = auth.Token;
        settings.User = auth.User;
        Views.MainPage.ProfileChanged();
        await GoToAsync("//main");
    }, showBusy: false);

    [RelayCommand]
    private Task AddProfile() => RunAsync(async () =>
    {
        var name = await Dialog.PromptAsync("New profile", "Name");
        if (string.IsNullOrWhiteSpace(name)) return;
        var pin = await Dialog.PromptAsync("New profile", "Optional PIN (leave empty for none)", keyboard: Keyboard.Numeric, maxLength: 8);
        var rating = await Dialog.ChooseAsync("Content restriction", null, "No restriction", "G", "PG", "PG-13", "R");
        if (rating is null) return;

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
        if (!await Dialog.ConfirmAsync("Delete profile", $"Delete {profile.Username} and its watch history?", "Delete", destructive: true)) return;
        await api.DeleteProfileAsync(profile.Id);
        Profiles.Remove(profile);
    });
}

/// <summary>The signed-in account: its username (also its default profile's name) and password.</summary>
public partial class AccountViewModel(PhantomApi api, SettingsService settings) : BaseViewModel
{
    // A managed profile shows its account's name once loaded; the default profile is the account itself.
    [ObservableProperty] private string username = settings.User is { IsManaged: false } u ? u.Username : "";
    [ObservableProperty] private string newPassword = "";
    [ObservableProperty] private string confirmPassword = "";
    [ObservableProperty] private string currentPassword = "";

    [RelayCommand]
    private Task Save() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "Username can't be empty.";
            return;
        }
        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "The new passwords don't match.";
            return;
        }
        if (string.IsNullOrEmpty(CurrentPassword))
        {
            ErrorMessage = "Enter your current password to confirm.";
            return;
        }

        var account = await api.UpdateAccountAsync(new UpdateAccountRequest(CurrentPassword, Username.Trim(), string.IsNullOrEmpty(NewPassword) ? null : NewPassword));
        if (settings.User?.Id == account.Id) settings.User = account; // signed in as the default profile
        Username = account.Username;
        NewPassword = ConfirmPassword = CurrentPassword = "";
        await ToastAsync("Account saved");
    });

    /// <summary>On a managed profile, the account's name comes from its default profile.</summary>
    [RelayCommand]
    public Task Load() => Username.Length > 0 ? Task.CompletedTask : RunAsync(async () =>
        Username = (await api.ProfilesAsync()).FirstOrDefault(p => !p.IsManaged)?.Username ?? "");
}

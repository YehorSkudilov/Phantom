using Phantom.Controls;
namespace Phantom.Services;

/// <summary>
/// Keeps the exception that brought the app down, so the next launch can show it (and it can be copied into a bug
/// report): TestFlight and Play crash reports only have native stacks, which never say what the .NET code was doing.
/// </summary>
public static class CrashLog
{
    private static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "last-crash.txt");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception);
#if IOS || MACCATALYST
        // An exception escaping to UIKit (from an async void handler, say) ends the app here, before UnhandledException.
        ObjCRuntime.Runtime.MarshalManagedException += (_, e) => Write(e.Exception);
#elif ANDROID
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Write(e.Exception);
#endif
    }

    public static void Write(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            File.WriteAllText(FilePath, $"Phantom {AppInfo.VersionString} ({AppInfo.BuildString}) · {DeviceInfo.Platform} {DeviceInfo.VersionString} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n{ex}");
        }
        catch (Exception)
        {
            // Nowhere to put it; the crash goes ahead regardless.
        }
    }

    /// <summary>The last crash's report, if there is one (kept until it's been offered: see <see cref="Forget"/>).</summary>
    public static string? Last()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void Forget()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Tells the user the app closed last time, with the whole report on show (it can be selected) and a button that
    /// copies it. After copying it comes back, in case it's wanted again; the report is forgotten once it's closed.
    /// </summary>
    public static async Task OfferLastAsync()
    {
        if (Last() is not { } report) return;
        // After start-up's own navigation (to sign-in, or the tabs), which would otherwise land on top of the pop-up.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        while (await Dialog.ConfirmAsync("Phantom closed unexpectedly",
                   $"Sorry about that. If you're sending feedback, copy the details below and paste them in.\n\n{report}", "Copy details", "Close"))
        {
            try
            {
                // The clipboard is the UI's: written from the main thread.
                await MainThread.InvokeOnMainThreadAsync(() => Clipboard.Default.SetTextAsync(report));
                await Toast("Copied");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CrashLog: couldn't copy: {ex}");
                await Dialog.AlertAsync("Couldn't copy", "Press and hold the text to select it, then copy it from there.");
            }
        }
        Forget();
    }

    private static async Task Toast(string message)
    {
        try
        {
            await CommunityToolkit.Maui.Alerts.Toast.Make(message).Show();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }
}

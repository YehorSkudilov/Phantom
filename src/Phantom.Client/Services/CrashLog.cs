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

    /// <summary>The last crash's report, once: it's removed as it's read.</summary>
    public static string? TakeLast()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var text = File.ReadAllText(FilePath);
            File.Delete(FilePath);
            return text;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Tells the user the app closed last time, with the details to copy into a bug report.</summary>
    public static async Task OfferLastAsync(Page page)
    {
        if (TakeLast() is not { } report) return;
        var summary = report.Split('\n').Skip(2).FirstOrDefault()?.Trim() ?? "";
        if (summary.Length > 300) summary = summary[..300] + "…";
        if (await Dialog.ConfirmAsync("Phantom closed unexpectedly",
                $"Sorry about that. If you're sending feedback, copy the details and paste them in.\n\n{summary}", "Copy details", "Close"))
            await Clipboard.Default.SetTextAsync(report);
    }
}

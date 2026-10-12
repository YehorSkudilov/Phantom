namespace Phantom.Services;

/// <summary>
/// Telling a lost connection from a real error, and whether the app is on screen: requests cut off because the app went
/// to the background, or because there's no network, aren't something to tell the user about in a pop-up.
/// </summary>
public static class NetworkState
{
    /// <summary>The app isn't on screen (minimised, or the phone locked); set by App from its window's lifecycle.</summary>
    public static bool InBackground { get; set; }

    /// <summary>When the app last came back to the front: requests cut off as it went away still fail just after.</summary>
    public static DateTime ResumedAt { get; set; }

    /// <summary>The app is away, or has only just come back (its old requests are still failing).</summary>
    public static bool JustAway => InBackground || DateTime.UtcNow - ResumedAt < TimeSpan.FromSeconds(5);

    /// <summary>
    /// A connection that couldn't be made or was cut off, in any of the forms the platforms raise it: .NET's
    /// (HttpRequestException, IOException, SocketException) and Android's own, which come through as Java exceptions
    /// ("Software caused connection abort" when the app is minimised, "No address associated with hostname" with no
    /// network, SSL handshakes cut short).
    /// </summary>
    public static bool IsConnectionError(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or IOException or System.Net.Sockets.SocketException or System.Net.WebException) return true;
            var name = e.GetType().FullName ?? "";
            if (name.StartsWith("Java.Net.", StringComparison.Ordinal) || name.StartsWith("Java.IO.", StringComparison.Ordinal)
                || name.StartsWith("Javax.Net.Ssl.", StringComparison.Ordinal) || name.StartsWith("Foundation.NSError", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

using System.Globalization;
using System.Text.Json;

namespace Phantom.Api.Services;

/// <summary>
/// Reading numbers from other sites' JSON. <see cref="JsonElement.TryGetInt32(out int)"/> doesn't return false for a
/// <c>null</c> (or a string): it throws, which failed a whole source sync over one torrent with "seeders": null. These
/// return false for anything that isn't a number, and also read numbers sent as strings ("seeds": "40").
/// </summary>
public static class JsonNumbers
{
    public static bool TryGetInt(this JsonElement e, out int value)
    {
        value = 0;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    public static bool TryGetLong(this JsonElement e, out long value)
    {
        value = 0;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }
}

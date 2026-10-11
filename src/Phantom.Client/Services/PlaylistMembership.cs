using Phantom.Controls;
using Phantom.Shared;

namespace Phantom.Services;

/// <summary>
/// Which of the profile's playlists hold which titles, for the ones a page shows: entries are named by key ("m12" movie
/// 12, "s4" show 4, "e99" episode 99). Pages ask for their keys (<see cref="LoadAsync"/>) and their playlist buttons
/// follow <see cref="Changed"/>.
/// </summary>
public class PlaylistMembership(PhantomApi api)
{
    private readonly Dictionary<string, List<string>> _names = [];

    public event EventHandler? Changed;

    /// <summary>The names of the playlists <paramref name="key"/> is in.</summary>
    public IReadOnlyList<string> PlaylistsOf(string key) => _names.TryGetValue(key, out var names) ? names : [];

    /// <summary>The playlists any of <paramref name="keys"/> is in, each once.</summary>
    public IReadOnlyList<string> PlaylistsOfAny(IEnumerable<string> keys) => keys.SelectMany(PlaylistsOf).Distinct().ToList();

    /// <summary>Asks the server where <paramref name="keys"/> are, replacing what was known about them.</summary>
    public async Task<List<PlaylistMembershipDto>> LoadAsync(IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0) return [];
        var found = await api.PlaylistsContainingAsync(keys);
        foreach (var key in keys) _names.Remove(key);
        foreach (var playlist in found)
            foreach (var key in playlist.Keys)
                (_names.TryGetValue(key, out var list) ? list : _names[key] = []).Add(playlist.Name);
        Changed?.Invoke(this, EventArgs.Empty);
        return found;
    }

    /// <summary>
    /// The playlist pop-up: every playlist, with a tick on those that already hold all of <paramref name="keys"/> (or
    /// how many, for a season partly in one). Picking a ticked one takes them out; any other adds them (with
    /// <paramref name="add"/>), and "New playlist" makes one to add them to.
    /// </summary>
    public async Task ChooseAsync(string title, IReadOnlyList<string> keys, Func<int, Task> add)
    {
        var playlists = await api.PlaylistsAsync();
        var inPlaylist = (await LoadAsync(keys)).ToDictionary(p => p.Id, p => p.Keys.Count);

        const string newPlaylist = "+  New playlist";
        var options = new Dictionary<string, PlaylistDto>();
        foreach (var p in playlists)
        {
            var count = inPlaylist.GetValueOrDefault(p.Id);
            var label = count >= keys.Count ? $"✓  {p.Name}" : count > 0 ? $"◐  {p.Name}  ({count} of {keys.Count})" : p.Name;
            options[label] = p;
        }
        var choice = await Dialog.ChooseAsync(title, null, [.. options.Keys, newPlaylist]);
        if (choice is null) return;

        if (choice == newPlaylist)
        {
            var name = await Dialog.PromptAsync("New playlist", "Name", "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            var created = await api.CreatePlaylistAsync(name.Trim());
            await add(created.Id);
            await Toast($"Added to {created.Name}");
        }
        else if (options.TryGetValue(choice, out var target))
        {
            if (inPlaylist.GetValueOrDefault(target.Id) >= keys.Count)
            {
                await api.RemovePlaylistEntriesAsync(target.Id, keys);
                await Toast($"Removed from {target.Name}");
            }
            else
            {
                await add(target.Id);
                await Toast($"Added to {target.Name}");
            }
        }
        await LoadAsync(keys);
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

    /// <summary>"Add to playlist", or where it already is: "In Movie night", "In 3 playlists".</summary>
    public static string Label(IReadOnlyList<string> playlists) => playlists.Count switch
    {
        0 => "Add to playlist",
        1 => $"In {playlists[0]}",
        _ => $"In {playlists.Count} playlists",
    };
}

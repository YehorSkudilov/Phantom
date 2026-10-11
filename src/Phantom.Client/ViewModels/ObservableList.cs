using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Phantom.ViewModels;

/// <summary>
/// An ObservableCollection that updates lists in one go. Filling a list item by item raised a change per item, and
/// clearing and refilling one redrew every row (reloading its poster) even when nothing in it had changed.
/// </summary>
public class ObservableList<T> : ObservableCollection<T>
{
    public ObservableList() { }

    public ObservableList(IEnumerable<T> items) : base(items) { }

    /// <summary>Appends <paramref name="items"/> with a single change notification (the list inserts them as a range).</summary>
    public void AddRange(IEnumerable<T> items)
    {
        CheckReentrancy();
        var added = items.ToList();
        if (added.Count == 0) return;
        var start = Count;
        foreach (var item in added) Items.Add(item);
        RaiseChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, start));
    }

    /// <summary>
    /// Makes the list equal to <paramref name="items"/>, touching only what differs: unchanged rows stay as they are
    /// (no redraw), changed ones are replaced one by one. A list that has little in common with the new one is reset
    /// in one go instead.
    /// </summary>
    public void Sync(IReadOnlyList<T> items, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        if (Count == 0)
        {
            AddRange(items);
            return;
        }

        var common = Math.Min(Count, items.Count);
        var same = 0;
        for (var i = 0; i < common; i++)
            if (comparer.Equals(this[i], items[i])) same++;
        if (same * 2 < common)
        {
            ReplaceAll(items);
            return;
        }

        for (var i = 0; i < common; i++)
            if (!comparer.Equals(this[i], items[i])) this[i] = items[i];
        if (items.Count > Count) AddRange(items.Skip(Count));
        while (Count > items.Count) RemoveAt(Count - 1);
    }

    /// <summary>Swaps the contents for <paramref name="items"/> with a single reset notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        RaiseChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void RaiseChanged(NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(args);
    }
}

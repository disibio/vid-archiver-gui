using System.Collections.ObjectModel;
using System.ComponentModel;

namespace VidArchiverGui.App.ViewModels;

/// <summary>
/// Calls <c>edited</c> on any edit to the lists it watches: items added, removed or moved, or a property of an item
/// changed. Used to withdraw an Undo that would lose or break those edits.
/// </summary>
/// <remarks>Removed items stay watched; nothing edits them once they're out of the list.</remarks>
public sealed class EditWatcher(Action edited)
{
    private readonly HashSet<INotifyPropertyChanged> _watched = [];

    /// <summary>
    /// Watches <paramref name="items"/> and each item in it, including ones added later. <paramref name="watchItem"/>
    /// can watch more of each item, e.g. a list inside it.
    /// </summary>
    public void WatchList<T>(ObservableCollection<T> items, Action<T>? watchItem = null) where T : INotifyPropertyChanged
    {
        void Watch(T item)
        {
            if (_watched.Add(item))
            {
                item.PropertyChanged += (_, _) => edited();
                watchItem?.Invoke(item);
            }
        }

        foreach (var item in items)
        {
            Watch(item);
        }

        items.CollectionChanged += (_, e) =>
        {
            foreach (var item in e.NewItems?.OfType<T>() ?? [])
            {
                Watch(item);
            }

            edited();
        };
    }
}

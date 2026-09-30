using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VidArchiverGui.App.ViewModels;

/// <summary>
/// The last delete or import, shown with an Undo button until it's undone, replaced by the next one, or withdrawn by
/// <see cref="Clear"/> once the list has changed since.
/// </summary>
public partial class UndoSlot : ObservableObject
{
    private Action? _undo;

    /// <summary>What can be undone; null when there's nothing to undo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    private string? _text;

    public bool IsAvailable => Text is not null;

    public void Offer(string text, Action undo)
    {
        _undo = undo;
        Text = text;
    }

    /// <summary>Withdraws the offer, e.g. because a later edit would be lost or broken by undoing.</summary>
    public void Clear()
    {
        _undo = null;
        Text = null;
    }

    [RelayCommand]
    private void Undo()
    {
        var undo = _undo;
        Clear();
        undo?.Invoke();
    }
}

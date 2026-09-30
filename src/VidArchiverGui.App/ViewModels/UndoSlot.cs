using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VidArchiverGui.App.Services;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

/// <summary>The last delete or import, shown with an Undo button until it's undone or replaced by the next one.</summary>
public partial class UndoSlot(AppHost host) : ObservableObject
{
    private Action? _undo;

    // The presets and rules just after an import, to tell at Undo time whether they've been edited since.
    private string? _stateAfter;

    /// <summary>What can be undone; null when there's nothing to undo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    private string? _text;

    public bool IsAvailable => Text is not null;

    /// <summary>
    /// Offers <paramref name="undo"/>. With <paramref name="restoresAll"/> (it puts back all presets and rules as they
    /// were), Undo first asks whether to go ahead if they've been edited since, as those edits would be lost.
    /// </summary>
    public void Offer(string text, Action undo, bool restoresAll = false)
    {
        _undo = undo;
        _stateAfter = restoresAll ? State() : null;
        Text = text;
    }

    private string State() => PresetExchange.Export(host.Settings) + RuleExchange.Export(host.Settings);

    [RelayCommand]
    private async Task Undo()
    {
        if (_stateAfter is not null && State() != _stateAfter
            && !await host.Dialogs.ConfirmAsync("Undo import",
                "You've changed your presets or folder rules since the import. Undoing puts everything back as it was " +
                "before the import, so those changes will be lost.", "Undo anyway", "Keep my changes"))
        {
            return;
        }

        var undo = _undo;
        _undo = null;
        _stateAfter = null;
        Text = null;
        undo?.Invoke();
    }
}

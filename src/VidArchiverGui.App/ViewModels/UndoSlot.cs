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
    private string? _editsLost;

    /// <summary>What can be undone; null when there's nothing to undo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    private string? _text;

    public bool IsAvailable => Text is not null;

    /// <summary>
    /// Offers <paramref name="undo"/>. With <paramref name="editsLost"/> (for an undo that puts a whole list back as it
    /// was), Undo first asks whether to go ahead if presets or rules have been edited since, saying what would be lost.
    /// </summary>
    public void Offer(string text, Action undo, string? editsLost = null)
    {
        _undo = undo;
        _editsLost = editsLost;
        _stateAfter = editsLost is null ? null : State();
        Text = text;
    }

    private string State() => PresetExchange.Export(host.Settings) + RuleExchange.Export(host.Settings);

    [RelayCommand]
    private async Task Undo()
    {
        if (_stateAfter is not null && State() != _stateAfter
            && !await host.Dialogs.ConfirmAsync("Undo import",
                $"You've changed your presets or folder rules since the import. {_editsLost}", "Undo anyway", "Keep my changes"))
        {
            return;
        }

        var undo = _undo;
        _undo = null;
        _stateAfter = null;
        _editsLost = null;
        Text = null;
        undo?.Invoke();
    }
}

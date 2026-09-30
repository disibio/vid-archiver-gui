using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VidArchiverGui.App.Services;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

public sealed record EngineChoice(string? Id, string Name);

public partial class PresetsViewModel : ObservableObject
{
    private readonly AppHost _host;

    public PresetsViewModel(AppHost host)
    {
        _host = host;
        SelectedPreset = host.Settings.DefaultPreset;
    }

    public ObservableCollection<Preset> Presets => _host.Settings.Presets;

    [ObservableProperty] private Preset? _selectedPreset;
    [ObservableProperty] private string _parsedPreview = "";
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private ObservableCollection<EngineChoice> _engineChoices = [];
    [ObservableProperty] private EngineChoice? _selectedEngineChoice;

    public string DefaultPresetText => "Default preset: " + _host.Settings.DefaultPreset.Name;

    partial void OnSelectedPresetChanged(Preset? oldValue, Preset? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnPresetPropertyChanged;
        }
        if (newValue is not null)
        {
            newValue.PropertyChanged += OnPresetPropertyChanged;
        }
        UpdatePreview();
        RefreshEngineChoices();
    }

    partial void OnSelectedEngineChoiceChanged(EngineChoice? value)
    {
        if (SelectedPreset is not null && value is not null)
        {
            SelectedPreset.EngineId = value.Id;
        }
    }

    /// <summary>Called when the tab is shown, since downloaders may have been added or the default changed.</summary>
    public void RefreshEngineChoices()
    {
        var choices = new ObservableCollection<EngineChoice> { new(null, $"Default downloader ({_host.Settings.DefaultEngine.Name})") };
        foreach (var e in _host.Settings.Engines)
        {
            choices.Add(new EngineChoice(e.Id, e.Name));
        }

        EngineChoices = choices;
        SelectedEngineChoice = choices.FirstOrDefault(c => c.Id == SelectedPreset?.EngineId) ?? choices[0];
    }

    private void OnPresetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Preset.Arguments))
        {
            UpdatePreview();
        }

        if (e.PropertyName == nameof(Preset.Name))
        {
            OnPropertyChanged(nameof(DefaultPresetText));
        }
    }

    private void UpdatePreview()
    {
        var args = ArgumentParser.Split(SelectedPreset?.Arguments);
        ParsedPreview = args.Count == 0 ? "(no arguments)" : string.Join(Environment.NewLine, args.Select(a => a.StartsWith('-') ? a : "    " + a));
        Warning = args.Any(a => a is "-P" or "--paths" || a.StartsWith("--paths="))
            ? "Note: -P/--paths in a preset is overridden by the folder chosen by your routing rules."
            : ArgumentParser.HasCookieOptions(args)
                ? "Note: this preset sends cookies on every download. The Cookies list on the Downloads tab is safer (per download), and replaces these when used."
                : "";
    }

    [RelayCommand]
    private void AddPreset()
    {
        var p = new Preset();
        Presets.Add(p);
        SelectedPreset = p;
    }

    /// <summary>The presets a new install starts with, offered under the Add button's arrow so they can be re-added.</summary>
    public IReadOnlyList<Preset> BuiltInPresets { get; } = SettingsStore.CreateDefaults().Presets;

    [RelayCommand]
    private void AddBuiltInPreset(Preset template)
    {
        var name = Presets.Any(p => p.Name == template.Name) ? template.Name + " (copy)" : template.Name;
        var p = new Preset { Name = name, Arguments = template.Arguments };
        Presets.Add(p);
        SelectedPreset = p;
        _host.SetStatus($"Added preset \"{name}\".");
    }

    [RelayCommand]
    private void DuplicatePreset()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        var copy = SelectedPreset.Clone();
        Presets.Insert(Presets.IndexOf(SelectedPreset) + 1, copy);
        SelectedPreset = copy;
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is null || Presets.Count <= 1)
        {
            _host.SetStatus("At least one preset is required.");
            return;
        }
        var deleted = SelectedPreset;
        var index = Presets.IndexOf(deleted);
        var wasDefault = _host.Settings.DefaultPresetId == deleted.Id;
        var rules = _host.Settings.Rules.Where(r => r.PresetId == deleted.Id).ToList();
        // Put it back where it was, as the default and on its rules if it was before.
        OfferUndo($"Deleted \"{deleted.Name}\"" + (rules.Count > 0 ? $" (used by {rules.Count} folder rule{(rules.Count == 1 ? "" : "s")})." : "."), () =>
        {
            Presets.Insert(Math.Min(index, Presets.Count), deleted);
            if (wasDefault)
            {
                _host.Settings.DefaultPresetId = deleted.Id;
            }

            foreach (var rule in rules.Where(r => r.PresetId is null && _host.Settings.Rules.Contains(r)))
            {
                rule.PresetId = deleted.Id;
            }

            SelectedPreset = deleted;
            OnPropertyChanged(nameof(DefaultPresetText));
            _host.SetStatus($"Restored preset \"{deleted.Name}\".");
        });

        Presets.Remove(deleted);
        if (wasDefault)
        {
            _host.Settings.DefaultPresetId = Presets[0].Id;
        }

        foreach (var rule in rules)
        {
            rule.PresetId = null;
        }

        SelectedPreset = Presets[Math.Min(index, Presets.Count - 1)];
        OnPropertyChanged(nameof(DefaultPresetText));
    }

    private Action? _undo;

    /// <summary>Shown with an Undo button after a delete or import; null when there's nothing to undo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    private string? _undoText;

    public bool CanUndo => UndoText is not null;

    private void OfferUndo(string text, Action undo)
    {
        _undo = undo;
        UndoText = text;
    }

    [RelayCommand]
    private void Undo()
    {
        var undo = _undo;
        _undo = null;
        UndoText = null;
        undo?.Invoke();
    }

    [RelayCommand]
    private async Task Export()
    {
        if (await _host.Dialogs.SaveJsonFileAsync("Export presets", $"presets-{DateTime.Now:yyyy-MM-dd}.json") is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, PresetExchange.Export(_host.Settings));
            _host.SetStatus($"Exported {Presets.Count} preset(s) to {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _host.SetStatus("Could not export presets: " + e.Message);
        }
    }

    [RelayCommand]
    private async Task Import()
    {
        if (await _host.Dialogs.PickJsonFileAsync("Import presets") is not { } path)
        {
            return;
        }

        ImportedPresets imported;
        try
        {
            imported = PresetExchange.Import(await File.ReadAllTextAsync(path), _host.Settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            _host.SetStatus("Could not import presets: " + e.Message);
            return;
        }

        var settings = _host.Settings;
        var count = imported.Presets.Count;
        var replace = await _host.Dialogs.ConfirmAsync("Import presets",
            $"The file has {count} preset(s). Replace your {Presets.Count} current preset(s) and the default, or add the new presets below yours?",
            "Replace", "Add below mine");

        // Everything an import can change, so Undo can put it all back.
        var before = Presets.ToList();
        var valuesBefore = before.Select(p => (p, p.Name, p.Arguments, p.EngineId)).ToList();
        var defaultBefore = settings.DefaultPresetId;
        var rulePresetsBefore = settings.Rules.Select(r => (r, r.PresetId)).ToList();

        List<Preset> added;
        if (replace)
        {
            // A preset with the same name as one of yours updates yours in place, so folder rules and
            // waiting downloads that use it keep pointing at it.
            added = [];
            foreach (var p in imported.Presets)
            {
                var existing = before.FirstOrDefault(e => !added.Contains(e) && string.Equals(e.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.Name = p.Name;
                    existing.Arguments = p.Arguments;
                    existing.EngineId = p.EngineId;
                }

                added.Add(existing ?? p);
            }

            Presets.Clear();
            foreach (var p in added)
            {
                Presets.Add(p);
            }

            settings.DefaultPresetId = (added.FirstOrDefault(p => string.Equals(p.Name, imported.DefaultPresetName, StringComparison.OrdinalIgnoreCase)) ?? added[0]).Id;
            foreach (var rule in settings.Rules.Where(r => r.PresetId is not null && settings.FindPreset(r.PresetId) is null))
            {
                rule.PresetId = null;
            }
        }
        else
        {
            added = imported.Presets;
            foreach (var p in added)
            {
                p.Name = PresetExchange.UniqueName(p.Name, Presets);
                Presets.Add(p);
            }
        }

        SelectedPreset = added[0];
        OnPropertyChanged(nameof(DefaultPresetText));
        OfferUndo((replace ? $"Replaced your presets with {count} imported preset(s)." : $"Added {count} imported preset(s).") +
            " Click Save presets to keep them.", () =>
        {
            foreach (var (p, name, arguments, engineId) in valuesBefore)
            {
                p.Name = name;
                p.Arguments = arguments;
                p.EngineId = engineId;
            }

            Presets.Clear();
            foreach (var p in before)
            {
                Presets.Add(p);
            }

            settings.DefaultPresetId = defaultBefore;
            foreach (var (rule, presetId) in rulePresetsBefore)
            {
                rule.PresetId = presetId;
            }

            SelectedPreset = settings.DefaultPreset;
            OnPropertyChanged(nameof(DefaultPresetText));
            _host.SetStatus("Undid the import.");
        });
        _host.SetStatus(imported.Warnings.Count == 0
            ? $"Imported {count} preset(s) from {path}"
            : $"Imported {count} preset(s). " + string.Join(" ", imported.Warnings));
    }

    [RelayCommand]
    private void SetDefault()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        _host.Settings.DefaultPresetId = SelectedPreset.Id;
        OnPropertyChanged(nameof(DefaultPresetText));
    }

    [RelayCommand]
    private void Save() => _host.Save();
}

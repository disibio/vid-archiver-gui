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
        _undoDelete = new DeletedPreset(deleted, index, wasDefault, rules);
        UndoText = $"Deleted \"{deleted.Name}\"" + (rules.Count > 0 ? $" (used by {rules.Count} folder rule{(rules.Count == 1 ? "" : "s")})." : ".");

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

    private sealed record DeletedPreset(Preset Preset, int Index, bool WasDefault, List<RoutingRule> Rules);
    private DeletedPreset? _undoDelete;

    /// <summary>Shown with an Undo button after a delete; null when there's nothing to undo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoDelete))]
    private string? _undoText;

    public bool CanUndoDelete => UndoText is not null;

    /// <summary>Puts the last deleted preset back where it was, as the default and on its rules if it was before.</summary>
    [RelayCommand]
    private void UndoDelete()
    {
        if (_undoDelete is not { } d)
        {
            return;
        }

        Presets.Insert(Math.Min(d.Index, Presets.Count), d.Preset);
        if (d.WasDefault)
        {
            _host.Settings.DefaultPresetId = d.Preset.Id;
        }

        foreach (var rule in d.Rules.Where(r => r.PresetId is null && _host.Settings.Rules.Contains(r)))
        {
            rule.PresetId = d.Preset.Id;
        }

        _undoDelete = null;
        UndoText = null;
        SelectedPreset = d.Preset;
        OnPropertyChanged(nameof(DefaultPresetText));
        _host.SetStatus($"Restored preset \"{d.Preset.Name}\".");
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

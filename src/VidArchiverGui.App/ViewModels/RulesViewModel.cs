using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VidArchiverGui.App.Services;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

public sealed record PresetChoice(string? Id, string Name);

public partial class RulesViewModel : ObservableObject
{
    private readonly AppHost _host;

    public RulesViewModel(AppHost host)
    {
        _host = host;
        _selectedRule = host.Settings.Rules.FirstOrDefault();
        RefreshPresetChoices();
    }

    public static MatchField[] Fields { get; } = Enum.GetValues<MatchField>();
    public static MatchOperator[] Operators { get; } = Enum.GetValues<MatchOperator>();
    public static MatchMode[] MatchModes { get; } = Enum.GetValues<MatchMode>();

    public static string TokenHelp { get; } =
        PathTemplate.Help + "  Use Test a URL below to see a link's field values.";

    public AppSettings Settings => _host.Settings;
    public ObservableCollection<RoutingRule> Rules => _host.Settings.Rules;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RoutingRule? _selectedRule;

    [ObservableProperty] private ObservableCollection<PresetChoice> _presetChoices = [];
    [ObservableProperty] private PresetChoice? _selectedPresetChoice;
    [ObservableProperty] private ObservableCollection<CookieChoice> _cookieChoices = [];
    [ObservableProperty] private CookieChoice? _selectedCookieChoice;
    [ObservableProperty] private string _testUrl = "";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _isTesting;

    public bool HasSelection => SelectedRule is not null;

    partial void OnSelectedRuleChanged(RoutingRule? value) => RefreshPresetChoices();

    partial void OnSelectedPresetChoiceChanged(PresetChoice? value)
    {
        if (SelectedRule is not null && value is not null)
        {
            SelectedRule.PresetId = value.Id;
        }
    }

    partial void OnSelectedCookieChoiceChanged(CookieChoice? value)
    {
        if (SelectedRule is not null && value is not null)
        {
            SelectedRule.CookieId = value.Id;
        }
    }

    /// <summary>Called when the tab is shown, since presets may have been renamed or added meanwhile.</summary>
    public void RefreshPresetChoices()
    {
        var choices = new ObservableCollection<PresetChoice> { new(null, "(keep the preset chosen when adding)") };
        foreach (var p in _host.Settings.Presets)
        {
            choices.Add(new PresetChoice(p.Id, p.Name));
        }

        PresetChoices = choices;
        SelectedPresetChoice = choices.FirstOrDefault(c => c.Id == SelectedRule?.PresetId) ?? choices[0];

        var cookies = new ObservableCollection<CookieChoice>(
            [new CookieChoice(null, "(keep the cookies chosen when adding)"), .. Cookies.Choices(_host.Settings, Cookies.DetectBrowsers())]);
        CookieChoices = cookies;
        SelectedCookieChoice = cookies.FirstOrDefault(c => c.Id == SelectedRule?.CookieId) ?? cookies[0];
    }

    [RelayCommand]
    private void AddRule()
    {
        var rule = new RoutingRule { Conditions = [new RuleCondition()] };
        Rules.Add(rule);
        SelectedRule = rule;
    }

    [RelayCommand]
    private void DuplicateRule()
    {
        if (SelectedRule is null)
        {
            return;
        }

        var copy = SelectedRule.Clone();
        Rules.Insert(Rules.IndexOf(SelectedRule) + 1, copy);
        SelectedRule = copy;
    }

    [RelayCommand]
    private void DeleteRule()
    {
        if (SelectedRule is null)
        {
            return;
        }

        var deleted = SelectedRule;
        var index = Rules.IndexOf(deleted);
        // Put it back at its old position, which matters: the first match wins.
        LastChange.Offer($"Deleted \"{deleted.Name}\".", () =>
        {
            Rules.Insert(Math.Min(index, Rules.Count), deleted);
            SelectedRule = deleted;
            _host.SetStatus($"Restored rule \"{deleted.Name}\".");
        });
        Rules.RemoveAt(index);
        SelectedRule = Rules.Count == 0 ? null : Rules[Math.Min(index, Rules.Count - 1)];
    }

    /// <summary>Shown with an Undo button after a delete or import.</summary>
    public UndoSlot LastChange { get; } = new();

    [RelayCommand]
    private async Task Export()
    {
        if (await _host.Dialogs.SaveJsonFileAsync("Export folder rules", $"folder-rules-{DateTime.Now:yyyy-MM-dd}.json") is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, RuleExchange.Export(Settings));
            _host.SetStatus($"Exported {Rules.Count} rule(s) to {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _host.SetStatus("Could not export rules: " + e.Message);
        }
    }

    [RelayCommand]
    private async Task Import()
    {
        if (await _host.Dialogs.PickJsonFileAsync("Import folder rules") is not { } path)
        {
            return;
        }

        ImportedRules imported;
        try
        {
            imported = RuleExchange.Import(await File.ReadAllTextAsync(path), Settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            _host.SetStatus("Could not import rules: " + e.Message);
            return;
        }

        var count = imported.Rules.Count;
        var replace = Rules.Count == 0 || await _host.Dialogs.ConfirmAsync("Import folder rules",
            $"The file has {count} rule(s). Replace your {Rules.Count} current rule(s) and the fallback folder, or add the new rules below yours?",
            "Replace", "Add below mine");

        var before = Rules.ToList();
        var fallbackBefore = Settings.FallbackDestination;
        if (replace)
        {
            Rules.Clear();
            if (imported.FallbackDestination is { } fallback)
            {
                Settings.FallbackDestination = fallback;
            }
        }

        foreach (var rule in imported.Rules)
        {
            Rules.Add(rule);
        }

        SelectedRule = imported.Rules.FirstOrDefault() ?? Rules.FirstOrDefault();
        LastChange.Offer((replace ? $"Replaced your rules with {count} imported rule(s)." : $"Added {count} imported rule(s).") +
            " Click Save rules to keep them.", () =>
        {
            Rules.Clear();
            foreach (var rule in before)
            {
                Rules.Add(rule);
            }

            Settings.FallbackDestination = fallbackBefore;
            SelectedRule = Rules.FirstOrDefault();
            _host.SetStatus("Undid the import.");
        });
        _host.SetStatus(imported.Warnings.Count == 0
            ? $"Imported {count} rule(s) from {path}"
            : $"Imported {count} rule(s). " + string.Join(" ", imported.Warnings));
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        if (SelectedRule is null)
        {
            return;
        }

        var rule = SelectedRule;
        var index = Rules.IndexOf(rule);
        var target = index + delta;
        if (target < 0 || target >= Rules.Count)
        {
            return;
        }

        Rules.Move(index, target);
        SelectedRule = rule;
    }

    [RelayCommand]
    private void AddCondition() => SelectedRule?.Conditions.Add(new RuleCondition());

    [RelayCommand]
    private void RemoveCondition(RuleCondition condition) => SelectedRule?.Conditions.Remove(condition);

    [RelayCommand]
    private async Task BrowseDestination()
    {
        if (SelectedRule is null)
        {
            return;
        }

        if (await _host.Dialogs.PickFolderAsync("Destination for this rule", SelectedRule.Destination) is { } path)
        {
            SelectedRule.Destination = path;
        }
    }

    [RelayCommand]
    private async Task BrowseFallback()
    {
        if (await _host.Dialogs.PickFolderAsync("Fallback destination", Settings.FallbackDestination) is { } path)
        {
            Settings.FallbackDestination = path;
        }
    }

    [RelayCommand]
    private void Save() => _host.Save();

    [RelayCommand]
    private async Task Test()
    {
        if (string.IsNullOrWhiteSpace(TestUrl))
        {
            return;
        }

        IsTesting = true;
        TestResult = "Reading info…";
        try
        {
            var preset = Settings.DefaultPreset;
            var info = await _host.Metadata.FetchAsync(TestUrl.Trim(), ArgumentParser.Split(preset.Arguments), _host.Tools.ResolveFor(preset));
            var route = RoutingEngine.Resolve(info, Rules, Settings.FallbackDestination);
            string F(string name) => info.Fields.TryGetValue(name, out var v) ? v : "—";
            TestResult =
                $"site: {info.Site}   domain: {info.Domain}   playlist: {info.Playlist ?? "—"}   title: {info.Title}\n" +
                $"channel: {F("channel")}   channel_id: {F("channel_id")}   uploader: {F("uploader")}   uploader_id: {F("uploader_id")}\n" +
                $"{route.Describe()}\n→ {route.Destination}" +
                (Settings.FindPreset(route.PresetId) is { } p ? $"\nPreset: {p.Name}" : "") +
                (route.CookieId is { } c ? $"\nCookies: {Cookies.DisplayName(Settings, c)}" : "");
            if (route.Rule is not null)
            {
                SelectedRule = route.Rule;
            }
        }
        catch (Exception e)
        {
            TestResult = "Error: " + e.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }
}

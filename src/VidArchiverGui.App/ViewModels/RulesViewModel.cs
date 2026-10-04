using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VidArchiverGui.App.Services;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

public partial class RulesViewModel : ObservableObject
{
    private readonly AppHost _host;

    public RulesViewModel(AppHost host)
    {
        _host = host;
        LastChange = new UndoSlot(host);
        _selectedRule = host.Settings.Rules.FirstOrDefault();
        RefreshChoices();
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

    [ObservableProperty] private ObservableCollection<Choice> _presetChoices = [];
    [ObservableProperty] private Choice? _selectedPresetChoice;
    [ObservableProperty] private ObservableCollection<Choice> _cookieChoices = [];
    [ObservableProperty] private Choice? _selectedCookieChoice;
    [ObservableProperty] private string _testUrl = "";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _isTesting;

    public bool HasSelection => SelectedRule is not null;

    partial void OnSelectedRuleChanged(RoutingRule? value) => SelectRuleChoices();

    partial void OnSelectedPresetChoiceChanged(Choice? value)
    {
        if (SelectedRule is not null && value is not null)
        {
            SelectedRule.PresetId = value.Id;
        }
    }

    partial void OnSelectedCookieChoiceChanged(Choice? value)
    {
        if (SelectedRule is not null && value is not null)
        {
            SelectedRule.CookieId = value.Id;
        }
    }

    /// <summary>
    /// Rebuilds the preset and cookie lists. Called when the tab is shown, since presets, cookie sources and installed
    /// browsers may have changed meanwhile.
    /// </summary>
    public void RefreshChoices()
    {
        BuildChoices();
        SelectRuleChoices();
    }

    private void BuildChoices()
    {
        PresetChoices = [new Choice(null, "(keep the preset chosen when adding)"), .. _host.Settings.Presets.Select(p => new Choice(p.Id, p.Name))];
        CookieChoices =
        [
            new Choice(null, "(keep the cookies chosen when adding)"),
            .. Cookies.Choices(_host.Settings, Cookies.DetectBrowsers(), Rules.Select(r => r.CookieId)),
        ];
    }

    /// <summary>Shows the selected rule's preset and cookies in the drop-downs.</summary>
    private void SelectRuleChoices()
    {
        // Picking an entry writes it to the rule, so a choice the lists don't have (e.g. from rules imported since
        // they were built) would be replaced by the first entry. Rebuild them to include it.
        if (SelectedRule is { } rule
            && (PresetChoices.All(c => c.Id != rule.PresetId) || CookieChoices.All(c => c.Id != rule.CookieId)))
        {
            BuildChoices();
        }

        SelectedPresetChoice = PresetChoices.FirstOrDefault(c => c.Id == SelectedRule?.PresetId) ?? PresetChoices[0];
        SelectedCookieChoice = CookieChoices.FirstOrDefault(c => c.Id == SelectedRule?.CookieId) ?? CookieChoices[0];
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
        Rules.RemoveAt(index);
        SelectedRule = Rules.Count == 0 ? null : Rules[Math.Min(index, Rules.Count - 1)];

        // Put it back at its old position, which matters: the first match wins.
        LastChange.Offer($"Deleted \"{deleted.Name}\".", () =>
        {
            Rules.Insert(Math.Min(index, Rules.Count), deleted);
            SelectedRule = deleted;
            _host.SetStatus($"Restored rule \"{deleted.Name}\".");
        });
    }

    /// <summary>Shown with an Undo button after a delete or import.</summary>
    public UndoSlot LastChange { get; }

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
        var hadRules = Rules.Count > 0;
        bool? replace;
        if (hadRules)
        {
            replace = await _host.Dialogs.AskAsync("Import folder rules",
                $"The file has {count} rule(s). Replace your {Rules.Count} current rule(s) and the fallback folder, or add the new rules below yours?",
                "Replace", "Add below mine");
        }
        else if (imported.FallbackDestination is { } fallback && fallback != Settings.FallbackDestination)
        {
            // No rules to replace, but the fallback folder is the user's own: take the file's only if they say so.
            replace = await _host.Dialogs.AskAsync("Import folder rules",
                $"The file also has a fallback folder, for links no rule matches:{Environment.NewLine}{fallback}{Environment.NewLine}{Environment.NewLine}" +
                $"Use it instead of yours?{Environment.NewLine}{Settings.FallbackDestination}",
                "Use the file's", "Keep mine");
        }
        else
        {
            replace = false;
        }

        if (replace is null)
        {
            return;
        }

        var before = new RulesSnapshot(Settings);
        if (replace.Value)
        {
            RuleExchange.Replace(Settings, imported);
        }
        else
        {
            RuleExchange.Append(Settings, imported);
        }

        SelectedRule = imported.Rules.FirstOrDefault() ?? Rules.FirstOrDefault();
        var done = (hadRules, replace.Value) switch
        {
            (true, true) => $"Replaced your rules with {count} imported rule(s).",
            (false, true) => $"Added {count} imported rule(s) and the file's fallback folder.",
            _ => $"Added {count} imported rule(s).",
        };
        LastChange.Offer(done, () =>
        {
            before.Restore();
            SelectedRule = Rules.FirstOrDefault();
            _host.SetStatus("Undid the import.");
        }, editsLost: "Undoing puts your list of folder rules and the fallback folder back as they were before the " +
           "import: rules added since are removed, rules deleted since come back, and a new fallback folder is lost.");
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
            var info = await MetadataService.FetchAsync(TestUrl.Trim(), ArgumentParser.Split(preset.Arguments), ToolManager.Resolve(Settings.DownloaderFor(preset)),
                proxy: Settings.Proxy);
            var route = Router.Resolve(info, Rules, Settings.FallbackDestination, Settings.AsciiNames);
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

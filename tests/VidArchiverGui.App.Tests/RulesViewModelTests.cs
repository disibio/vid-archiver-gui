using VidArchiverGui.App.ViewModels;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.Tests;

public sealed class RulesViewModelTests : IDisposable
{
    private readonly TestHost _t = new();

    public void Dispose() => _t.Dispose();

    private static string RulesFile(string fallback, params string[] names) =>
        $$"""
        {
          "Format": "vid-archiver-gui/folder-rules", "Version": 1, "FallbackDestination": {{System.Text.Json.JsonSerializer.Serialize(fallback)}},
          "Rules": [{{string.Join(", ", names.Select(n => $$"""{ "Name": "{{n}}", "Destination": "/d" }"""))}}]
        }
        """;

    private RulesViewModel WithRules(params string[] names)
    {
        _t.Settings.Rules.Clear();
        foreach (var name in names)
        {
            _t.Settings.Rules.Add(new RoutingRule { Name = name });
        }

        return new RulesViewModel(_t.Host);
    }

    [Fact]
    public void Undoing_a_delete_puts_the_rule_back_at_its_old_position()
    {
        var vm = WithRules("A", "B", "C");
        var b = _t.Settings.Rules[1];

        vm.SelectedRule = b;
        vm.DeleteRuleCommand.Execute(null);
        Assert.Equal(["A", "C"], _t.Settings.Rules.Select(r => r.Name));

        vm.LastChange.UndoCommand.Execute(null);

        Assert.Equal(["A", "B", "C"], _t.Settings.Rules.Select(r => r.Name));
        Assert.Same(b, vm.SelectedRule);
    }

    [Fact]
    public async Task Import_can_replace_the_rules_and_fallback_and_be_undone()
    {
        var vm = WithRules("A", "B");
        var fallback = _t.Settings.FallbackDestination;
        _t.PickFile(RulesFile("/other", "New"));
        _t.Dialogs.Answers.Enqueue(true); // Replace

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(["New"], _t.Settings.Rules.Select(r => r.Name));
        Assert.Equal("/other", _t.Settings.FallbackDestination);

        await vm.LastChange.UndoCommand.ExecuteAsync(null);

        Assert.Equal(["A", "B"], _t.Settings.Rules.Select(r => r.Name));
        Assert.Equal(fallback, _t.Settings.FallbackDestination);
    }

    [Fact]
    public async Task Import_can_add_below_the_existing_rules_keeping_the_fallback()
    {
        var vm = WithRules("A");
        var fallback = _t.Settings.FallbackDestination;
        _t.PickFile(RulesFile("/other", "New"));
        _t.Dialogs.Answers.Enqueue(false); // Add below mine

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(["A", "New"], _t.Settings.Rules.Select(r => r.Name));
        Assert.Equal(fallback, _t.Settings.FallbackDestination);
    }

    [Theory]
    [InlineData(true, "/other")]
    [InlineData(false, null)]
    public async Task With_no_rules_yet_import_asks_only_about_the_fallback_folder(bool useFiles, string? expectedFallback)
    {
        var vm = WithRules();
        var fallback = _t.Settings.FallbackDestination;
        _t.PickFile(RulesFile("/other", "New"));
        _t.Dialogs.Answers.Enqueue(useFiles);

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Contains("fallback folder", Assert.Single(_t.Dialogs.Questions));
        Assert.Equal(["New"], _t.Settings.Rules.Select(r => r.Name));
        Assert.Equal(expectedFallback ?? fallback, _t.Settings.FallbackDestination);
    }

    [Fact]
    public async Task Undoing_an_import_asks_first_if_the_rules_were_edited_since()
    {
        var vm = WithRules("A");
        _t.PickFile(RulesFile("/other", "New"));
        _t.Dialogs.Answers.Enqueue(false); // Add below mine
        await vm.ImportCommand.ExecuteAsync(null);

        _t.Settings.Rules.Add(new RoutingRule { Name = "Added since" });
        _t.Dialogs.Answers.Enqueue(false); // Keep my changes
        await vm.LastChange.UndoCommand.ExecuteAsync(null);

        Assert.Contains("rules added since are removed", _t.Dialogs.Questions[^1]);
        Assert.Equal(["A", "New", "Added since"], _t.Settings.Rules.Select(r => r.Name));
        Assert.True(vm.LastChange.IsAvailable);
    }

    [Fact]
    public async Task Undoing_an_import_clears_a_preset_deleted_since()
    {
        var preset = _t.Settings.Presets[1];
        var vm = WithRules("A");
        _t.Settings.Rules[0].PresetId = preset.Id;
        _t.PickFile(RulesFile("/other", "New"));
        _t.Dialogs.Answers.Enqueue(true); // Replace
        await vm.ImportCommand.ExecuteAsync(null);

        _t.Settings.Presets.Remove(preset);
        _t.Dialogs.Answers.Enqueue(true); // Undo anyway
        await vm.LastChange.UndoCommand.ExecuteAsync(null);

        Assert.Equal("A", _t.Settings.Rules[0].Name);
        Assert.Null(_t.Settings.Rules[0].PresetId);
    }

    [Fact]
    public void Selecting_a_rule_keeps_its_browser_even_if_it_is_not_installed_here()
    {
        var missing = Cookies.BrowserId("not-a-real-browser");
        var vm = WithRules("A", "B");
        _t.Settings.Rules[1].CookieId = missing; // e.g. imported after the lists were built

        vm.SelectedRule = _t.Settings.Rules[1];

        Assert.Equal(missing, _t.Settings.Rules[1].CookieId);
        Assert.Equal(missing, vm.SelectedCookieChoice?.Id);
    }
}

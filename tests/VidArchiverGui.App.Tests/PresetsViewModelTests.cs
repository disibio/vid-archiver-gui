using VidArchiverGui.App.ViewModels;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.Tests;

public sealed class PresetsViewModelTests : IDisposable
{
    private readonly TestHost _t = new();
    private readonly PresetsViewModel _vm;

    public PresetsViewModelTests() => _vm = new PresetsViewModel(_t.Host);

    public void Dispose() => _t.Dispose();

    private static string PresetsFile(params string[] names) =>
        $$"""{ "Format": "vid-archiver-gui/presets", "Version": 1, "Presets": [{{string.Join(", ", names.Select(n => $$"""{ "Name": "{{n}}", "Arguments": "-x" }"""))}}] }""";

    [Fact]
    public void Undoing_a_delete_puts_the_preset_back_where_it_was_as_the_default_and_on_its_rules()
    {
        var presets = _t.Settings.Presets.ToList();
        var deleted = _t.Settings.DefaultPreset;
        var rule = new RoutingRule { PresetId = deleted.Id };
        _t.Settings.Rules.Add(rule);

        _vm.SelectedPreset = deleted;
        _vm.DeletePresetCommand.Execute(null);

        Assert.DoesNotContain(deleted, _t.Settings.Presets);
        Assert.NotEqual(deleted.Id, _t.Settings.DefaultPresetId);
        Assert.Null(rule.PresetId);
        Assert.True(_vm.LastChange.IsAvailable);

        _vm.LastChange.UndoCommand.Execute(null);

        Assert.Equal(presets, _t.Settings.Presets);
        Assert.Equal(deleted.Id, _t.Settings.DefaultPresetId);
        Assert.Equal(deleted.Id, rule.PresetId);
        Assert.Same(deleted, _vm.SelectedPreset);
        Assert.False(_vm.LastChange.IsAvailable);
        Assert.Empty(_t.Dialogs.Questions);
    }

    [Fact]
    public void Undoing_a_delete_leaves_a_rule_that_was_given_another_preset_since()
    {
        var deleted = _t.Settings.Presets[1];
        var other = _t.Settings.Presets[2];
        var rule = new RoutingRule { PresetId = deleted.Id };
        _t.Settings.Rules.Add(rule);

        _vm.SelectedPreset = deleted;
        _vm.DeletePresetCommand.Execute(null);
        rule.PresetId = other.Id;
        _vm.LastChange.UndoCommand.Execute(null);

        Assert.Contains(deleted, _t.Settings.Presets);
        Assert.Equal(other.Id, rule.PresetId);
    }

    [Fact]
    public void The_last_preset_cannot_be_deleted()
    {
        while (_t.Settings.Presets.Count > 1)
        {
            _t.Settings.Presets.RemoveAt(1);
        }

        _vm.SelectedPreset = _t.Settings.Presets[0];
        _vm.DeletePresetCommand.Execute(null);

        Assert.Single(_t.Settings.Presets);
        Assert.False(_vm.LastChange.IsAvailable);
    }

    [Fact]
    public async Task Import_can_replace_the_presets_and_be_undone()
    {
        var presets = _t.Settings.Presets.ToList();
        var defaultId = _t.Settings.DefaultPresetId;
        _t.PickFile(PresetsFile("One", "Two"));
        _t.Dialogs.Answers.Enqueue(true); // Replace

        await _vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(["One", "Two"], _t.Settings.Presets.Select(p => p.Name));
        Assert.Equal("One", _vm.SelectedPreset?.Name);

        await _vm.LastChange.UndoCommand.ExecuteAsync(null);

        Assert.Equal(presets, _t.Settings.Presets);
        Assert.Equal(defaultId, _t.Settings.DefaultPresetId);
        Assert.Single(_t.Dialogs.Questions); // no "changed since" question: nothing was edited
    }

    [Fact]
    public async Task Import_can_add_below_the_existing_presets()
    {
        var count = _t.Settings.Presets.Count;
        _t.PickFile(PresetsFile("One"));
        _t.Dialogs.Answers.Enqueue(false); // Add below mine

        await _vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(count + 1, _t.Settings.Presets.Count);
        Assert.Equal("One", _t.Settings.Presets[^1].Name);
    }

    [Fact]
    public async Task Cancelling_an_import_changes_nothing()
    {
        var presets = _t.Settings.Presets.ToList();
        _t.PickFile(PresetsFile("One"));
        _t.Dialogs.Answers.Enqueue(null); // Cancel

        await _vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(presets, _t.Settings.Presets);
        Assert.False(_vm.LastChange.IsAvailable);
    }

    [Fact]
    public async Task A_file_that_is_not_a_presets_export_is_reported_and_changes_nothing()
    {
        var presets = _t.Settings.Presets.ToList();
        _t.PickFile("""{ "Format": "vid-archiver-gui/folder-rules", "Version": 1 }""");

        await _vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(presets, _t.Settings.Presets);
        Assert.StartsWith("Could not import presets", _t.Statuses[^1]);
        Assert.Empty(_t.Dialogs.Questions);
    }

    [Theory]
    [InlineData(false, "One")]  // Keep my changes
    [InlineData(true, null)]    // Undo anyway
    public async Task Undoing_an_import_asks_first_if_the_presets_were_edited_since(bool undoAnyway, string? expectedFirst)
    {
        var original = _t.Settings.Presets[0];
        _t.PickFile(PresetsFile("One"));
        _t.Dialogs.Answers.Enqueue(true); // Replace
        await _vm.ImportCommand.ExecuteAsync(null);

        _t.Settings.Presets[0].Arguments = "edited";
        _t.Dialogs.Answers.Enqueue(undoAnyway);
        await _vm.LastChange.UndoCommand.ExecuteAsync(null);

        Assert.Contains("edits to them since will be lost", _t.Dialogs.Questions[^1]);
        Assert.Equal(expectedFirst ?? original.Name, _t.Settings.Presets[0].Name);
        Assert.Equal(!undoAnyway, _vm.LastChange.IsAvailable);
    }

    [Fact]
    public async Task Export_writes_a_file_that_imports_back()
    {
        _t.PickFile("");

        await _vm.ExportCommand.ExecuteAsync(null);

        var imported = PresetExchange.Import(File.ReadAllText(_t.Dialogs.FileToPick!), _t.Settings);
        Assert.Equal(_t.Settings.Presets.Select(p => p.Name), imported.Presets.Select(p => p.Name));
    }
}

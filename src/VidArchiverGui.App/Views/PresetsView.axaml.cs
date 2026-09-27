using Avalonia.Controls;
using VidArchiverGui.App.ViewModels;

namespace VidArchiverGui.App.Views;

public partial class PresetsView : UserControl
{
    public PresetsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not PresetsViewModel vm)
            {
                return;
            }

            var items = new List<Control>
            {
                new MenuItem { Header = "Blank preset", Command = vm.AddPresetCommand },
                new Separator(),
                new MenuItem { Header = "Built-in presets:", IsEnabled = false },
            };
            items.AddRange(vm.BuiltInPresets.Select(p =>
                new MenuItem { Header = p.Name, Command = vm.AddBuiltInPresetCommand, CommandParameter = p }));
            AddButton.Flyout = new MenuFlyout { ItemsSource = items };
        };
    }
}

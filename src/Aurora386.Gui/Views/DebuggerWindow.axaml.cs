using Avalonia.Controls;
using Avalonia.Input;
using System.Threading.Tasks;
using Aurora386.Gui.ViewModels;
using Avalonia.Platform.Storage;

namespace Aurora386.Gui.Views;

/// <summary>
/// The debugger window. Keyboard shortcuts follow the convention every
/// debugger has used for decades, so the muscle memory transfers: F5 to run,
/// F10 to step over, F11 to step into.
/// </summary>
public partial class DebuggerWindow : Window
{
    public DebuggerWindow()
    {
        InitializeComponent();

        // The picker has to come from the window: the view model has no
        // business knowing how to put a dialogue on the screen, and giving
        // it a callback keeps it testable.
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } model)
            {
                model.PickSaveAsync = PickSaveAsync;
            }
        };
    }

    private DebuggerViewModel? ViewModel => DataContext as DebuggerViewModel;

    /// <summary>Asks where to put a file about to be written.</summary>
    private async Task<string?> PickSaveAsync(string title, string suggestedName)
    {
        IStorageProvider? storage = StorageProvider;
        if (storage is null)
        {
            return null;
        }

        IStorageFile? chosen = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
        });

        return chosen?.Path.LocalPath;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        DebuggerViewModel? model = ViewModel;
        if (model is null) { base.OnKeyDown(e); return; }

        switch (e.Key)
        {
            case Key.F5:
                model.RunToBreakpointCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F10:
                model.StepOverCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F11:
                model.StepInstructionCommand.Execute(null);
                e.Handled = true;
                break;
            default:
                base.OnKeyDown(e);
                break;
        }
    }
}

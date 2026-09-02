using Avalonia.Controls;
using Avalonia.Input;
using Aurora386.Gui.ViewModels;

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
    }

    private DebuggerViewModel? ViewModel => DataContext as DebuggerViewModel;

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

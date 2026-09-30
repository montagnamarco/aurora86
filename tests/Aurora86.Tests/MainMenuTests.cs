using Avalonia.Controls;
using Avalonia.Input;
using Aurora86.Gui.ViewModels;
using Aurora86.Gui.Views;

namespace Aurora86.Tests;

/// <summary>Host menu navigation must not type into the emulated machine.</summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class MainMenuTests
{
    private readonly HeadlessApp _application;

    public MainMenuTests(HeadlessApp application)
    {
        _application = application;
    }

    [Fact]
    public void NavigatingTheHostMenuDoesNotSendScancodesToTheGuest()
    {
        _application.Invoke(() =>
        {
            var model = new MainWindowViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            try
            {
                model.Session.WithMachine(machine =>
                {
                    while ((machine.Keyboard.ReadByte(0x64) & 1) != 0)
                    {
                        machine.Keyboard.ReadByte(0x60);
                    }
                });

                Menu menu = window.FindControl<Menu>("MainMenu")!;
                var fileMenu = (MenuItem)menu.Items[0]!;
                fileMenu.Focus();
                fileMenu.IsSubMenuOpen = true;
                Assert.True(menu.IsOpen || menu.IsKeyboardFocusWithin);
                window.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Down,
                    PhysicalKey = PhysicalKey.ArrowDown,
                });
                window.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyUpEvent,
                    Key = Key.Down,
                    PhysicalKey = PhysicalKey.ArrowDown,
                });
                model.Session.WithMachine(machine =>
                    Assert.Equal(0, machine.Keyboard.ReadByte(0x64) & 1));
            }
            finally
            {
                window.Close();
            }
        });
    }
}

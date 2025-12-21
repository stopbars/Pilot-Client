using System.Windows;
using System.Windows.Input;
using BARS_Client_V2.Presentation.ViewModels;

namespace BARS_Client_V2;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        // Ignore this error in vscode.
        InitializeComponent();

        // Register keybinding for debug mode toggle (Ctrl+Shift+D)
        var debugModeBinding = new KeyBinding(
            new DebugModeCommand(this),
            Key.D,
            ModifierKeys.Control | ModifierKeys.Shift);
        InputBindings.Add(debugModeBinding);
    }

    private sealed class DebugModeCommand : ICommand
    {
        private readonly MainWindow _window;

        public DebugModeCommand(MainWindow window)
        {
            _window = window;
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (_window.DataContext is MainWindowViewModel vm)
            {
                vm.ToggleDebugMode();
            }
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
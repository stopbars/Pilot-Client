using System.Reflection;
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
        Title = $"BARS Client - v{GetAppVersion()}";

        // Register keybinding for debug mode toggle (Ctrl+Shift+D)
        var debugModeBinding = new KeyBinding(
            new DebugModeCommand(this),
            Key.D,
            ModifierKeys.Control | ModifierKeys.Shift);
        InputBindings.Add(debugModeBinding);
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.OpenSettings();
        }
    }

    private static string GetAppVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        var version = assembly.GetName().Version;
        return version == null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
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

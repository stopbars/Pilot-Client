using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BARS_Client_V2.Application; // Contains SimulatorManager; no conflict if fully qualified below
using BARS_Client_V2.Domain;
using BARS_Client_V2.Infrastructure.Diagnostics;
using BARS_Client_V2.Presentation.ViewModels;
using BARS_Client_V2.Services;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace BARS_Client_V2
{
    public partial class App : System.Windows.Application
    {
        private IHost? _host;
        private TaskbarIcon? _taskbarIcon;
        private MainWindow? _mainWindow;
        private ContextMenu? _trayContextMenu;
        private MenuItem? _autoMinimizeMenuItem;
        private MenuItem? _discordPresenceMenuItem;
        private DiscordPresenceService? _discordPresenceService;
        private bool _startupAutoMinimizeRequested;
        private MainWindowViewModel? _mainWindowViewModel;
        private bool _suppressStateChanged;
        private bool _startupDiscordPresenceEnabled = true;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            StartupTrace.Reset();
            StartupTrace.Write("OnStartup begin");
            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(lb =>
                {
                    lb.ClearProviders();
                    lb.AddConsole();          // Console (visible if app started from console / debug output window)
                    lb.AddDebug();            // VS Debug Output window
                    lb.AddEventSourceLogger(); // ETW / PerfView if needed
                    lb.SetMinimumLevel(LogLevel.Trace);
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ISimulatorConnector, Infrastructure.Simulators.Msfs.MsfsSimulatorConnector>();
                    services.AddSingleton<IAirportRepository, Infrastructure.Networking.HttpAirportRepository>();
                    services.AddSingleton<ISettingsStore, Infrastructure.Settings.JsonSettingsStore>();
                    services.AddSingleton<SimulatorManager>();
                    services.AddHostedService(sp => sp.GetRequiredService<SimulatorManager>()); // background stream
                    services.AddHttpClient();
                    services.AddSingleton<INearestAirportService, NearestAirportService>();
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Networking.AirportWebSocketManager>();
                    services.AddHostedService(sp => sp.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportWebSocketManager>());
                    services.AddSingleton<BARS_Client_V2.Services.DiscordPresenceService>();
                    services.AddHostedService(sp => sp.GetRequiredService<BARS_Client_V2.Services.DiscordPresenceService>());
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Networking.AirportStateHub>();
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController>(sp =>
                    {
                        var connectors = sp.GetServices<ISimulatorConnector>();
                        var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController>>();
                        var hub = sp.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportStateHub>();
                        var simManager = sp.GetRequiredService<SimulatorManager>();
                        return new BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController(connectors, logger, hub, simManager, null);
                    });
                    services.AddHostedService(sp => sp.GetRequiredService<BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController>());
                    services.AddSingleton<MainWindowViewModel>();
                    services.AddTransient<MainWindow>();
                })
                .Build();


            StartupTrace.Write("Host built");
            StartupTrace.Write("Resolving MainWindow");
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            StartupTrace.Write("MainWindow resolved");
            StartupTrace.Write("Resolving MainWindowViewModel");
            var vm = _host.Services.GetRequiredService<MainWindowViewModel>();
            StartupTrace.Write("MainWindowViewModel resolved");
            _discordPresenceService = _host.Services.GetRequiredService<DiscordPresenceService>();
            _startupDiscordPresenceEnabled = _discordPresenceService.IsEnabled;
            ClientSettings startupSettings;
            try
            {
                StartupTrace.Write("Preloading client settings");
                var settingsStore = _host.Services.GetRequiredService<ISettingsStore>();
                startupSettings = settingsStore.LoadAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                StartupTrace.Write($"Preloading client settings failed: {ex.Message}");
                startupSettings = ClientSettings.Empty;
            }
            vm.SeedSettings(startupSettings);
            _mainWindowViewModel = vm;
            _mainWindowViewModel.PropertyChanged += MainWindowViewModelOnPropertyChanged;
            _startupAutoMinimizeRequested = startupSettings.AutoMinimizeOnStart;
            mainWindow.DataContext = vm;
            StartupTrace.Write("Resolving AirportWebSocketManager");
            var wsMgr = _host.Services.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportWebSocketManager>();
            StartupTrace.Write("AirportWebSocketManager resolved");
            StartupTrace.Write("Resolving AirportStateHub");
            var hub = _host.Services.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportStateHub>();
            StartupTrace.Write("Airport services resolved");
            wsMgr.AttachHub(hub);
            wsMgr.Connected += () => vm.NotifyServerConnected();
            wsMgr.ConnectionError += code => vm.NotifyServerError(code);
            wsMgr.MessageReceived += msg => { vm.NotifyServerMessage(); _ = hub.ProcessAsync(msg); };
            var pointController = _host.Services.GetRequiredService<BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController>();
            wsMgr.OfflineSnapshotReceived += snapshot => { vm.NotifyOfflineSnapshot(); _ = hub.ProcessAsync(snapshot); };
            wsMgr.OfflineModeChanged += offline =>
            {
                vm.NotifyServerOfflineMode(offline);
                if (offline)
                {
                    pointController.Resume();
                }
                else if (!wsMgr.IsConnected)
                {
                    pointController.Suspend();
                }
            };
            wsMgr.Disconnected += reason =>
            {
                if (wsMgr.IsOfflineMode)
                {
                    pointController.Resume();
                    return;
                }
                pointController.Suspend();
            };
            wsMgr.Connected += () => pointController.Resume();
            StartupTrace.Write("Event wiring complete");

            ConfigureTaskbarIcon(mainWindow);
            UpdateAutoMinimizeMenuItem(_startupAutoMinimizeRequested);
            UpdateDiscordPresenceMenuItem(_startupDiscordPresenceEnabled);

            _mainWindow = mainWindow;
            MainWindow = mainWindow;
            _mainWindow.StateChanged += MainWindowOnStateChanged;
            _mainWindow.Closing += MainWindowOnClosing;

            if (_startupAutoMinimizeRequested)
            {
                mainWindow.ShowInTaskbar = false;
                mainWindow.WindowState = WindowState.Minimized;
            }

            var initializationTask = vm.InitializeAsync();
            _ = initializationTask.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    StartupTrace.Write($"MainWindowViewModel.InitializeAsync error: {t.Exception.GetBaseException().Message}");
                }
            }, TaskScheduler.Default);

            mainWindow.Show();
            StartupTrace.Write("MainWindow shown");

            if (_startupAutoMinimizeRequested)
            {
                HideMainWindowToTray(mainWindow);
            }
            else
            {
                ShowTrayIcon();
            }

            if (_host != null)
            {
                var host = _host;
                StartupTrace.Write("Starting host background task");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        StartupTrace.Write("Host StartAsync begin");
                        await host.StartAsync().ConfigureAwait(false);
                        StartupTrace.Write("Host StartAsync complete");
                    }
                    catch (Exception ex)
                    {
                        StartupTrace.Write($"Host StartAsync error: {ex.Message}");
                    }
                });
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            StartupTrace.Write("OnExit begin");
            if (_mainWindow != null)
            {
                _mainWindow.StateChanged -= MainWindowOnStateChanged;
                _mainWindow.Closing -= MainWindowOnClosing;
            }
            if (_mainWindowViewModel != null)
            {
                _mainWindowViewModel.PropertyChanged -= MainWindowViewModelOnPropertyChanged;
                _mainWindowViewModel = null;
            }
            if (_trayContextMenu != null)
            {
                _trayContextMenu.Opened -= TrayContextMenuOnOpened;
                _trayContextMenu = null;
            }
            _autoMinimizeMenuItem = null;
            if (_host != null)
            {
                try
                {
                    await _host.StopAsync();
                    StartupTrace.Write("Host StopAsync complete");
                }
                catch (Exception ex)
                {
                    StartupTrace.Write($"Host StopAsync error: {ex.Message}");
                }
                _host.Dispose();
                StartupTrace.Write("Host disposed");
            }
            DisposeTaskbarIcon();
            base.OnExit(e);
            StartupTrace.Write("OnExit complete");
        }

        private void ConfigureTaskbarIcon(Window mainWindow)
        {
            if (FindResource("AppTaskbarIcon") is TaskbarIcon taskbarIcon)
            {
                _taskbarIcon = taskbarIcon;
                _taskbarIcon.Visibility = Visibility.Collapsed;
                _taskbarIcon.ToolTipText = string.IsNullOrWhiteSpace(mainWindow.Title)
                    ? "BARS Client"
                    : mainWindow.Title;
                if (_taskbarIcon.ContextMenu is ContextMenu menu)
                {
                    _trayContextMenu = menu;
                    _trayContextMenu.Opened += TrayContextMenuOnOpened;
                    _autoMinimizeMenuItem = FindAutoMinimizeMenuItem(menu);
                    _discordPresenceMenuItem = FindDiscordPresenceMenuItem(menu);
                }
                StartupTrace.Write("Taskbar icon prepared");
            }
            else
            {
                StartupTrace.Write("TaskbarIcon resource not found");
            }
        }

        private void MainWindowOnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            DisposeTaskbarIcon();
        }

        private void MainWindowOnStateChanged(object? sender, EventArgs e)
        {
            if (_suppressStateChanged || MainWindow == null)
            {
                return;
            }

            if (MainWindow.WindowState == WindowState.Minimized)
            {
                HideMainWindowToTray(MainWindow);
            }
        }

        private void HideMainWindowToTray(Window window)
        {
            if (_taskbarIcon == null)
            {
                StartupTrace.Write("Tray icon unavailable; skipping tray minimize");
                return;
            }

            ShowTrayIcon();
            window.ShowInTaskbar = false;
            window.Hide();
        }

        private void RestoreMainWindowFromTray()
        {
            if (MainWindow == null)
            {
                return;
            }

            _suppressStateChanged = true;
            try
            {
                MainWindow.ShowInTaskbar = true;
                MainWindow.Show();
                MainWindow.WindowState = WindowState.Normal;
                MainWindow.Activate();
            }
            finally
            {
                _suppressStateChanged = false;
            }
        }

        private void TrayContextMenuOnOpened(object? sender, RoutedEventArgs e)
        {
            var isChecked = _mainWindowViewModel?.AutoMinimizeOnStart ?? false;
            UpdateAutoMinimizeMenuItem(isChecked);
            var discordEnabled = _discordPresenceService?.IsEnabled ?? _startupDiscordPresenceEnabled;
            UpdateDiscordPresenceMenuItem(discordEnabled);
        }

        private static MenuItem? FindAutoMinimizeMenuItem(ContextMenu menu) =>
            menu.Items.OfType<MenuItem>().FirstOrDefault(item =>
                item.Tag is string tag && string.Equals(tag, "AutoMinimizeToggle", StringComparison.Ordinal));

        private static MenuItem? FindDiscordPresenceMenuItem(ContextMenu menu) =>
            menu.Items.OfType<MenuItem>().FirstOrDefault(item =>
                item.Tag is string tag && string.Equals(tag, "DiscordPresenceToggle", StringComparison.Ordinal));

        private void UpdateAutoMinimizeMenuItem(bool isChecked)
        {
            if (_autoMinimizeMenuItem != null)
            {
                _autoMinimizeMenuItem.IsChecked = isChecked;
            }
        }

        private void UpdateDiscordPresenceMenuItem(bool isChecked)
        {
            if (_discordPresenceMenuItem != null)
            {
                _discordPresenceMenuItem.IsChecked = isChecked;
            }
        }

        private void MainWindowViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(MainWindowViewModel.AutoMinimizeOnStart), StringComparison.Ordinal) || _mainWindowViewModel == null)
            {
                return;
            }

            void Apply() => UpdateAutoMinimizeMenuItem(_mainWindowViewModel.AutoMinimizeOnStart);

            if (Dispatcher?.CheckAccess() == true)
            {
                Apply();
            }
            else
            {
                Dispatcher?.Invoke(Apply);
            }
        }

        private void TaskbarIcon_AutoMinimizeMenuItem_OnClick(object? sender, RoutedEventArgs e)
        {
            if (_mainWindowViewModel == null || sender is not MenuItem menuItem)
            {
                return;
            }

            _mainWindowViewModel.AutoMinimizeOnStart = menuItem.IsChecked;
        }

        private void TaskbarIcon_OnTrayLeftMouseUp(object? sender, RoutedEventArgs e)
        {
            RestoreMainWindowFromTray();
        }

        private void TaskbarIcon_ShowMenuItem_OnClick(object? sender, RoutedEventArgs e)
        {
            RestoreMainWindowFromTray();
        }

        private void TaskbarIcon_DiscordPresenceMenuItem_OnClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem)
            {
                return;
            }

            var requestedState = menuItem.IsChecked;
            if (_discordPresenceService == null)
            {
                _startupDiscordPresenceEnabled = requestedState;
                UpdateDiscordPresenceMenuItem(requestedState);
                return;
            }

            _discordPresenceService.SetEnabled(requestedState);
            UpdateDiscordPresenceMenuItem(_discordPresenceService.IsEnabled);
        }

        private void TaskbarIcon_ExitMenuItem_OnClick(object? sender, RoutedEventArgs e)
        {
            Shutdown();
        }

        private void DisposeTaskbarIcon()
        {
            if (_taskbarIcon == null)
            {
                return;
            }

            HideTrayIcon();
            if (_trayContextMenu != null)
            {
                _trayContextMenu.Opened -= TrayContextMenuOnOpened;
                _trayContextMenu = null;
            }
            _autoMinimizeMenuItem = null;
            _taskbarIcon.Dispose();
            _taskbarIcon = null;
        }

        private void ShowTrayIcon()
        {
            if (_taskbarIcon == null)
            {
                return;
            }

            _taskbarIcon.Visibility = Visibility.Visible;
            try
            {
                _taskbarIcon.ForceCreate();
            }
            catch (Exception ex)
            {
                StartupTrace.Write($"Taskbar icon ForceCreate failed: {ex.Message}");
            }
        }

        private void HideTrayIcon()
        {
            if (_taskbarIcon == null)
            {
                return;
            }

            _taskbarIcon.Visibility = Visibility.Collapsed;
        }
    }

}

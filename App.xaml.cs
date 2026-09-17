using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
        private const string SingleInstanceMutexName = "Global\\Pilot-Client-SingleInstance";
        private const string SingleInstancePipeName = "Pilot-Client-SingleInstance-Pipe";
        private IHost? _host;
        private TaskbarIcon? _taskbarIcon;
        private MainWindow? _mainWindow;
        private DiscordPresenceService? _discordPresenceService;
        private bool _startupAutoMinimizeRequested;
        private MainWindowViewModel? _mainWindowViewModel;
        private bool _suppressStateChanged;
        private SettingsWindow? _settingsWindow;
        private CancellationTokenRegistration _applicationStoppingRegistration;
        private Mutex? _singleInstanceMutex;
        private bool _ownsSingleInstanceMutex;
        private CancellationTokenSource? _singleInstancePipeCts;
        private Task? _singleInstancePipeTask;
        private string? _pendingProtocolUrl;
        private CancellationTokenSource? _removalsUpdateCts;
        private Task? _removalsUpdateTask;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var startupProtocolUrl = GetTestingProtocolUrl(e.Args);
            if (!TryAcquireSingleInstanceMutex())
            {
                if (string.IsNullOrWhiteSpace(startupProtocolUrl))
                {
                    MessageBox.Show("BARS client already running.", "BARS Client", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                if (!TryNotifyExistingInstance(startupProtocolUrl))
                {
                    TryActivateExistingInstance();
                }
                Shutdown();
                return;
            }
            StartSingleInstancePipeServer();
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
                    services.AddSingleton<Infrastructure.Simulators.XPlane.XPlaneSimulatorConnector>();
                    services.AddSingleton<ISimulatorConnector>(sp =>
                        sp.GetRequiredService<Infrastructure.Simulators.XPlane.XPlaneSimulatorConnector>());
                    services.AddSingleton<IAirportRepository, Infrastructure.Networking.HttpAirportRepository>();
                    services.AddSingleton<ISettingsStore, Infrastructure.Settings.JsonSettingsStore>();
                    services.AddSingleton<LightDrawDistanceSettings>();
                    services.AddSingleton<RemovalsUpdateService>();
                    services.AddSingleton<SimulatorManager>();
                    services.AddHostedService(sp => sp.GetRequiredService<SimulatorManager>()); // background stream
                    services.AddHttpClient();
                    services.AddSingleton<INearestAirportService, NearestAirportService>();
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Networking.AirportWebSocketManager>();
                    services.AddHostedService(sp => sp.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportWebSocketManager>());
                    services.AddSingleton<BARS_Client_V2.Services.DiscordPresenceService>();
                    services.AddHostedService(sp => sp.GetRequiredService<BARS_Client_V2.Services.DiscordPresenceService>());
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Networking.AirportStateHub>();
                    services.AddSingleton<BARS_Client_V2.Services.TestingModeService>();
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController>(sp =>
                    {
                        var connectors = sp.GetServices<ISimulatorConnector>();
                        var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController>>();
                        var hub = sp.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportStateHub>();
                        var simManager = sp.GetRequiredService<SimulatorManager>();
                        return new BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController(
                            connectors, logger, hub, simManager, null, sp.GetRequiredService<LightDrawDistanceSettings>());
                    });
                    services.AddHostedService(sp => sp.GetRequiredService<BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController>());
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Simulators.XPlane.XPlanePointController>();
                    services.AddHostedService(sp =>
                        sp.GetRequiredService<BARS_Client_V2.Infrastructure.Simulators.XPlane.XPlanePointController>());
                    services.AddSingleton<MainWindowViewModel>(sp =>
                    {
                        var simManager = sp.GetRequiredService<SimulatorManager>();
                        var nearestService = sp.GetRequiredService<INearestAirportService>();
                        var airportRepo = sp.GetRequiredService<IAirportRepository>();
                        var settingsStore = sp.GetRequiredService<ISettingsStore>();
                        var pointController = sp.GetRequiredService<BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController>();
                        var hub = sp.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportStateHub>();
                        return new MainWindowViewModel(simManager, nearestService, airportRepo, settingsStore, pointController, hub);
                    });
                    services.AddTransient<MainWindow>();
                })
                .Build();

            var lifetime = _host.Services.GetRequiredService<IHostApplicationLifetime>();
            _applicationStoppingRegistration = lifetime.ApplicationStopping.Register(OnHostApplicationStopping);


            StartupTrace.Write("Host built");
            StartupTrace.Write("Resolving MainWindow");
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            StartupTrace.Write("MainWindow resolved");
            StartupTrace.Write("Resolving MainWindowViewModel");
            var vm = _host.Services.GetRequiredService<MainWindowViewModel>();
            StartupTrace.Write("MainWindowViewModel resolved");
            _discordPresenceService = _host.Services.GetRequiredService<DiscordPresenceService>();
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
            _discordPresenceService.SetEnabled(startupSettings.DiscordPresenceEnabled);
            _host.Services.GetRequiredService<LightDrawDistanceSettings>().SetMeters(startupSettings.LightDrawDistanceMeters);
            vm.SeedSettings(startupSettings);
            _mainWindowViewModel = vm;
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
            wsMgr.MessageReceived += msg =>
            {
                vm.NotifyServerMessage();
                return hub.ProcessAsync(msg);
            };
            var pointController = _host.Services.GetRequiredService<BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController>();
            wsMgr.OfflineSnapshotReceived += snapshot =>
            {
                vm.NotifyOfflineSnapshot();
                return hub.ProcessAsync(snapshot);
            };
            wsMgr.OfflineModeChanged += offline =>
            {
                vm.NotifyServerOfflineMode(offline);
                if (offline)
                {
                    pointController.Resume();
                }
                else if (!wsMgr.IsConnected && !hub.IsTestingMode)
                {
                    pointController.Suspend();
                }
            };
            wsMgr.Disconnected += reason =>
            {
                if (hub.IsTestingMode)
                {
                    pointController.Resume();
                    return;
                }

                if (wsMgr.IsOfflineMode)
                {
                    pointController.Resume();
                    return;
                }
                pointController.Suspend();
                vm.NotifyServerDisconnected(reason);
            };
            wsMgr.Connected += () => pointController.Resume();
            hub.TestingModeChanged += e =>
            {
                if (e.IsTestingMode)
                {
                    pointController.Resume();
                }
                else if (!wsMgr.IsConnected && !wsMgr.IsOfflineMode)
                {
                    pointController.Suspend();
                }
            };
            StartupTrace.Write("Event wiring complete");

            ConfigureTaskbarIcon(mainWindow);

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
            _removalsUpdateCts = new CancellationTokenSource();
            _removalsUpdateTask = CheckRemovalsInBackgroundAsync(
                startupSettings,
                vm,
                _removalsUpdateCts.Token);

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

            ProcessTestingProtocolUrl(startupProtocolUrl);
            ProcessTestingProtocolUrl(_pendingProtocolUrl);
            _pendingProtocolUrl = null;
        }

        private async Task CheckRemovalsInBackgroundAsync(
            ClientSettings startupSettings,
            MainWindowViewModel viewModel,
            CancellationToken cancellationToken)
        {
            var reconcilePending = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    StartupTrace.Write("Checking for removals updates in background");
                    var service = _host?.Services.GetRequiredService<RemovalsUpdateService>();
                    if (service == null) return;
                    RemovalsUpdateService.RemovalsUpdateResult result;
                    await RemovalFileAccess.MsfsTransactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        result = await service.CheckAndUpdateRemovalsAsync(startupSettings, cancellationToken)
                            .ConfigureAwait(false);
                        viewModel.ApplyRemovalsUpdateResult(result);
                        reconcilePending |= result.UpdatedSimulators.Count > 0;
                        if (!cancellationToken.IsCancellationRequested &&
                            reconcilePending)
                        {
                            try
                            {
                                var settingsStore = _host?.Services.GetRequiredService<ISettingsStore>();
                                if (settingsStore != null)
                                {
                                    await SceneryService.Instance.SyncMsfsRemovalStatesFromSettingsAsync(
                                        settingsStore,
                                        cancellationToken).ConfigureAwait(false);
                                    reconcilePending = false;
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception syncError)
                            {
                                StartupTrace.Write($"Post-update removals reconciliation failed: {syncError.Message}");
                                throw;
                            }
                        }
                    }
                    finally
                    {
                        RemovalFileAccess.MsfsTransactionGate.Release();
                    }
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        viewModel.SetRemovalsUpdateError(result.FailedSimulators.Count == 0 ? null :
                            $"Could not update removals for {string.Join(", ", result.FailedSimulators.Select(sim => sim == "msfs2024" ? "MSFS 2024" : "MSFS 2020"))}. Leave BARS open to retry automatically.");
                    }
                    StartupTrace.Write($"Removals update check complete, updated sims: {string.Join(", ", result.UpdatedSimulators)}");
                    if (result.FailedSimulators.Count == 0) return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    StartupTrace.Write("Removals update check cancelled");
                    return;
                }
                catch (RemovalPackageInstaller.RecoveryException ex)
                {
                    StartupTrace.Write(ex.ToString());
                    viewModel.SetRemovalsUpdateError("Removal update recovery failed. Close the simulator and contact BARS support with startup.log.");
                    return;
                }
                catch (Exception ex)
                {
                    StartupTrace.Write($"Removals update check failed: {ex.Message}");
                    viewModel.SetRemovalsUpdateError("Could not update removals. Leave BARS open to retry automatically.");
                }
                StartupTrace.Write("Retrying removals update in 30 seconds");
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            StartupTrace.Write("OnExit begin");
            if (_mainWindowViewModel != null)
            {
                try { await _mainWindowViewModel.PrepareForShutdownAsync(); }
                catch (Exception ex) { StartupTrace.Write($"Active-mode shutdown cleanup failed: {ex.Message}"); }
            }
            _removalsUpdateCts?.Cancel();
            if (_removalsUpdateTask != null)
            {
                try { await _removalsUpdateTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { StartupTrace.Write($"Removals update shutdown error: {ex.Message}"); }
            }
            _removalsUpdateCts?.Dispose();
            _removalsUpdateCts = null;
            _removalsUpdateTask = null;
            if (_mainWindow != null)
            {
                _mainWindow.StateChanged -= MainWindowOnStateChanged;
                _mainWindow.Closing -= MainWindowOnClosing;
            }
            if (_mainWindowViewModel != null)
            {
                _mainWindowViewModel = null;
            }
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
                finally
                {
                    _applicationStoppingRegistration.Dispose();
                }
                _host.Dispose();
                StartupTrace.Write("Host disposed");
            }
            DisposeTaskbarIcon();
            StopSingleInstancePipeServer();
            ReleaseSingleInstanceMutex();
            base.OnExit(e);
            StartupTrace.Write("OnExit complete");
        }

        private bool TryAcquireSingleInstanceMutex()
        {
            try
            {
                _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
                _ownsSingleInstanceMutex = createdNew;
                return createdNew;
            }
            catch
            {
                _ownsSingleInstanceMutex = false;
                _singleInstanceMutex = null;
                return true;
            }
        }

        private void StartSingleInstancePipeServer()
        {
            _singleInstancePipeCts = new CancellationTokenSource();
            _singleInstancePipeTask = Task.Run(() => SingleInstancePipeServerLoopAsync(_singleInstancePipeCts.Token));
        }

        private void StopSingleInstancePipeServer()
        {
            if (_singleInstancePipeCts == null)
            {
                return;
            }

            try
            {
                _singleInstancePipeCts.Cancel();
            }
            catch
            {
                // Best-effort shutdown.
            }
            finally
            {
                _singleInstancePipeCts.Dispose();
                _singleInstancePipeCts = null;
                _singleInstancePipeTask = null;
            }
        }

        private async Task SingleInstancePipeServerLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        SingleInstancePipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    var message = await reader.ReadLineAsync().ConfigureAwait(false);

                    if (message != null && message.StartsWith("URL ", StringComparison.Ordinal))
                    {
                        var protocolUrl = message[4..];
                        Dispatcher?.BeginInvoke(new Action(() =>
                        {
                            RestoreMainWindowFromTray();
                            if (MainWindow != null)
                            {
                                if (MainWindow.WindowState == WindowState.Minimized)
                                {
                                    MainWindow.WindowState = WindowState.Normal;
                                }
                                MainWindow.Activate();
                            }
                            ProcessTestingProtocolUrl(protocolUrl);
                        }));
                    }
                    else if (string.Equals(message, "SHOW", StringComparison.OrdinalIgnoreCase))
                    {
                        Dispatcher?.BeginInvoke(new Action(() =>
                        {
                            RestoreMainWindowFromTray();
                            if (MainWindow != null)
                            {
                                if (MainWindow.WindowState == WindowState.Minimized)
                                {
                                    MainWindow.WindowState = WindowState.Normal;
                                }
                                MainWindow.Activate();
                            }
                        }));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    StartupTrace.Write($"Single-instance pipe server error: {ex.Message}");
                    try
                    {
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private static bool TryNotifyExistingInstance(string? protocolUrl = null)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out);
                client.Connect(250);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(string.IsNullOrWhiteSpace(protocolUrl) ? "SHOW" : $"URL {protocolUrl}");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? GetTestingProtocolUrl(string[]? args)
        {
            if (args == null || args.Length == 0)
            {
                return null;
            }

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg) || arg.Any(char.IsControl))
                {
                    continue;
                }

                if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) &&
                    string.Equals(uri.Scheme, "bars", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(uri.Host, "test", StringComparison.OrdinalIgnoreCase))
                {
                    return arg;
                }
            }

            return null;
        }

        private void ProcessTestingProtocolUrl(string? protocolUrl)
        {
            if (string.IsNullOrWhiteSpace(protocolUrl))
            {
                return;
            }

            if (_host == null)
            {
                _pendingProtocolUrl = protocolUrl;
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var testingMode = _host.Services.GetRequiredService<TestingModeService>();
                    await testingMode.HandleProtocolUrlAsync(protocolUrl).ConfigureAwait(false);
                    Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        RestoreMainWindowFromTray();
                        MainWindow?.Activate();
                    }));
                }
                catch (Exception ex)
                {
                    StartupTrace.Write($"Testing protocol error: {ex.Message}");
                    Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        MessageBox.Show(
                            ex.Message,
                            "BARS Testing Mode",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }));
                }
            });
        }

        private void ReleaseSingleInstanceMutex()
        {
            if (_singleInstanceMutex == null)
            {
                return;
            }

            try
            {
                if (_ownsSingleInstanceMutex)
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
            finally
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                _ownsSingleInstanceMutex = false;
            }
        }

        private static void TryActivateExistingInstance()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var processes = Process.GetProcessesByName(current.ProcessName)
                    .Where(p => p.Id != current.Id)
                    .ToArray();

                foreach (var process in processes)
                {
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (IsIconic(handle))
                    {
                        ShowWindow(handle, SwRestore);
                    }

                    SetForegroundWindow(handle);
                    break;
                }
            }
            catch
            {
                // Best-effort activation.
            }
        }

        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private void OnHostApplicationStopping()
        {
            var dispatcher = Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (Dispatcher?.HasShutdownStarted == true)
                    {
                        return;
                    }
                    Shutdown();
                }));
            }
            catch
            {
                // Best-effort shutdown; ignore dispatcher rejection during teardown.
            }
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

        private void TaskbarIcon_OnTrayLeftMouseUp(object? sender, RoutedEventArgs e)
        {
            RestoreMainWindowFromTray();
        }

        private void TaskbarIcon_ShowMenuItem_OnClick(object? sender, RoutedEventArgs e)
        {
            RestoreMainWindowFromTray();
        }

        internal void OpenSettings()
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }
            if (_mainWindow == null || _mainWindowViewModel == null || _discordPresenceService == null)
            {
                return;
            }

            RestoreMainWindowFromTray();
            var window = new SettingsWindow(_mainWindowViewModel, _discordPresenceService,
                _host!.Services.GetRequiredService<LightDrawDistanceSettings>())
            {
                Owner = _mainWindow
            };
            _settingsWindow = window;
            try
            {
                window.ShowDialog();
            }
            finally
            {
                _settingsWindow = null;
            }
        }

        private void TaskbarIcon_SettingsMenuItem_OnClick(object? sender, RoutedEventArgs e)
        {
            OpenSettings();
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

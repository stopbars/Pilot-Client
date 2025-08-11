using System.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http;
using BARS_Client_V2.Domain;
using BARS_Client_V2.Application; // Contains SimulatorManager; no conflict if fully qualified below
using BARS_Client_V2.Presentation.ViewModels;
using BARS_Client_V2.Services;

namespace BARS_Client_V2
{
    public partial class App : System.Windows.Application
    {
        private IHost? _host;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
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

            _host.Start();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            var vm = _host.Services.GetRequiredService<MainWindowViewModel>();
            mainWindow.DataContext = vm;
            var wsMgr = _host.Services.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportWebSocketManager>();
            var hub = _host.Services.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportStateHub>();
            wsMgr.AttachHub(hub);
            wsMgr.Connected += () => vm.NotifyServerConnected();
            wsMgr.ConnectionError += code => vm.NotifyServerError(code);
            wsMgr.MessageReceived += msg => { vm.NotifyServerMessage(); _ = hub.ProcessAsync(msg); };
            var pointController = _host.Services.GetRequiredService<BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController>();
            wsMgr.Disconnected += reason => { pointController.Suspend(); _ = pointController.DespawnAllAsync(); };
            wsMgr.Connected += () => pointController.Resume();
            mainWindow.Show();
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
            base.OnExit(e);
        }
    }

}

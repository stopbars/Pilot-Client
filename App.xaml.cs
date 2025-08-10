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
                .ConfigureLogging(lb => lb.AddConsole())
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ISimulatorConnector, Infrastructure.Simulators.Msfs.MsfsSimulatorConnector>();
                    services.AddSingleton<IAirportRepository, Infrastructure.InMemory.InMemoryAirportRepository>();
                    services.AddSingleton<ISettingsStore, Infrastructure.Settings.JsonSettingsStore>();
                    services.AddSingleton<SimulatorManager>();
                    services.AddHostedService(sp => sp.GetRequiredService<SimulatorManager>()); // background stream
                    services.AddHttpClient();
                    services.AddSingleton<INearestAirportService, NearestAirportService>();
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Networking.AirportWebSocketManager>();
                    services.AddHostedService(sp => sp.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportWebSocketManager>());
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Networking.AirportStateHub>();
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController>();
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
            wsMgr.Connected += () => vm.NotifyServerConnected();
            wsMgr.ConnectionError += code => vm.NotifyServerError(code);
            wsMgr.MessageReceived += msg => { vm.NotifyServerMessage(); _ = hub.ProcessAsync(msg); };
            var pointController = _host.Services.GetRequiredService<BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsPointController>();
            wsMgr.Disconnected += reason => { _ = pointController.DespawnAllAsync(); };
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

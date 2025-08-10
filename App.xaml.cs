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
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
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
                    // Register simulator connectors (order: real MSFS then mock fallback)
                    services.AddSingleton<ISimulatorConnector, Infrastructure.Simulators.Msfs.MsfsSimulatorConnector>();
                    // Repositories / stores
                    services.AddSingleton<IAirportRepository, Infrastructure.InMemory.InMemoryAirportRepository>();
                    services.AddSingleton<ISettingsStore, Infrastructure.Settings.JsonSettingsStore>();
                    // Core manager
                    services.AddSingleton<SimulatorManager>();
                    services.AddHostedService(sp => sp.GetRequiredService<SimulatorManager>()); // background stream
                    // HTTP + nearest airport service
                    services.AddHttpClient();
                    services.AddSingleton<INearestAirportService, NearestAirportService>();
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Networking.AirportWebSocketManager>();
                    services.AddHostedService(sp => sp.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportWebSocketManager>());
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Networking.AirportStreamMessageProcessor>();
                    services.AddSingleton<BARS_Client_V2.Infrastructure.Networking.PointStateDispatcher>();
                    // Point state listeners (add MsfsPointController which also runs background loop)
                    services.AddSingleton<Infrastructure.Simulators.Msfs.MsfsPointController>();
                    services.AddSingleton<Domain.IPointStateListener>(sp => sp.GetRequiredService<Infrastructure.Simulators.Msfs.MsfsPointController>());
                    services.AddHostedService(sp => sp.GetRequiredService<Infrastructure.Simulators.Msfs.MsfsPointController>());
                    // ViewModels
                    services.AddSingleton<MainWindowViewModel>();
                    // Windows
                    services.AddTransient<MainWindow>();
                })
                .Build();

            _host.Start();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            var vm = _host.Services.GetRequiredService<MainWindowViewModel>();
            mainWindow.DataContext = vm;
            // Wire server connection events
            var wsMgr = _host.Services.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportWebSocketManager>();
            var processor = _host.Services.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.AirportStreamMessageProcessor>();
            // Force creation of dispatcher so it subscribes before messages arrive
            _ = _host.Services.GetRequiredService<BARS_Client_V2.Infrastructure.Networking.PointStateDispatcher>();
            wsMgr.Connected += () => vm.NotifyServerConnected();
            wsMgr.ConnectionError += code => vm.NotifyServerError(code);
            wsMgr.MessageReceived += msg => { vm.NotifyServerMessage(); _ = processor.ProcessAsync(msg); };
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

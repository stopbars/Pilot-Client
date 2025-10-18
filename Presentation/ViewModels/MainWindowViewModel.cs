using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using BARS_Client_V2.Application;
using BARS_Client_V2.Domain;
using BARS_Client_V2.Services;
using BARS_Client_V2.Infrastructure.Diagnostics;

namespace BARS_Client_V2.Presentation.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly SimulatorManager _simManager;
    private readonly DispatcherTimer _uiPoll;
    private readonly DispatcherTimer _serverTimer;
    private string _closestAirport = "Unknown";
    private bool _onGround;
    private string _simulatorName = "Not Connected";
    private bool _simConnected;
    private double _latitude;
    private double _longitude;
    private readonly IAirportRepository _airportRepo;
    private readonly ISettingsStore _settingsStore;
    private readonly int _pageSize = 7;
    private int _currentPage = 1;
    private int _totalCount;
    private string? _searchText;
    private string? _apiToken;
    private string? _originalApiToken; // tracks last saved (sanitized) token
    private string _status = "Ready";
    private bool _isBusy;
    private bool _serverConnected; // backend websocket
    private DateTime _lastServerMessageUtc;

    public ObservableCollection<AirportRowViewModel> Airports { get; } = new();

    public string ClosestAirport { get => _closestAirport; private set { if (value != _closestAirport) { _closestAirport = value; OnPropertyChanged(); } } }

    public bool OnGround
    {
        get => _onGround;
        set { if (value != _onGround) { _onGround = value; OnPropertyChanged(); OnPropertyChanged(nameof(OnGroundText)); } }
    }

    public string OnGroundText => OnGround ? "On Ground" : "Airborne";
    public string SimulatorName { get => _simulatorName; set { if (value != _simulatorName) { _simulatorName = value; OnPropertyChanged(); } } }
    public bool SimulatorConnected { get => _simConnected; set { if (value != _simConnected) { _simConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(SimulatorConnectionText)); OnPropertyChanged(nameof(SimulatorStatusColor)); } } }
    public string SimulatorConnectionText => SimulatorConnected ? "Connected" : "Disconnected";
    public string SimulatorStatusColor => SimulatorConnected ? "LimeGreen" : "Gray";
    public bool ServerConnected { get => _serverConnected; private set { if (value != _serverConnected) { _serverConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(ServerStatusText)); OnPropertyChanged(nameof(ServerStatusColor)); } } }
    public string ServerStatusText => ServerConnected ? "Connected" : (string.IsNullOrEmpty(ServerStatusDetail) ? "Disconnected" : ServerStatusDetail);
    public string ServerStatusColor => ServerConnected ? "LimeGreen" : "Gray";
    public string ServerStatusDetail { get; private set; } = ""; // optional reason
    public double Latitude { get => _latitude; set { if (value != _latitude) { _latitude = value; OnPropertyChanged(); } } }
    public double Longitude { get => _longitude; set { if (value != _longitude) { _longitude = value; OnPropertyChanged(); } } }

    public ObservableCollection<string> LogLines { get; } = new();

    private readonly INearestAirportService _nearestService;
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);

    public string? SearchText { get => _searchText; set { if (value != _searchText) { _searchText = value; OnPropertyChanged(); } } }
    public string? ApiToken
    {
        get => _apiToken;
        set
        {
            var sanitized = SanitizeToken(value);
            if (sanitized != _apiToken)
            {
                _apiToken = sanitized;
                OnPropertyChanged();
                (SaveTokenCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
    public string Status { get => _status; private set { if (value != _status) { _status = value; OnPropertyChanged(); } } }
    public bool IsBusy { get => _isBusy; private set { if (value != _isBusy) { _isBusy = value; OnPropertyChanged(); } } }
    public int CurrentPage { get => _currentPage; private set { if (value != _currentPage) { _currentPage = value; OnPropertyChanged(); OnPropertyChanged(nameof(PageInfo)); UpdatePagingCommands(); } } }
    public int TotalCount { get => _totalCount; private set { if (value != _totalCount) { _totalCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PageInfo)); UpdatePagingCommands(); } } }
    public string PageInfo => $"Page {CurrentPage} of {Math.Max(1, (int)Math.Ceiling(TotalCount / (double)_pageSize))}";

    // Commands (simple DelegateCommand implementation inline)
    public ICommand SearchCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand SaveTokenCommand { get; }

    public MainWindowViewModel(SimulatorManager simManager, INearestAirportService nearestService, IAirportRepository airportRepository, ISettingsStore settingsStore)
    {
        StartupTrace.Write("MainWindowViewModel ctor");
        _simManager = simManager;
        _nearestService = nearestService;
        _airportRepo = airportRepository;
        _settingsStore = settingsStore;
        _uiPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiPoll.Tick += UiPollOnTick;
        _uiPoll.Start();

        _serverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _serverTimer.Tick += ServerTimerOnTick;
        _serverTimer.Start();

        SearchCommand = new DelegateCommand(async _ => await RunSearchAsync(resetPage: true));
        NextPageCommand = new DelegateCommand(async _ => { CurrentPage++; await RunSearchAsync(); }, _ => CanChangePage(+1));
        PrevPageCommand = new DelegateCommand(async _ => { CurrentPage--; await RunSearchAsync(); }, _ => CanChangePage(-1));
        SaveTokenCommand = new DelegateCommand(async _ => await SaveSettingsAsync(), _ => CanSaveToken());

        // Kick off async load of settings + initial data
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        StartupTrace.Write("InitializeAsync start");
        var settings = await _settingsStore.LoadAsync();
        // Sanitize and store original token baseline
        _originalApiToken = SanitizeToken(settings.ApiToken);
        _apiToken = _originalApiToken; // set backing field directly to avoid redundant raise
        OnPropertyChanged(nameof(ApiToken));
        (SaveTokenCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        _savedPackages = settings.AirportPackages != null
            ? new Dictionary<string, string>(settings.AirportPackages, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        StartupTrace.Write($"InitializeAsync settings loaded; packages={_savedPackages.Count}");
        await RefreshFromStateAsync();
        StartupTrace.Write("InitializeAsync state refreshed");
        await RunSearchAsync(resetPage: true);
        StartupTrace.Write("InitializeAsync completed initial search");
    }

    private IDictionary<string, string> _savedPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private bool CanChangePage(int delta)
    {
        var newPage = CurrentPage + delta;
        var totalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)_pageSize));
        return newPage >= 1 && newPage <= totalPages;
    }

    private void UpdatePagingCommands()
    {
        (NextPageCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        (PrevPageCommand as DelegateCommand)?.RaiseCanExecuteChanged();
    }

    private async Task RunSearchAsync(bool resetPage = false)
    {
        StartupTrace.Write($"RunSearchAsync begin reset={resetPage}");
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            Status = "Searching...";
            if (resetPage) CurrentPage = 1;
            var (items, total) = await _airportRepo.SearchAsync(SearchText, CurrentPage, _pageSize);
            StartupTrace.Write($"RunSearchAsync results items={items.Count} total={total}");
            TotalCount = total;
            Airports.Clear();
            var packagesChanged = false;
            foreach (var a in items)
            {
                var row = new AirportRowViewModel(a);
                if (_savedPackages.TryGetValue(a.ICAO, out var pkgName))
                {
                    var match = a.SceneryPackages.FirstOrDefault(p => p.Name == pkgName);
                    if (match != null) row.SelectedPackage = match;
                }
                // Auto-select first package if none stored/selected (always at least one per requirements)
                if (row.SelectedPackage == null && a.SceneryPackages.Count > 0)
                {
                    var defaultPackage = a.SceneryPackages.First();
                    row.SelectedPackage = defaultPackage;
                    var needsUpdate = !_savedPackages.TryGetValue(a.ICAO, out var existing) || !string.Equals(existing, defaultPackage.Name, StringComparison.Ordinal);
                    if (needsUpdate)
                    {
                        _savedPackages[a.ICAO] = defaultPackage.Name;
                        packagesChanged = true;
                        try { SceneryService.Instance.SetSelectedPackage(a.ICAO, defaultPackage.Name); } catch (Exception ex) { StartupTrace.Write($"SceneryService.SetSelectedPackage error: {ex.Message}"); }
                    }
                }
                row.PropertyChanged += AirportRowOnPropertyChanged;
                Airports.Add(row);
            }
            Status = $"Loaded {Airports.Count} airports";
            // Refresh command enable states after data load/page change
            UpdatePagingCommands();
            if (packagesChanged)
            {
                await PersistSettingsAsync();
                StartupTrace.Write("RunSearchAsync persisted package changes");
            }
        }
        catch (System.Exception ex)
        {
            Status = "Error loading airports";
            LogLines.Add(ex.Message);
            StartupTrace.Write($"RunSearchAsync exception: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            StartupTrace.Write("RunSearchAsync end");
        }
    }

    private async Task SaveSettingsAsync()
    {
        StartupTrace.Write("SaveSettingsAsync begin");
        if (_apiToken != null)
        {
            var resanitized = SanitizeToken(_apiToken);
            if (resanitized != _apiToken)
            {
                _apiToken = resanitized;
                OnPropertyChanged(nameof(ApiToken));
                (SaveTokenCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            }
        }

        if (!IsValidToken(ApiToken))
        {
            Status = "API Token must start with 'BARS_'";
            LogLines.Add(Status);
            StartupTrace.Write("SaveSettingsAsync invalid token");
            return;
        }

        await PersistSettingsAsync();
        Status = "Settings saved";
        // Update baseline so save button disables until another change
        _originalApiToken = _apiToken;
        (SaveTokenCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        StartupTrace.Write("SaveSettingsAsync complete");
    }

    private async void AirportRowOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AirportRowViewModel.SelectedPackage) && sender is AirportRowViewModel row && row.SelectedPackage != null)
        {
            _savedPackages[row.ICAO] = row.SelectedPackage.Name;
            // Fire and forget save to persist selection quickly without blocking UI
            try { SceneryService.Instance.SetSelectedPackage(row.ICAO, row.SelectedPackage.Name); } catch { }
            try { await PersistSettingsAsync(); StartupTrace.Write($"PersistSettingsAsync after selection {row.ICAO}"); }
            catch (Exception ex) { LogLines.Add(ex.Message); StartupTrace.Write($"PersistSettingsAsync error: {ex.Message}"); }
        }
    }

    private async Task RefreshFromStateAsync()
    {
        var state = _simManager.LatestState;
        var connector = _simManager.ActiveConnector;

        if (state is { } sample)
        {
            OnGround = sample.OnGround;
            Latitude = sample.Latitude;
            Longitude = sample.Longitude;

            var cached = _nearestService.GetCachedNearest(Latitude, Longitude);
            if (cached != null)
            {
                ClosestAirport = cached;
            }
            else
            {
                try
                {
                    var resolved = await _nearestService.ResolveAndCacheAsync(Latitude, Longitude);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        ClosestAirport = resolved;
                    }
                }
                catch
                {
                }
            }
        }
        else
        {
            ClosestAirport = "Unknown";
        }

        var connectorReady = connector?.IsConnected == true;
        if (connectorReady && state != null)
        {
            SimulatorName = connector!.DisplayName;
            SimulatorConnected = true;
        }
        else
        {
            SimulatorConnected = false;
            SimulatorName = "Not Connected";
            if (!connectorReady)
            {
                ClosestAirport = "Unknown";
            }
        }
    }

    // Called externally when a backend websocket message received (wire from AirportWebSocketManager)
    public void NotifyServerMessage()
    {
        RunOnDispatcher(() =>
        {
            _lastServerMessageUtc = DateTime.UtcNow;
            if (!ServerConnected)
            {
                ServerConnected = true;
            }
            if (!string.IsNullOrEmpty(ServerStatusDetail))
            {
                ServerStatusDetail = string.Empty;
                OnPropertyChanged(nameof(ServerStatusDetail));
                OnPropertyChanged(nameof(ServerStatusText));
            }
        });
    }

    public void NotifyServerConnected()
    {
        RunOnDispatcher(() =>
        {
            ServerConnected = true;
            ServerStatusDetail = string.Empty;
            OnPropertyChanged(nameof(ServerStatusDetail));
            OnPropertyChanged(nameof(ServerStatusText));
            _lastServerMessageUtc = DateTime.UtcNow;
        });
    }

    public void NotifyServerError(int code)
    {
        RunOnDispatcher(() =>
        {
            ServerConnected = false;
            ServerStatusDetail = code switch
            {
                401 => "Invalid API token",
                403 => "Not connected to VATSIM",
                _ => "Server unavailable"
            };
            OnPropertyChanged(nameof(ServerStatusDetail));
            OnPropertyChanged(nameof(ServerStatusText));
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string? SanitizeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Remove all whitespace characters anywhere in the string
        var cleaned = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return cleaned;
    }

    private static bool IsValidToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return true; // Allow empty token (user may clear it)
        return token.StartsWith("BARS_", StringComparison.Ordinal);
    }

    private bool CanSaveToken()
    {
        // Only allow save if the token value has changed from last saved AND is valid (or user is clearing a previously non-empty token)
        var current = ApiToken;
        var changed = current != _originalApiToken;
        if (!changed) return false;
        // Allow clearing (current null/empty) or valid token format
        return string.IsNullOrWhiteSpace(current) || IsValidToken(current);
    }

    private async void UiPollOnTick(object? sender, EventArgs e)
    {
        try { await RefreshFromStateAsync(); }
        catch (Exception ex) { LogLines.Add(ex.Message); }
    }

    private void ServerTimerOnTick(object? sender, EventArgs e)
    {
        if (_lastServerMessageUtc == DateTime.MinValue) return;
        if ((DateTime.UtcNow - _lastServerMessageUtc) > TimeSpan.FromSeconds(90))
        {
            ServerConnected = false;
        }
    }

    private async Task PersistSettingsAsync()
    {
        StartupTrace.Write("PersistSettingsAsync begin");
        await _settingsSaveGate.WaitAsync();
        try
        {
            var copy = new Dictionary<string, string>(_savedPackages, StringComparer.OrdinalIgnoreCase);
            await _settingsStore.SaveAsync(new ClientSettings(ApiToken, copy));
            StartupTrace.Write("PersistSettingsAsync save complete");
        }
        finally
        {
            _settingsSaveGate.Release();
            StartupTrace.Write("PersistSettingsAsync end");
        }
    }

    private static void RunOnDispatcher(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }
}

public sealed class AirportRowViewModel : INotifyPropertyChanged
{
    private readonly BARS_Client_V2.Domain.Airport _airport;
    private BARS_Client_V2.Domain.SceneryPackage? _selected;
    public string ICAO => _airport.ICAO;
    public IReadOnlyList<BARS_Client_V2.Domain.SceneryPackage> SceneryPackages => _airport.SceneryPackages;
    public BARS_Client_V2.Domain.SceneryPackage? SelectedPackage { get => _selected; set { if (value != _selected) { _selected = value; OnPropertyChanged(); } } }
    public AirportRowViewModel(BARS_Client_V2.Domain.Airport airport) { _airport = airport; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class DelegateCommand : ICommand
{
    private readonly Func<object?, Task> _executeAsync;
    private readonly Predicate<object?>? _canExecute;
    public DelegateCommand(Func<object?, Task> executeAsync, Predicate<object?>? canExecute = null) { _executeAsync = executeAsync; _canExecute = canExecute; }
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public async void Execute(object? parameter) { await _executeAsync(parameter); }
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

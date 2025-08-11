using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Timers;
using Timer = System.Timers.Timer; // Disambiguate from System.Threading.Timer
using BARS_Client_V2.Application;
using BARS_Client_V2.Domain;
using BARS_Client_V2.Services;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Linq;

namespace BARS_Client_V2.Presentation.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly SimulatorManager _simManager;
    private readonly Timer _uiPoll;
    private string _closestAirport = "ZZZZ";
    private bool _onGround;
    private string _simulatorName = "(none)";
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
        _simManager = simManager;
        _nearestService = nearestService;
        _airportRepo = airportRepository;
        _settingsStore = settingsStore;
        _uiPoll = new Timer(1000);
        _uiPoll.Elapsed += (_, _) => RefreshFromState();
        _uiPoll.Start();

        // Periodically evaluate server connection staleness (if no messages for 30s, mark disconnected)
        var serverTimer = new Timer(5000);
        serverTimer.Elapsed += (_, _) =>
        {
            // Heartbeat is every 60s; allow >60s (90s) before marking disconnected to avoid flicker.
            if (_lastServerMessageUtc != DateTime.MinValue && (DateTime.UtcNow - _lastServerMessageUtc) > TimeSpan.FromSeconds(90))
            {
                ServerConnected = false;
            }
        };
        serverTimer.Start();

        SearchCommand = new DelegateCommand(async _ => await RunSearchAsync(resetPage: true));
        NextPageCommand = new DelegateCommand(async _ => { CurrentPage++; await RunSearchAsync(); }, _ => CanChangePage(+1));
        PrevPageCommand = new DelegateCommand(async _ => { CurrentPage--; await RunSearchAsync(); }, _ => CanChangePage(-1));
        SaveTokenCommand = new DelegateCommand(async _ => await SaveSettingsAsync(), _ => CanSaveToken());

        // Kick off async load of settings + initial data
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var settings = await _settingsStore.LoadAsync();
        // Sanitize and store original token baseline
        _originalApiToken = SanitizeToken(settings.ApiToken);
        _apiToken = _originalApiToken; // set backing field directly to avoid redundant raise
        OnPropertyChanged(nameof(ApiToken));
        (SaveTokenCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        _savedPackages = settings.AirportPackages ?? new Dictionary<string, string>();
        await RunSearchAsync(resetPage: true);
    }

    private IDictionary<string, string> _savedPackages = new Dictionary<string, string>();

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
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            Status = "Searching...";
            if (resetPage) CurrentPage = 1;
            var (items, total) = await _airportRepo.SearchAsync(SearchText, CurrentPage, _pageSize);
            TotalCount = total;
            Airports.Clear();
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
                    row.SelectedPackage = a.SceneryPackages.First();
                    if (!_savedPackages.ContainsKey(a.ICAO))
                    {
                        _savedPackages[a.ICAO] = row.SelectedPackage.Name;
                        // Fire and forget save (debounced vs per-change not critical given infrequent list rebuild)
                        _ = _settingsStore.SaveAsync(new ClientSettings(ApiToken, _savedPackages));
                    }
                    // AirportRowOnPropertyChanged handler will persist selection via PropertyChanged event
                }
                row.PropertyChanged += AirportRowOnPropertyChanged;
                Airports.Add(row);
            }
            Status = $"Loaded {Airports.Count} airports";
            // Refresh command enable states after data load/page change
            UpdatePagingCommands();
        }
        catch (System.Exception ex)
        {
            Status = "Error loading airports";
            LogLines.Add(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        // Persist current selections (all visible) and last selected (most recently changed or first with a selection)
        var firstSelected = Airports.FirstOrDefault(a => a.SelectedPackage != null);
        var icao = firstSelected?.ICAO;
        var pkg = firstSelected?.SelectedPackage?.Name;
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
            return;
        }

        await _settingsStore.SaveAsync(new ClientSettings(ApiToken, _savedPackages));
        Status = "Settings saved";
        // Update baseline so save button disables until another change
        _originalApiToken = _apiToken;
        (SaveTokenCommand as DelegateCommand)?.RaiseCanExecuteChanged();
    }

    private void AirportRowOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AirportRowViewModel.SelectedPackage) && sender is AirportRowViewModel row && row.SelectedPackage != null)
        {
            _savedPackages[row.ICAO] = row.SelectedPackage.Name;
            // Fire and forget save to persist selection quickly without blocking UI
            _ = _settingsStore.SaveAsync(new ClientSettings(ApiToken, _savedPackages));
        }
    }

    private void RefreshFromState()
    {
        var state = _simManager.LatestState;
        var connector = _simManager.ActiveConnector;
        if (state != null)
        {
            OnGround = state.OnGround;
            Latitude = state.Latitude;
            Longitude = state.Longitude;
            var cached = _nearestService.GetCachedNearest(Latitude, Longitude);
            if (cached != null)
            {
                ClosestAirport = cached;
            }
            else
            {
                _ = _nearestService.ResolveAndCacheAsync(Latitude, Longitude)
                    .ContinueWith(t =>
                    {
                        if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result != null)
                        {
                            ClosestAirport = t.Result;
                        }
                    });
            }
        }
        if (connector != null)
        {
            SimulatorName = connector.DisplayName;
            SimulatorConnected = connector.IsConnected;
        }
        else
        {
            SimulatorConnected = false;
        }
    }

    // Called externally when a backend websocket message received (wire from AirportWebSocketManager)
    public void NotifyServerMessage()
    {
        _lastServerMessageUtc = DateTime.UtcNow;
        if (!ServerConnected)
        {
            ServerConnected = true;
        }
        if (!string.IsNullOrEmpty(ServerStatusDetail))
        {
            ServerStatusDetail = string.Empty; OnPropertyChanged(nameof(ServerStatusDetail)); OnPropertyChanged(nameof(ServerStatusText));
        }
    }

    public void NotifyServerConnected()
    {
        ServerConnected = true;
        ServerStatusDetail = string.Empty; OnPropertyChanged(nameof(ServerStatusDetail)); OnPropertyChanged(nameof(ServerStatusText));
        _lastServerMessageUtc = DateTime.UtcNow;
    }

    public void NotifyServerError(int code)
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

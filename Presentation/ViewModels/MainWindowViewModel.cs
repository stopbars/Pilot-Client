using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
using BARS_Client_V2.Infrastructure.Networking;
using BARS_Client_V2.Infrastructure.Simulators.Msfs;

namespace BARS_Client_V2.Presentation.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly SimulatorManager _simManager;
    private readonly MsfsPointController? _pointController;
    private readonly AirportStateHub? _stateHub;
    private readonly DispatcherTimer _uiPoll;
    private readonly DispatcherTimer _serverTimer;
    private readonly DispatcherTimer _searchDebounce;
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
    private bool _resetPageOnNextSearch;
    private string? _apiToken;
    private string? _originalApiToken; // tracks last saved (sanitized) token
    private string _status = "Ready";
    private bool _isBusy;
    private bool _serverConnected; // backend websocket
    private DateTime _lastServerMessageUtc;
    private bool _autoMinimizeOnStart;
    private bool _serverOfflineMode;
    private ClientSettings? _preloadedSettings;
    private bool _initializationStarted;
    private bool _debugModeActive;
    private bool _testingModeActive;
    private int _debugStateId;
    private string? _debugPointId;
    private string? _testingAirport;
    private string _debugModeTextCache = string.Empty;
    private int _selectedSimulatorIndex; // 0 = MSFS 2024, 1 = MSFS 2020
    private string? _msfsNeedsRestartSim;
    private const int ApiTokenLength = 69;

    public ObservableCollection<AirportRowViewModel> Airports { get; } = new();

    /// <summary>
    /// Available simulators for the toggle.
    /// </summary>
    public string[] SimulatorOptions { get; } = { "MSFS 2024", "MSFS 2020" };

    /// <summary>
    /// Index of the currently selected simulator for configuration (0 = MSFS 2024, 1 = MSFS 2020).
    /// Changing this will refresh the airport list to show packages for that simulator.
    /// This is separate from the actual connected simulator.
    /// </summary>
    public int SelectedSimulatorIndex
    {
        get => _selectedSimulatorIndex;
        set
        {
            if (value == _selectedSimulatorIndex) return;
            _selectedSimulatorIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedSimulatorDisplay));

            // Update SceneryService ConfiguredSimulator (not CurrentSimulator - that's set by SimConnect)
            var newSim = value == 0 ? "msfs2024" : "msfs2020";
            var wasChanged = !string.Equals(SceneryService.Instance.ConfiguredSimulator, newSim, StringComparison.OrdinalIgnoreCase);
            SceneryService.Instance.ConfiguredSimulator = newSim;

            // If the event didn't fire (was already the same value), manually trigger refresh
            if (!wasChanged)
            {
                _ = ForceRefreshAirportsAsync(newSim);
            }
            // Otherwise the ConfiguredSimulatorChanged event will trigger a refresh
        }
    }

    private async Task ForceRefreshAirportsAsync(string simulator)
    {
        Status = $"Loading packages for {(simulator == "msfs2024" ? "MSFS 2024" : "MSFS 2020")}...";

        // Clear the airports list to force a complete refresh
        foreach (var row in Airports)
        {
            row.PropertyChanged -= AirportRowOnPropertyChanged;
        }
        Airports.Clear();

        await RunSearchAsync(resetPage: true);
    }

    /// <summary>
    /// Display name of the currently configured simulator (for UI).
    /// </summary>
    public string SelectedSimulatorDisplay => _selectedSimulatorIndex == 0 ? "MSFS 2024" : "MSFS 2020";

    public string ClosestAirport { get => _closestAirport; private set { if (value != _closestAirport) { _closestAirport = value; OnPropertyChanged(); } } }

    public bool OnGround
    {
        get => _onGround;
        set { if (value != _onGround) { _onGround = value; OnPropertyChanged(); OnPropertyChanged(nameof(OnGroundText)); } }
    }

    public string OnGroundText => OnGround ? "On Ground" : "Airborne";
    public string SimulatorName { get => _simulatorName; set { if (value != _simulatorName) { _simulatorName = value; OnPropertyChanged(); } } }
    public bool SimulatorConnected { get => _simConnected; set { if (value != _simConnected) { _simConnected = value; if (!value) { _msfsNeedsRestartSim = null; if (_debugModeActive) DebugModeActive = false; } OnPropertyChanged(); OnPropertyChanged(nameof(SimulatorConnectionText)); OnPropertyChanged(nameof(SimulatorStatusColor)); OnPropertyChanged(nameof(MsfsNeedsRestartVisibility)); OnPropertyChanged(nameof(MsfsNeedsRestartText)); } } }
    public string SimulatorConnectionText => SimulatorConnected ? "Connected" : "Disconnected";
    public string SimulatorStatusColor => SimulatorConnected ? "LimeGreen" : "Gray";
    public bool ServerConnected
    {
        get => _serverConnected;
        private set
        {
            if (value == _serverConnected)
            {
                return;
            }

            _serverConnected = value;
            if (value && _serverOfflineMode)
            {
                _serverOfflineMode = false;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(ServerStatusText));
            OnPropertyChanged(nameof(ServerStatusColor));
        }
    }

    public string ServerStatusText => ServerConnected
        ? "Connected"
        : (string.IsNullOrEmpty(ServerStatusDetail) ? "Disconnected" : ServerStatusDetail);

    public string ServerStatusColor => ServerConnected ? "LimeGreen" : "Gray";
    public string ServerStatusDetail { get; private set; } = ""; // optional reason
    public double Latitude { get => _latitude; set { if (value != _latitude) { _latitude = value; OnPropertyChanged(); } } }
    public double Longitude { get => _longitude; set { if (value != _longitude) { _longitude = value; OnPropertyChanged(); } } }

    /// <summary>
    /// Indicates whether debug/test mode is currently active.
    /// </summary>
    public bool DebugModeActive
    {
        get => _debugModeActive || _testingModeActive;
        private set
        {
            var wasActive = DebugModeActive;
            if (value != _debugModeActive)
            {
                _debugModeActive = value;

                if (DebugModeActive != wasActive)
                {
                    OnPropertyChanged();
                }

                if (DebugModeActive)
                {
                    // Immediately update text when turning on
                    UpdateDebugModeText();
                }
                // Text clears on next enable, not on disable (prevents text vanishing mid-animation)
                
                OnPropertyChanged(nameof(DebugModeVisibility));
            }
        }
    }

    /// <summary>
    /// Current debug state ID being displayed.
    /// </summary>
    public int DebugStateId
    {
        get => _debugStateId;
        private set
        {
            if (value != _debugStateId)
            {
                _debugStateId = value;
                OnPropertyChanged();
                UpdateDebugModeText();
            }
        }
    }

    /// <summary>
    /// Text to display when debug mode is active.
    /// </summary>
    public string DebugModeText => _debugModeTextCache;
    
    private void UpdateDebugModeText()
    {
        // Only update when active to prevent text flash during fade-out
        if (!DebugModeActive) return;

        if (_testingModeActive)
        {
            _debugModeTextCache = "Testing Mode";
            OnPropertyChanged(nameof(DebugModeText));
            return;
        }

        _debugModeTextCache = $"Debug Mode  —  State: {_debugStateId}";
        OnPropertyChanged(nameof(DebugModeText));
    }

    /// <summary>
    /// Visibility of the debug mode indicator.
    /// </summary>
    public string DebugModeVisibility => DebugModeActive ? "Visible" : "Collapsed";
    public bool IsTestingModeActive => _testingModeActive;

    /// <summary>
    /// Visibility of the MSFS restart banner.
    /// </summary>
    public string MsfsNeedsRestartVisibility => !string.IsNullOrEmpty(_msfsNeedsRestartSim) ? "Visible" : "Collapsed";

    /// <summary>
    /// Text for the MSFS restart banner.
    /// </summary>
    public string MsfsNeedsRestartText => string.IsNullOrEmpty(_msfsNeedsRestartSim)
        ? string.Empty
        : $"Restart {(_msfsNeedsRestartSim == "msfs2024" ? "MSFS 2024" : "MSFS 2020")} for removal changes to apply";

    private void SetMsfsNeedsRestart(string simulator)
    {
        if (_msfsNeedsRestartSim != simulator)
        {
            _msfsNeedsRestartSim = simulator;
            OnPropertyChanged(nameof(MsfsNeedsRestartVisibility));
            OnPropertyChanged(nameof(MsfsNeedsRestartText));
        }
    }

    public ObservableCollection<string> LogLines { get; } = new();

    private readonly INearestAirportService _nearestService;
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);

    public string? SearchText
    {
        get => _searchText;
        set
        {
            if (value == _searchText)
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
            ScheduleSearch(resetPage: true);
        }
    }
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
                OnPropertyChanged(nameof(ApiTokenValidationMessage));
                (SaveTokenCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
    public string? ApiTokenValidationMessage => GetApiTokenValidationMessage(ApiToken);
    public string Status { get => _status; private set { if (value != _status) { _status = value; OnPropertyChanged(); } } }
    public bool IsBusy { get => _isBusy; private set { if (value != _isBusy) { _isBusy = value; OnPropertyChanged(); } } }
    public bool AutoMinimizeOnStart
    {
        get => _autoMinimizeOnStart;
        set
        {
            if (value == _autoMinimizeOnStart) return;
            _autoMinimizeOnStart = value;
            OnPropertyChanged();
            _ = PersistSettingsAsync();
        }
    }
    public int CurrentPage { get => _currentPage; private set { if (value != _currentPage) { _currentPage = value; OnPropertyChanged(); OnPropertyChanged(nameof(PageInfo)); UpdatePagingCommands(); } } }
    public int TotalCount { get => _totalCount; private set { if (value != _totalCount) { _totalCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PageInfo)); UpdatePagingCommands(); } } }
    public string PageInfo => $"Page {CurrentPage} of {Math.Max(1, (int)Math.Ceiling(TotalCount / (double)_pageSize))}";

    // Commands (simple DelegateCommand implementation inline)
    public ICommand NextPageCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand SaveTokenCommand { get; }
    public ICommand ToggleDebugModeCommand { get; }
    public ICommand EndActiveModeCommand { get; }

    public MainWindowViewModel(SimulatorManager simManager, INearestAirportService nearestService, IAirportRepository airportRepository, ISettingsStore settingsStore, MsfsPointController? pointController = null, AirportStateHub? stateHub = null)
    {
        StartupTrace.Write("MainWindowViewModel ctor");
        _simManager = simManager;
        _pointController = pointController;
        _stateHub = stateHub;
        _nearestService = nearestService;
        _airportRepo = airportRepository;
        _settingsStore = settingsStore;
        _uiPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiPoll.Tick += UiPollOnTick;
        _uiPoll.Start();

        _serverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _serverTimer.Tick += ServerTimerOnTick;
        _serverTimer.Start();

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
        _searchDebounce.Tick += SearchDebounceOnTick;

        NextPageCommand = new DelegateCommand(async _ => { CurrentPage++; await RunSearchAsync(); }, _ => CanChangePage(+1));
        PrevPageCommand = new DelegateCommand(async _ => { CurrentPage--; await RunSearchAsync(); }, _ => CanChangePage(-1));
        SaveTokenCommand = new DelegateCommand(async _ => await SaveSettingsAsync(), _ => CanSaveToken());
        ToggleDebugModeCommand = new DelegateCommand(_ => { ToggleDebugMode(); return Task.CompletedTask; });
        EndActiveModeCommand = new DelegateCommand(async _ => await EndActiveModeAsync(), _ => DebugModeActive);

        // Subscribe to debug mode changes
        if (_pointController != null)
        {
            _pointController.DebugModeChanged += OnDebugModeChanged;
        }

        if (stateHub != null)
        {
            stateHub.TestingModeChanged += OnTestingModeChanged;
        }

        // Subscribe to configured simulator changes to refresh airport list when toggle changes
        SceneryService.Instance.ConfiguredSimulatorChanged += OnConfiguredSimulatorChanged;
    }

    private void OnConfiguredSimulatorChanged(string newSimulator)
    {
        // Refresh the airport list when the configured simulator changes (user clicked toggle)
        RunOnDispatcher(async () =>
        {
            // Update the toggle to reflect the new simulator (in case it was set programmatically)
            var newIndex = string.Equals(newSimulator, "msfs2024", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            if (_selectedSimulatorIndex != newIndex)
            {
                _selectedSimulatorIndex = newIndex;
                OnPropertyChanged(nameof(SelectedSimulatorIndex));
                OnPropertyChanged(nameof(SelectedSimulatorDisplay));
            }

            Status = $"Loading packages for {(newSimulator == "msfs2024" ? "MSFS 2024" : "MSFS 2020")}...";

            // Clear the airports list to force a complete refresh with new simulator's data
            foreach (var row in Airports)
            {
                row.PropertyChanged -= AirportRowOnPropertyChanged;
            }
            Airports.Clear();

            await RunSearchAsync(resetPage: true);
        });
    }

    private HashSet<string>? _removalsUpdatedSims;
    private string? _msfs2020RemovalsEtag;
    private string? _msfs2024RemovalsEtag;

    public void SeedSettings(ClientSettings settings, HashSet<string>? removalsUpdatedSims = null)
    {
        _preloadedSettings = settings;
        _removalsUpdatedSims = removalsUpdatedSims;
        _msfs2020RemovalsEtag = settings.Msfs2020RemovalsEtag;
        _msfs2024RemovalsEtag = settings.Msfs2024RemovalsEtag;
        _autoMinimizeOnStart = settings.AutoMinimizeOnStart;
        OnPropertyChanged(nameof(AutoMinimizeOnStart));
    }

    public async Task InitializeAsync()
    {
        if (_initializationStarted)
        {
            return;
        }
        _initializationStarted = true;
        StartupTrace.Write("InitializeAsync start");
        ClientSettings settings;
        if (_preloadedSettings is { } preloaded)
        {
            settings = preloaded;
            _preloadedSettings = null;
        }
        else
        {
            settings = await _settingsStore.LoadAsync();
            _msfs2020RemovalsEtag = settings.Msfs2020RemovalsEtag;
            _msfs2024RemovalsEtag = settings.Msfs2024RemovalsEtag;
        }
        if (_autoMinimizeOnStart != settings.AutoMinimizeOnStart)
        {
            _autoMinimizeOnStart = settings.AutoMinimizeOnStart;
            OnPropertyChanged(nameof(AutoMinimizeOnStart));
        }
        // Sanitize and store original token baseline
        _originalApiToken = SanitizeToken(settings.ApiToken);
        _apiToken = _originalApiToken; // set backing field directly to avoid redundant raise
        OnPropertyChanged(nameof(ApiToken));
        OnPropertyChanged(nameof(ApiTokenValidationMessage));
        (SaveTokenCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        _savedPackages = settings.AirportPackages != null
            ? new Dictionary<string, string>(settings.AirportPackages, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _savedRemovalToggles = settings.SceneryRemovalToggles != null
            ? new Dictionary<string, bool>(settings.SceneryRemovalToggles, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        StartupTrace.Write($"InitializeAsync settings loaded; packages={_savedPackages.Count}");

        if (_removalsUpdatedSims != null && _removalsUpdatedSims.Count > 0)
        {
            var runningSim = GetRunningMsfsSim();
            if (runningSim != null && _removalsUpdatedSims.Contains(runningSim))
            {
                SetMsfsNeedsRestart(runningSim);
            }
            _removalsUpdatedSims = null;
        }

        _ = SceneryService.Instance.SyncAllRemovalStatesAsync(_savedPackages, _savedRemovalToggles)
            .ContinueWith(t => { var runningSim = GetRunningMsfsSim(); if (runningSim != null && t.Result.Contains(runningSim)) RunOnDispatcher(() => SetMsfsNeedsRestart(runningSim)); });

        await RefreshFromStateAsync();
        StartupTrace.Write("InitializeAsync state refreshed");
        await RunSearchAsync(resetPage: true);
        StartupTrace.Write("InitializeAsync completed initial search");
    }

    private IDictionary<string, string> _savedPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private IDictionary<string, bool> _savedRemovalToggles = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    private bool _suppressSelectionNotifications;

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
        _searchDebounce.Stop();
        StartupTrace.Write($"RunSearchAsync begin reset={resetPage}");
        if (IsBusy)
        {
            ScheduleSearch(resetPage);
            return;
        }

        try
        {
            IsBusy = true;
            Status = "Searching...";
            if (resetPage) CurrentPage = 1;

            var (items, total) = await _airportRepo.SearchAsync(SearchText, CurrentPage, _pageSize);
            StartupTrace.Write($"RunSearchAsync results items={items.Count} total={total}");
            TotalCount = total;

            if (IsContentUnchanged(items))
            {
                Status = $"Loaded {Airports.Count} airports";
                UpdatePagingCommands();
                return;
            }

            var packagesChanged = false;
            var existingByIcao = Airports.ToDictionary(r => r.ICAO, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < items.Count; i++)
            {
                var airport = items[i];
                seen.Add(airport.ICAO);

                if (i < Airports.Count && string.Equals(Airports[i].ICAO, airport.ICAO, StringComparison.OrdinalIgnoreCase))
                {
                    var row = Airports[i];
                    row.UpdateSource(airport);
                    packagesChanged |= SyncPackageSelection(row, airport);
                    continue;
                }

                if (existingByIcao.TryGetValue(airport.ICAO, out var existingRow))
                {
                    var currentIndex = Airports.IndexOf(existingRow);
                    if (currentIndex != i)
                    {
                        Airports.Move(currentIndex, i);
                    }
                    existingRow.UpdateSource(airport);
                    packagesChanged |= SyncPackageSelection(existingRow, airport);
                    continue;
                }

                var newRow = new AirportRowViewModel(airport);
                newRow.PropertyChanged += AirportRowOnPropertyChanged;
                if (i <= Airports.Count)
                {
                    Airports.Insert(i, newRow);
                }
                else
                {
                    Airports.Add(newRow);
                }
                packagesChanged |= SyncPackageSelection(newRow, airport);
            }

            for (var index = Airports.Count - 1; index >= 0; index--)
            {
                var row = Airports[index];
                if (seen.Contains(row.ICAO))
                {
                    continue;
                }

                row.PropertyChanged -= AirportRowOnPropertyChanged;
                Airports.RemoveAt(index);
            }

            Status = $"Loaded {Airports.Count} airports";
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

    private bool IsContentUnchanged(IReadOnlyList<BARS_Client_V2.Domain.Airport> items)
    {
        if (Airports.Count != items.Count)
        {
            return false;
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (!Airports[i].IsEquivalentTo(items[i]))
            {
                return false;
            }
        }

        return true;
    }

    private bool SyncPackageSelection(AirportRowViewModel row, BARS_Client_V2.Domain.Airport airport)
    {
        var changed = false;
        var selectionChanged = false;

        var configuredSim = SceneryService.Instance.ConfiguredSimulator;
        var key = $"{airport.ICAO}:{configuredSim}";

        if (_savedPackages.TryGetValue(key, out var savedPackage))
        {
            var match = airport.SceneryPackages.FirstOrDefault(p => string.Equals(p.Name, savedPackage, StringComparison.Ordinal));
            if (match != null)
            {
                selectionChanged = !string.Equals(row.SelectedPackage?.Name, match.Name, StringComparison.Ordinal);
                if (selectionChanged || !ReferenceEquals(row.SelectedPackage, match))
                {
                    _suppressSelectionNotifications = true;
                    try { row.SelectedPackage = match; }
                    finally { _suppressSelectionNotifications = false; }
                }

                if (selectionChanged)
                {
                    try { SceneryService.Instance.SetSelectedPackage(airport.ICAO, configuredSim, match.Name); }
                    catch (Exception ex) { StartupTrace.Write($"SceneryService.SetSelectedPackage error: {ex.Message}"); }
                }

                SyncRemovalToggle(row, airport.ICAO, configuredSim, match.Name);
                return changed;
            }

            _savedPackages.Remove(key);
            changed = true;
        }

        if (airport.SceneryPackages.Count > 0)
        {
            var defaultPackage = airport.SceneryPackages[0];
            selectionChanged = !string.Equals(row.SelectedPackage?.Name, defaultPackage.Name, StringComparison.Ordinal);

            if (selectionChanged || !ReferenceEquals(row.SelectedPackage, defaultPackage))
            {
                _suppressSelectionNotifications = true;
                try { row.SelectedPackage = defaultPackage; }
                finally { _suppressSelectionNotifications = false; }
            }

            if (!_savedPackages.TryGetValue(key, out var existing) || !string.Equals(existing, defaultPackage.Name, StringComparison.Ordinal))
            {
                _savedPackages[key] = defaultPackage.Name;
                changed = true;
            }

            if (selectionChanged)
            {
                try { SceneryService.Instance.SetSelectedPackage(airport.ICAO, configuredSim, defaultPackage.Name); }
                catch (Exception ex) { StartupTrace.Write($"SceneryService.SetSelectedPackage error: {ex.Message}"); }
            }

            SyncRemovalToggle(row, airport.ICAO, configuredSim, defaultPackage.Name);
        }

        return changed;
    }

    private void SyncRemovalToggle(AirportRowViewModel row, string icao, string simulator, string packageName)
    {
        var toggleKey = $"{icao}:{simulator}:{packageName}";
        var enabled = !_savedRemovalToggles.TryGetValue(toggleKey, out var saved) || saved;
        if (row.SceneryRemovalEnabled != enabled)
        {
            _suppressSelectionNotifications = true;
            try { row.SceneryRemovalEnabled = enabled; }
            finally { _suppressSelectionNotifications = false; }
        }

        _ = SceneryService.Instance.ApplySceneryRemovalAsync(icao, simulator, packageName, enabled);
    }

    private void ScheduleSearch(bool resetPage)
    {
        if (resetPage)
        {
            _resetPageOnNextSearch = true;
        }

        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private async void SearchDebounceOnTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        var reset = _resetPageOnNextSearch;
        _resetPageOnNextSearch = false;
        await RunSearchAsync(reset);
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
                OnPropertyChanged(nameof(ApiTokenValidationMessage));
                (SaveTokenCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            }
        }

        if (!IsValidToken(ApiToken))
        {
            Status = ApiTokenValidationMessage ?? "API tokens start with BARS_.";
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
        if (_suppressSelectionNotifications)
        {
            return;
        }

        if (sender is not AirportRowViewModel row) return;
        var configuredSim = SceneryService.Instance.ConfiguredSimulator;

        if (e.PropertyName == nameof(AirportRowViewModel.SelectedPackage) && row.SelectedPackage != null)
        {
            var key = $"{row.ICAO}:{configuredSim}";
            _savedPackages[key] = row.SelectedPackage.Name;
            try { SceneryService.Instance.SetSelectedPackage(row.ICAO, configuredSim, row.SelectedPackage.Name); }
            catch (Exception ex) { StartupTrace.Write($"SceneryService.SetSelectedPackage error: {ex.Message}"); }

            if (row.SceneryRemovalEnabled)
            {
                var changed = await SceneryService.Instance.ApplySceneryRemovalAsync(row.ICAO, configuredSim, row.SelectedPackage.Name, true);
                if (changed) { var runningSim = GetRunningMsfsSim(); if (runningSim != null && string.Equals(runningSim, configuredSim, StringComparison.OrdinalIgnoreCase)) SetMsfsNeedsRestart(runningSim); }
            }

            try { await PersistSettingsAsync(); StartupTrace.Write($"PersistSettingsAsync after selection {row.ICAO}"); }
            catch (Exception ex) { LogLines.Add(ex.Message); StartupTrace.Write($"PersistSettingsAsync error: {ex.Message}"); }
        }
        else if (e.PropertyName == nameof(AirportRowViewModel.SceneryRemovalEnabled))
        {
            var packageName = row.SelectedPackage?.Name ?? string.Empty;
            var toggleKey = $"{row.ICAO}:{configuredSim}:{packageName}";
            _savedRemovalToggles[toggleKey] = row.SceneryRemovalEnabled;

            var changed = await SceneryService.Instance.ApplySceneryRemovalAsync(row.ICAO, configuredSim, packageName, row.SceneryRemovalEnabled);
            if (changed) { var runningSim = GetRunningMsfsSim(); if (runningSim != null && string.Equals(runningSim, configuredSim, StringComparison.OrdinalIgnoreCase)) SetMsfsNeedsRestart(runningSim); }

            try { await PersistSettingsAsync(); StartupTrace.Write($"PersistSettingsAsync after toggle {row.ICAO}"); }
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
            if (_serverOfflineMode)
            {
                _serverOfflineMode = false;
                OnPropertyChanged(nameof(ServerStatusText));
                OnPropertyChanged(nameof(ServerStatusColor));
            }
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
            if (_serverOfflineMode)
            {
                _serverOfflineMode = false;
                OnPropertyChanged(nameof(ServerStatusText));
                OnPropertyChanged(nameof(ServerStatusColor));
            }
            ServerConnected = true;
            ServerStatusDetail = string.Empty;
            OnPropertyChanged(nameof(ServerStatusDetail));
            OnPropertyChanged(nameof(ServerStatusText));
            _lastServerMessageUtc = DateTime.UtcNow;
        });
    }

    public void NotifyServerOfflineMode(bool isOffline)
    {
        RunOnDispatcher(() =>
        {
            if (_serverOfflineMode == isOffline)
            {
                return;
            }

            _serverOfflineMode = isOffline;
            if (isOffline && ServerConnected)
            {
                ServerConnected = false;
            }

            if (isOffline)
            {
                if (!string.Equals(ServerStatusDetail, "No VATSIM Connection", StringComparison.Ordinal))
                {
                    ServerStatusDetail = "No VATSIM Connection";
                    OnPropertyChanged(nameof(ServerStatusDetail));
                }
            }

            OnPropertyChanged(nameof(ServerStatusText));
            OnPropertyChanged(nameof(ServerStatusColor));
        });
    }

    public void NotifyOfflineSnapshot()
    {
        RunOnDispatcher(() =>
        {
            _lastServerMessageUtc = DateTime.UtcNow;
            if (!_serverOfflineMode)
            {
                _serverOfflineMode = true;
                if (ServerConnected)
                {
                    ServerConnected = false;
                }
                if (!string.Equals(ServerStatusDetail, "No VATSIM Connection", StringComparison.Ordinal))
                {
                    ServerStatusDetail = "No VATSIM Connection";
                    OnPropertyChanged(nameof(ServerStatusDetail));
                }
                OnPropertyChanged(nameof(ServerStatusText));
                OnPropertyChanged(nameof(ServerStatusColor));
            }
        });
    }

    public void NotifyServerError(int code)
    {
        RunOnDispatcher(() =>
        {
            if (_serverOfflineMode)
            {
                _serverOfflineMode = false;
                OnPropertyChanged(nameof(ServerStatusText));
                OnPropertyChanged(nameof(ServerStatusColor));
            }
            ServerConnected = false;
            ServerStatusDetail = code switch
            {
                401 => "Invalid API token",
                403 => "No VATSIM Connection",
                _ => "Server unavailable"
            };
            OnPropertyChanged(nameof(ServerStatusDetail));
            OnPropertyChanged(nameof(ServerStatusText));
        });
    }

    public void NotifyServerDisconnected(string? reason)
    {
        RunOnDispatcher(() =>
        {
            if (_serverOfflineMode)
            {
                return;
            }

            ServerConnected = false;

            var detail = NormalizeDisconnectDetail(reason);
            if (!string.Equals(ServerStatusDetail, detail, StringComparison.Ordinal))
            {
                ServerStatusDetail = detail;
                OnPropertyChanged(nameof(ServerStatusDetail));
            }

            OnPropertyChanged(nameof(ServerStatusText));
            OnPropertyChanged(nameof(ServerStatusColor));
        });
    }

    private static string NormalizeDisconnectDetail(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return string.Empty;
        }

        if (reason.Contains("token", StringComparison.OrdinalIgnoreCase))
        {
            return reason;
        }

        return string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Toggles debug/test mode for light state cycling.
    /// </summary>
    public void ToggleDebugMode()
    {
        if (_testingModeActive) return;
        if (!SimulatorConnected) return;
        _pointController?.ToggleDebugMode();
    }

    private async Task EndActiveModeAsync()
    {
        if (_testingModeActive && _stateHub != null)
        {
            Status = "Ending Testing Mode...";
            await _stateHub.EndTestingModeAsync();
            return;
        }

        if (_debugModeActive)
        {
            _pointController?.ToggleDebugMode();
        }
    }

    private void OnDebugModeChanged(object? sender, DebugModeChangedEventArgs e)
    {
        RunOnDispatcher(() =>
        {
            DebugModeActive = e.IsDebugMode;
            DebugStateId = e.CurrentStateId;
            _debugPointId = e.ClosestPointId;
            if (e.IsDebugMode)
            {
                Status = $"Debug Mode: Testing point {e.ClosestPointId ?? "(none)"}";
            }
            else
            {
                Status = "Ready";
            }

            (EndActiveModeCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        });
    }

    private void OnTestingModeChanged(AirportStateHub.TestingModeChangedEventArgs e)
    {
        RunOnDispatcher(() =>
        {
            var wasActive = DebugModeActive;
            _testingModeActive = e.IsTestingMode;
            _testingAirport = e.Airport;

            if (DebugModeActive != wasActive)
            {
                OnPropertyChanged(nameof(DebugModeActive));
            }

            if (DebugModeActive)
            {
                UpdateDebugModeText();
            }

            OnPropertyChanged(nameof(DebugModeVisibility));
            OnPropertyChanged(nameof(IsTestingModeActive));
            (EndActiveModeCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            Status = e.IsTestingMode
                ? $"Testing Mode: Loaded {e.Airport ?? "airport"} contribution"
                : "Ready";
        });
    }

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
        return token.StartsWith("BARS_", StringComparison.Ordinal) && token.Length == ApiTokenLength;
    }

    private static string? GetApiTokenValidationMessage(string? token)
    {
        if (string.IsNullOrEmpty(token) || IsValidToken(token)) return null;

        if (LooksLikeJwt(token))
        {
            return "This looks like a v1 token. Enter a BARS_ API token.";
        }

        if (token.StartsWith("BARS_", StringComparison.Ordinal) && token.Length != ApiTokenLength)
        {
            return "API tokens must be 69 characters long.";
        }

        return "API tokens start with BARS_.";
    }

    private static bool LooksLikeJwt(string token)
    {
        var segments = token.Split('.');
        return segments.Length == 3
            && segments.All(segment => segment.Length > 0 && segment.All(IsBase64JwtCharacter));
    }

    private static bool IsBase64JwtCharacter(char character)
    {
        return char.IsLetterOrDigit(character)
            || character == '-'
            || character == '_'
            || character == '+'
            || character == '/'
            || character == '=';
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

        if (!string.IsNullOrEmpty(_msfsNeedsRestartSim) && !IsMsfsRunning())
        {
            SetMsfsNeedsRestart(string.Empty);
        }
    }

    private void ServerTimerOnTick(object? sender, EventArgs e)
    {
        if (_lastServerMessageUtc == DateTime.MinValue) return;
        if ((DateTime.UtcNow - _lastServerMessageUtc) > TimeSpan.FromSeconds(90))
        {
            if (_serverOfflineMode)
            {
                _serverOfflineMode = false;
                OnPropertyChanged(nameof(ServerStatusText));
                OnPropertyChanged(nameof(ServerStatusColor));
            }
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
            var togglesCopy = new Dictionary<string, bool>(_savedRemovalToggles, StringComparer.OrdinalIgnoreCase);
            await _settingsStore.SaveAsync(new ClientSettings(ApiToken, copy, togglesCopy, _autoMinimizeOnStart, _msfs2020RemovalsEtag, _msfs2024RemovalsEtag));
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

    private static bool IsMsfsRunning() => GetRunningMsfsSim() != null;

    /// <summary>
    /// Returns the identifier of the currently running MSFS simulator, or null if none is running.
    /// Returns "msfs2024" for MSFS 2024 or "msfs2020" for MSFS 2020.
    /// </summary>
    private static string? GetRunningMsfsSim()
    {
        try
        {
            if (Process.GetProcessesByName("FlightSimulator2024").Length > 0)
                return "msfs2024";
            if (Process.GetProcessesByName("FlightSimulator").Length > 0)
                return "msfs2020";
            return null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class AirportRowViewModel : INotifyPropertyChanged
{
    private BARS_Client_V2.Domain.Airport _airport;
    private BARS_Client_V2.Domain.SceneryPackage? _selected;
    private bool _sceneryRemovalEnabled = true;
    public string ICAO => _airport.ICAO;
    public string? Name => _airport.Name;
    public IReadOnlyList<BARS_Client_V2.Domain.SceneryPackage> SceneryPackages => _airport.SceneryPackages;
    public BARS_Client_V2.Domain.SceneryPackage? SelectedPackage { get => _selected; set { if (value != _selected) { _selected = value; OnPropertyChanged(); } } }
    public bool SceneryRemovalEnabled { get => _sceneryRemovalEnabled; set { if (value != _sceneryRemovalEnabled) { _sceneryRemovalEnabled = value; OnPropertyChanged(); } } }
    public AirportRowViewModel(BARS_Client_V2.Domain.Airport airport) { _airport = airport; }
    public bool IsEquivalentTo(BARS_Client_V2.Domain.Airport airport) =>
        string.Equals(_airport.ICAO, airport.ICAO, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_airport.Name ?? string.Empty, airport.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && PackagesEqual(_airport.SceneryPackages, airport.SceneryPackages);
    public void UpdateSource(BARS_Client_V2.Domain.Airport airport)
    {
        var packagesChanged = !PackagesEqual(_airport.SceneryPackages, airport.SceneryPackages);
        var currentName = _airport.Name ?? string.Empty;
        var newName = airport.Name ?? string.Empty;
        var nameChanged = !string.Equals(currentName, newName, StringComparison.OrdinalIgnoreCase);
        _airport = airport;
        if (packagesChanged)
        {
            OnPropertyChanged(nameof(SceneryPackages));
        }
        if (nameChanged)
        {
            OnPropertyChanged(nameof(Name));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static bool PackagesEqual(IReadOnlyList<BARS_Client_V2.Domain.SceneryPackage> left, IReadOnlyList<BARS_Client_V2.Domain.SceneryPackage> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Name, right[i].Name, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
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

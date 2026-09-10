using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Application;
using BARS_Client_V2.Infrastructure.Networking;
using BARS_Client_V2.Infrastructure.Settings;
using BARS_Client_V2.Infrastructure.Simulators.XPlane;

namespace BARS_Client_V2.Services
{
    public class SceneryService
    {
        private const string SETTINGS_FILENAME = "settings.json";
        private readonly HttpClient _httpClient;
        private readonly XPlaneLocalRemovalsService _xplaneRemovals;
        // Key format: "ICAO:simulator" (e.g., "YSCB:msfs2020" or "YSCB:msfs2024")
        private Dictionary<string, string> _selectedPackages;
        private readonly object _selectionLock = new();
        private static readonly Lazy<SceneryService> LazyInstance = new(
            () => new SceneryService(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        private string _currentSimulator = "msfs2020"; // Default to 2020 - set by actual SimConnect detection
        private string _configuredSimulator = "msfs2024"; // Which simulator the user is configuring in the UI

        // Cached packages data - fetched once, filtered locally by simulator
        private Dictionary<string, Dictionary<string, List<string>>>? _cachedPackages;
        private readonly SemaphoreSlim _cacheLock = new(1, 1);
        private DateTime _packagesLoadedUtc = DateTime.MinValue;
        private static readonly TimeSpan PackageCacheFreshness = TimeSpan.FromMinutes(5);
        private readonly SemaphoreSlim _msfsRemovalGate = RemovalFileAccess.MsfsGate;
        private readonly SemaphoreSlim _xplaneRemovalGate = new(1, 1);

        // Supported simulators
        public static readonly string[] SupportedSimulators = { "msfs2020", "msfs2024", "xplane" };

        /// <summary>
        /// Gets or sets the current active simulator detected by its connector.
        /// This determines which map to load when actually flying.
        /// Valid values: "msfs2020", "msfs2024", "xplane"
        /// </summary>
        public string CurrentSimulator
        {
            get => _currentSimulator;
            set
            {
                var normalized = value?.ToLowerInvariant() ?? "msfs2020";
                if (Array.IndexOf(SupportedSimulators, normalized) < 0)
                    normalized = "msfs2020";

                if (_currentSimulator != normalized)
                {
                    _currentSimulator = normalized;
                    try { CurrentSimulatorChanged?.Invoke(normalized); } catch { }
                }
            }
        }

        /// <summary>
        /// Gets or sets which simulator the user is configuring in the UI.
        /// This determines which packages are shown in the airport list.
        /// Valid values: "msfs2020", "msfs2024", "xplane"
        /// </summary>
        public string ConfiguredSimulator
        {
            get => _configuredSimulator;
            set
            {
                var normalized = value?.ToLowerInvariant() ?? "msfs2024";
                if (Array.IndexOf(SupportedSimulators, normalized) < 0)
                    normalized = "msfs2024";

                if (_configuredSimulator != normalized)
                {
                    _configuredSimulator = normalized;
                    try { ConfiguredSimulatorChanged?.Invoke(normalized); } catch { }
                }
            }
        }

        /// <summary>
        /// Fired when the actual connected simulator changes (detected by SimConnect).
        /// </summary>
        public event Action<string>? CurrentSimulatorChanged;

        /// <summary>
        /// Fired when the user changes which simulator they're configuring in the UI.
        /// Used to refresh the airport list.
        /// </summary>
        public event Action<string>? ConfiguredSimulatorChanged;

        // Legacy event - kept for compatibility, fires on CurrentSimulator change
        public event Action<string>? SimulatorChanged
        {
            add => CurrentSimulatorChanged += value;
            remove => CurrentSimulatorChanged -= value;
        }

        // Fired when a user changes the selected scenery package for an airport/simulator.
        // Args: (icao, simulator, newPackageName)
        public event Action<string, string, string>? PackageChanged;

        public static SceneryService Instance
        {
            get => LazyInstance.Value;
        }

        private SceneryService()
        {
            _httpClient = new HttpClient();
            _xplaneRemovals = new XPlaneLocalRemovalsService(_httpClient);
            _selectedPackages = LoadSelectedPackages();
        }

        /// <summary>
        /// Gets available packages organized by simulator, then by airport ICAO.
        /// Returns: Dictionary[simulator] -> Dictionary[icao] -> List of package names
        /// Refreshes the cached result periodically.
        /// </summary>
        public async Task<Dictionary<string, Dictionary<string, List<string>>>> GetAvailablePackagesAsync()
        {
            // Return cached data if available
            if (IsPackageCacheFresh())
            {
                return _cachedPackages!;
            }

            await _cacheLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                if (IsPackageCacheFresh())
                {
                    return _cachedPackages!;
                }

                var packages = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);

                // Initialize with supported simulators
                foreach (var sim in SupportedSimulators)
                {
                    packages[sim] = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }

                var contributions = await ApprovedContributionsCache
                    .GetAsync(_httpClient)
                    .ConfigureAwait(false);
                foreach (var contribution in contributions)
                {
                    if (string.IsNullOrWhiteSpace(contribution.AirportIcao) ||
                        string.IsNullOrWhiteSpace(contribution.PackageName))
                    {
                        continue;
                    }

                    var simulator = string.IsNullOrWhiteSpace(contribution.Simulator)
                        ? "msfs2020"
                        : contribution.Simulator.ToLowerInvariant();
                    if (!packages.TryGetValue(simulator, out var airports))
                    {
                        airports = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        packages[simulator] = airports;
                    }
                    var icao = contribution.AirportIcao.ToUpperInvariant();
                    if (!airports.TryGetValue(icao, out var names))
                    {
                        names = [];
                        airports[icao] = names;
                    }
                    if (!names.Contains(contribution.PackageName, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(contribution.PackageName);
                    }
                }

                _cachedPackages = packages;
                _packagesLoadedUtc = DateTime.UtcNow;
                return packages;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        /// <summary>
        /// Gets available packages for the current (detected) simulator only.
        /// Used when loading maps for actual flight.
        /// Returns: Dictionary[icao] -> List of package names
        /// </summary>
        public async Task<Dictionary<string, List<string>>> GetAvailablePackagesForCurrentSimulatorAsync()
        {
            var all = await GetAvailablePackagesAsync();
            return all.TryGetValue(CurrentSimulator, out var packages)
                ? packages
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets available packages for the configured simulator (UI toggle).
        /// Used when displaying the airport list in the UI.
        /// Returns: Dictionary[icao] -> List of package names
        /// </summary>
        public async Task<Dictionary<string, List<string>>> GetAvailablePackagesForConfiguredSimulatorAsync()
        {
            var all = await GetAvailablePackagesAsync();
            return all.TryGetValue(ConfiguredSimulator, out var packages)
                ? packages
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the selected package for an airport and simulator combination.
        /// </summary>
        /// <param name="icao">Airport ICAO code</param>
        /// <param name="simulator">Simulator identifier (e.g., "msfs2020" or "msfs2024")</param>
        /// <returns>The selected package name, or empty string if none selected</returns>
        public string GetSelectedPackage(string icao, string simulator)
        {
            if (string.IsNullOrWhiteSpace(icao) || string.IsNullOrWhiteSpace(simulator)) return string.Empty;

            var key = $"{icao.ToUpperInvariant()}:{simulator.ToLowerInvariant()}";
            lock (_selectionLock)
            {
                return _selectedPackages.TryGetValue(key, out string? package) ? package : string.Empty;
            }
        }

        /// <summary>
        /// Gets the selected package for an airport using the current simulator.
        /// </summary>
        /// <param name="icao">Airport ICAO code</param>
        /// <returns>The selected package name, or empty string if none selected</returns>
        public string GetSelectedPackage(string icao) => GetSelectedPackage(icao, CurrentSimulator);

        /// <summary>
        /// Gets the selected package for an airport across all simulators.
        /// Returns a dictionary of simulator -> selected package name.
        /// </summary>
        public Dictionary<string, string> GetSelectedPackagesForAirport(string icao)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(icao)) return result;

            var upperIcao = icao.ToUpperInvariant();
            lock (_selectionLock)
            {
                foreach (var sim in SupportedSimulators)
                {
                    var key = $"{upperIcao}:{sim}";
                    if (_selectedPackages.TryGetValue(key, out string? package) && !string.IsNullOrEmpty(package))
                    {
                        result[sim] = package;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Sets the selected package for an airport and simulator combination.
        /// </summary>
        /// <param name="icao">Airport ICAO code</param>
        /// <param name="simulator">Simulator identifier (e.g., "msfs2020" or "msfs2024")</param>
        /// <param name="packageName">The package name to select</param>
        public void SetSelectedPackage(string icao, string simulator, string packageName)
        {
            if (string.IsNullOrWhiteSpace(icao) || string.IsNullOrWhiteSpace(simulator)) return;

            var key = $"{icao.Trim().ToUpperInvariant()}:{simulator.Trim().ToLowerInvariant()}";
            packageName = packageName?.Trim() ?? string.Empty;

            string? previous;
            lock (_selectionLock)
            {
                if (_selectedPackages.TryGetValue(key, out previous) &&
                    string.Equals(previous, packageName, StringComparison.Ordinal))
                {
                    return;
                }
                _selectedPackages[key] = packageName;
            }

            try
            {
                SaveSelectedPackages();
            }
            catch
            {
                lock (_selectionLock)
                {
                    if (previous == null) _selectedPackages.Remove(key);
                    else _selectedPackages[key] = previous;
                }
                throw;
            }

            try { PackageChanged?.Invoke(icao.Trim().ToUpperInvariant(), simulator.Trim().ToLowerInvariant(), packageName); } catch { }
        }

        /// <summary>
        /// Sets the selected package for an airport using the current simulator.
        /// </summary>
        /// <param name="icao">Airport ICAO code</param>
        /// <param name="packageName">The package name to select</param>
        public void SetSelectedPackage(string icao, string packageName) => SetSelectedPackage(icao, CurrentSimulator, packageName);

        /// <summary>
        /// Syncs all removal folder BGL states to match persisted settings.
        /// Called on startup to ensure filesystem matches expected state.
        /// Only applies to airports that have an explicit package selection saved.
        /// Returns a set of simulator identifiers that had files actually modified.
        /// </summary>
        public async Task<HashSet<string>> SyncAllRemovalStatesAsync(IDictionary<string, string> savedPackages, IDictionary<string, bool> savedToggles)
        {
            var packageSnapshot = new Dictionary<string, string>(savedPackages, StringComparer.OrdinalIgnoreCase);
            var toggleSnapshot = new Dictionary<string, bool>(savedToggles, StringComparer.OrdinalIgnoreCase);

            HashSet<string> changedSims;
            await RemovalFileAccess.MsfsTransactionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                changedSims = await SyncMsfsRemovalStatesAsync(packageSnapshot, toggleSnapshot)
                    .ConfigureAwait(false);
            }
            finally
            {
                RemovalFileAccess.MsfsTransactionGate.Release();
            }
            var xplaneChanged = await SyncXPlaneRemovalStatesAsync(packageSnapshot, toggleSnapshot)
                .ConfigureAwait(false);
            if (xplaneChanged) changedSims.Add("xplane");
            return changedSims;
        }

        private bool IsPackageCacheFresh() =>
            _cachedPackages != null &&
            DateTime.UtcNow - _packagesLoadedUtc < PackageCacheFreshness;

        private async Task<HashSet<string>> SyncMsfsRemovalStatesAsync(
            IDictionary<string, string> savedPackages,
            IDictionary<string, bool> savedToggles)
        {
            await _msfsRemovalGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => SyncMsfsRemovalStatesCore(
                    savedPackages,
                    savedToggles,
                    CancellationToken.None)).ConfigureAwait(false);
            }
            finally
            {
                _msfsRemovalGate.Release();
            }
        }

        public async Task<HashSet<string>> SyncMsfsRemovalStatesFromSettingsAsync(
            ISettingsStore settingsStore,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settingsStore);
            await _msfsRemovalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Load after taking the filesystem gate. A UI change that got
                // the gate first can finish persisting before this read, while
                // a UI change that arrives later will apply after this sync.
                var settings = await settingsStore.LoadAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(() => SyncMsfsRemovalStatesCore(
                    settings.AirportPackages ?? new Dictionary<string, string>(),
                    settings.SceneryRemovalToggles ?? new Dictionary<string, bool>(),
                    cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _msfsRemovalGate.Release();
            }
        }

        private static HashSet<string> SyncMsfsRemovalStatesCore(
            IDictionary<string, string> savedPackages,
            IDictionary<string, bool> savedToggles,
            CancellationToken cancellationToken)
        {
            var changedSims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sim in SupportedSimulators.Where(sim =>
                         !sim.Equals("xplane", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var communityPath = TryResolveCommunityPath(sim);
                if (string.IsNullOrWhiteSpace(communityPath)) continue;

                var removalsRoot = Path.Combine(communityPath, "bars-removals", "Scenery", "removals");
                if (!Directory.Exists(removalsRoot)) continue;

                foreach (var icaoDir in Directory.EnumerateDirectories(removalsRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var icao = Path.GetFileName(icaoDir)?.ToUpperInvariant();
                    if (string.IsNullOrEmpty(icao)) continue;

                    var packageKey = $"{icao}:{sim}";
                    if (!savedPackages.TryGetValue(packageKey, out var selectedPackage) ||
                        string.IsNullOrWhiteSpace(selectedPackage))
                    {
                        continue;
                    }

                    var toggleKey = $"{icao}:{sim}:{selectedPackage}";
                    var enabled = !savedToggles.TryGetValue(toggleKey, out var toggled) || toggled;
                    if (ApplyRemovalState(icaoDir, icao, selectedPackage, enabled))
                    {
                        changedSims.Add(sim);
                    }
                }
            }
            return changedSims;
        }

        public async Task<bool> SyncXPlaneRemovalStatesAsync(
            IDictionary<string, string> savedPackages,
            IDictionary<string, bool> savedToggles)
        {
            await _xplaneRemovalGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await SyncXPlaneRemovalStatesCoreAsync(savedPackages, savedToggles)
                    .ConfigureAwait(false);
            }
            finally
            {
                _xplaneRemovalGate.Release();
            }
        }

        private async Task<bool> SyncXPlaneRemovalStatesCoreAsync(
            IDictionary<string, string> savedPackages,
            IDictionary<string, bool> savedToggles)
        {
            var changed = false;
            var refreshMetadata = true;
            foreach (var selection in savedPackages.Where(item =>
                         item.Key.EndsWith(":xplane", StringComparison.OrdinalIgnoreCase)))
            {
                var separator = selection.Key.IndexOf(':');
                if (separator <= 0 || string.IsNullOrWhiteSpace(selection.Value)) continue;
                var icao = selection.Key[..separator];
                var toggleKey = $"{icao}:xplane:{selection.Value}";
                var enabled = !savedToggles.TryGetValue(toggleKey, out var toggled) || toggled;
                var artifact = await FindXPlaneContributionAsync(
                        icao,
                        selection.Value,
                        refreshMetadata)
                    .ConfigureAwait(false);
                refreshMetadata = false;
                // Enabled removals are refreshed once per launch so a newly
                // published artifact cannot be hidden by an older local patch.
                if (!enabled && _xplaneRemovals.IsStateApplied(icao, enabled))
                {
                    continue;
                }
                changed |= await _xplaneRemovals
                    .ApplyAsync(
                        icao,
                        selection.Value,
                        enabled,
                        artifact?.RemovalArtifactKey,
                        artifact?.ArtifactIdentity,
                        artifact?.ArtifactGenerationId)
                    .ConfigureAwait(false);
            }
            changed |= await _xplaneRemovals.RestoreTestingArtifactsAsync().ConfigureAwait(false);
            return changed;
        }

        public async Task<bool> ApplySceneryRemovalAsync(string icao, string simulator, string packageName, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(icao) || string.IsNullOrWhiteSpace(simulator))
            {
                return false;
            }

            var normalizedSim = simulator.Trim().ToLowerInvariant();
            var normalizedIcao = icao.Trim().ToUpperInvariant();
            var normalizedPackage = packageName?.Trim() ?? string.Empty;

            if (normalizedSim.Equals("xplane", StringComparison.OrdinalIgnoreCase))
            {
                await _xplaneRemovalGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var artifact = await FindXPlaneContributionAsync(
                            normalizedIcao,
                            normalizedPackage,
                            forceRefresh: enabled)
                        .ConfigureAwait(false);
                    return await _xplaneRemovals
                        .ApplyAsync(
                            normalizedIcao,
                            normalizedPackage,
                            enabled,
                            artifact?.RemovalArtifactKey,
                            artifact?.ArtifactIdentity,
                            artifact?.ArtifactGenerationId)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _xplaneRemovalGate.Release();
                }
            }

            var communityPath = TryResolveCommunityPath(normalizedSim);
            if (string.IsNullOrWhiteSpace(communityPath))
            {
                throw new InvalidOperationException(
                    $"The {normalizedSim} Community folder is not configured in the BARS Installer.");
            }

            var removalsPath = Path.Combine(communityPath, "bars-removals", "Scenery", "removals", normalizedIcao);
            if (!Directory.Exists(removalsPath))
            {
                throw new DirectoryNotFoundException(
                    $"No removal files were installed for {normalizedIcao} in {normalizedSim}.");
            }

            await _msfsRemovalGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => ApplyRemovalState(removalsPath, normalizedIcao, normalizedPackage, enabled)).ConfigureAwait(false);
            }
            finally
            {
                _msfsRemovalGate.Release();
            }
        }

        public Task<bool> ApplyXPlaneTestingRemovalsAsync(
            string icao,
            string removalJson,
            CancellationToken cancellationToken = default) =>
            _xplaneRemovals.ApplyTestingArtifactAsync(icao, removalJson, cancellationToken);

        private async Task<ApprovedContributionMetadata?> FindXPlaneContributionAsync(
            string icao,
            string packageName,
            bool forceRefresh = false)
        {
            var contributions = await ApprovedContributionsCache
                .GetAsync(_httpClient, forceRefresh)
                .ConfigureAwait(false);
            return contributions.FirstOrDefault(item =>
                string.Equals(item.Simulator, "xplane", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.AirportIcao, icao, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.PackageName, packageName, StringComparison.Ordinal));
        }

        private static bool ApplyRemovalState(string removalsPath, string icao, string packageName, bool enabled)
        {
            var normalizedPackageName = NormalizeRemovalFileComponent(packageName);
            var expectedBase = string.IsNullOrWhiteSpace(normalizedPackageName)
                ? string.Empty
                : $"{normalizedPackageName}_{icao}";
            var matchingFileFound = false;
            var candidates = Directory.EnumerateFiles(removalsPath)
                .Select(file => file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? file[..^".disabled".Length]
                    : file)
                .Where(file => file.EndsWith(".bgl", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var moves = new List<(string Source, string Target, bool DeleteAfterCommit)>();

            foreach (var candidatePath in candidates)
            {
                var baseName = Path.GetFileNameWithoutExtension(candidatePath);
                var normalizedBaseName = NormalizeRemovalFileComponent(baseName);
                var shouldEnable = enabled && !string.IsNullOrEmpty(expectedBase) &&
                    string.Equals(normalizedBaseName, NormalizeRemovalFileComponent(expectedBase), StringComparison.OrdinalIgnoreCase);
                matchingFileFound |= shouldEnable;
                var disabledPath = candidatePath + ".disabled";
                var activeExists = File.Exists(candidatePath);
                var disabledExists = File.Exists(disabledPath);

                if (shouldEnable)
                {
                    if (!activeExists && disabledExists)
                    {
                        moves.Add((disabledPath, candidatePath, false));
                    }
                    else if (activeExists && disabledExists)
                    {
                        moves.Add((disabledPath, UniqueQuarantinePath(disabledPath), true));
                    }
                }
                else if (activeExists)
                {
                    if (!disabledExists)
                    {
                        moves.Add((candidatePath, disabledPath, false));
                    }
                    else
                    {
                        moves.Add((candidatePath, UniqueQuarantinePath(candidatePath), true));
                    }
                }
            }
            if (enabled && !matchingFileFound)
            {
                throw new FileNotFoundException(
                    $"No installed removal BGL matched package '{packageName}' for {icao}.");
            }

            var completed = new List<(string Source, string Target, bool DeleteAfterCommit)>();
            try
            {
                foreach (var move in moves)
                {
                    File.Move(move.Source, move.Target);
                    completed.Add(move);
                }
            }
            catch (Exception operationError)
            {
                Exception? rollbackError = null;
                foreach (var move in completed.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (File.Exists(move.Target) && !File.Exists(move.Source))
                        {
                            File.Move(move.Target, move.Source);
                        }
                    }
                    catch (Exception ex)
                    {
                        rollbackError ??= ex;
                    }
                }

                if (rollbackError != null)
                {
                    throw new IOException(
                        $"Changing removal files for {icao} failed, and rollback also failed.",
                        new AggregateException(operationError, rollbackError));
                }
                throw;
            }

            foreach (var move in completed.Where(item => item.DeleteAfterCommit))
            {
                try { File.Delete(move.Target); } catch { }
            }
            return completed.Count > 0;
        }

        private static string UniqueQuarantinePath(string sourcePath) =>
            $"{sourcePath}.bars-stale-{Guid.NewGuid():N}";

        private static string NormalizeRemovalFileComponent(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var builder = new System.Text.StringBuilder(value.Length);
            var previousWasSeparator = false;
            foreach (var character in value.Trim())
            {
                if (char.IsLetterOrDigit(character) || character == '.')
                {
                    builder.Append(char.ToLowerInvariant(character));
                    previousWasSeparator = false;
                }
                else if (!previousWasSeparator)
                {
                    builder.Append('-');
                    previousWasSeparator = true;
                }
            }
            return builder.ToString().Trim('-');
        }

        private static string? TryResolveCommunityPath(string simulator)
        {
            try
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? string.Empty;
                var settingsPath = Path.Combine(root, "BARS", "Installer", SETTINGS_FILENAME);
                if (!File.Exists(settingsPath))
                {
                    return null;
                }

                var json = File.ReadAllText(settingsPath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var settings = JsonSerializer.Deserialize<InstallerSettings>(json, options);
                var pilotClient = settings?.PilotClient;
                if (pilotClient == null)
                {
                    return null;
                }

                if (simulator.Equals("msfs2024", StringComparison.OrdinalIgnoreCase))
                    return pilotClient.Msfs2024Path;
                if (simulator.Equals("msfs2020", StringComparison.OrdinalIgnoreCase))
                    return pilotClient.Msfs2020Path;
                return null;
            }
            catch
            {
                return null;
            }
        }
        private Dictionary<string, string> LoadSelectedPackages()
        {
            SettingsFileAccess.Gate.Wait();
            try
            {
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? string.Empty,
                    "BARS",
                    "Client"
                );
                if (!Directory.Exists(appDataPath))
                {
                    Directory.CreateDirectory(appDataPath);
                }

                string settingsPath = Path.Combine(appDataPath, SETTINGS_FILENAME);
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

                if (File.Exists(settingsPath))
                {
                    try
                    {
                        string json = File.ReadAllText(settingsPath);
                        var root = JsonNode.Parse(json) as JsonObject;
                        var packageProperty = root?.FirstOrDefault(item =>
                            item.Key.Equals("airportPackages", StringComparison.OrdinalIgnoreCase)).Value;
                        var loaded = packageProperty?.Deserialize<Dictionary<string, string>>(options);
                        if (loaded != null)
                        {
                            return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to read settings.json; {ex.Message}");
                    }
                }

                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading scenery selections from settings.json: {ex.Message}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                SettingsFileAccess.Gate.Release();
            }
        }

        private void SaveSelectedPackages()
        {
            SettingsFileAccess.Gate.Wait();
            try
            {
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? string.Empty,
                    "BARS",
                    "Client"
                );
                if (!Directory.Exists(appDataPath))
                {
                    Directory.CreateDirectory(appDataPath);
                }

                string settingsPath = Path.Combine(appDataPath, SETTINGS_FILENAME);
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
                JsonObject persisted = new();
                if (File.Exists(settingsPath))
                {
                    persisted = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject
                        ?? throw new InvalidDataException("The client settings file did not contain a JSON object.");
                }

                Dictionary<string, string> selections;
                lock (_selectionLock)
                {
                    selections = new Dictionary<string, string>(_selectedPackages, StringComparer.OrdinalIgnoreCase);
                }
                var propertyName = persisted.Select(item => item.Key)
                    .FirstOrDefault(name => name.Equals("airportPackages", StringComparison.OrdinalIgnoreCase))
                    ?? "airportPackages";
                persisted[propertyName] = JsonSerializer.SerializeToNode(selections, options);

                var json = persisted.ToJsonString(options);
                SettingsFileAccess.WriteAllTextAtomic(settingsPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving scenery selections to settings.json: {ex.Message}");
                throw;
            }
            finally
            {
                SettingsFileAccess.Gate.Release();
            }
        }

        private sealed class InstallerSettings
        {
            [JsonPropertyName("Pilot-Client")]
            public InstallerClientSettings? PilotClient { get; set; }
        }

        private sealed class InstallerClientSettings
        {
            [JsonPropertyName("msfs2020Path")]
            public string? Msfs2020Path { get; set; }

            [JsonPropertyName("msfs2024Path")]
            public string? Msfs2024Path { get; set; }
        }
    }
}

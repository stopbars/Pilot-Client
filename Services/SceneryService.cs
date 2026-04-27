using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Infrastructure.Settings;

namespace BARS_Client_V2.Services
{
    public class SceneryContribution
    {
        public string Id { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string UserDisplayName { get; set; } = string.Empty;
        public string AirportIcao { get; set; } = string.Empty;
        public string PackageName { get; set; } = string.Empty;
        public string SubmittedXml { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public string Simulator { get; set; } = string.Empty;
        public DateTime SubmissionDate { get; set; }
        public string Status { get; set; } = string.Empty;
        public string RejectionReason { get; set; } = string.Empty;
        public DateTime? DecisionDate { get; set; }
    }

    public class ContributionsResponse
    {
        // Initialize to empty list to satisfy non-nullable warning
        public List<SceneryContribution> contributions { get; set; } = new();
    }

    public class SceneryService
    {
        private const string API_URL = "https://v2.stopbars.com/contributions?status=approved";
        private const string SETTINGS_FILENAME = "settings.json";
        private readonly HttpClient _httpClient;
        // Key format: "ICAO:simulator" (e.g., "YSCB:msfs2020" or "YSCB:msfs2024")
        private Dictionary<string, string> _selectedPackages;
        private static SceneryService? _instance;
        private string _currentSimulator = "msfs2020"; // Default to 2020 - set by actual SimConnect detection
        private string _configuredSimulator = "msfs2024"; // Which simulator the user is configuring in the UI

        // Cached packages data - fetched once, filtered locally by simulator
        private Dictionary<string, Dictionary<string, List<string>>>? _cachedPackages;
        private readonly SemaphoreSlim _cacheLock = new(1, 1);
        private readonly SemaphoreSlim _removalGate = new(1, 1);

        // Supported simulators
        public static readonly string[] SupportedSimulators = { "msfs2020", "msfs2024" };

        /// <summary>
        /// Gets or sets the current active simulator detected by SimConnect.
        /// This determines which map to load when actually flying.
        /// Valid values: "msfs2020", "msfs2024"
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
        /// Valid values: "msfs2020", "msfs2024"
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
            get
            {
                _instance ??= new SceneryService();
                return _instance;
            }
        }

        private SceneryService()
        {
            _httpClient = new HttpClient();
            _selectedPackages = LoadSelectedPackages();
        }

        /// <summary>
        /// Gets available packages organized by simulator, then by airport ICAO.
        /// Returns: Dictionary[simulator] -> Dictionary[icao] -> List of package names
        /// Fetches from API only once and caches the result.
        /// </summary>
        public async Task<Dictionary<string, Dictionary<string, List<string>>>> GetAvailablePackagesAsync()
        {
            // Return cached data if available
            if (_cachedPackages != null)
            {
                return _cachedPackages;
            }

            await _cacheLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                if (_cachedPackages != null)
                {
                    return _cachedPackages;
                }

                var packages = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);

                // Initialize with supported simulators
                foreach (var sim in SupportedSimulators)
                {
                    packages[sim] = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }

                try
                {
                    var response = await _httpClient.GetStringAsync(API_URL);

                    // Use case-insensitive JSON options
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };

                    var data = JsonSerializer.Deserialize<ContributionsResponse>(response, options);

                    // Print debug info
                    Console.WriteLine($"API Response received, contributions count: {data?.contributions?.Count ?? 0}");

                    if (data?.contributions != null && data.contributions.Count > 0)
                    {
                        foreach (var contribution in data.contributions)
                        {
                            if (string.IsNullOrEmpty(contribution.AirportIcao) || string.IsNullOrEmpty(contribution.PackageName))
                                continue;

                            // Default to msfs2020 if simulator not specified
                            var simulator = string.IsNullOrEmpty(contribution.Simulator) ? "msfs2020" : contribution.Simulator.ToLowerInvariant();

                            // Ensure simulator key exists
                            if (!packages.ContainsKey(simulator))
                            {
                                packages[simulator] = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                            }

                            var icao = contribution.AirportIcao.ToUpperInvariant();

                            if (!packages[simulator].ContainsKey(icao))
                            {
                                packages[simulator][icao] = new List<string>();
                            }

                            if (!packages[simulator][icao].Contains(contribution.PackageName))
                            {
                                packages[simulator][icao].Add(contribution.PackageName);
                            }
                        }

                        var totalAirports = packages.Values.Sum(d => d.Count);
                        Console.WriteLine($"Processed contributions into {totalAirports} airport/simulator combinations with scenery packages");
                    }
                    else
                    {
                        Console.WriteLine("No contributions found in API response");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error fetching scenery packages: {ex.Message}");
                }

                // Cache the result (even if empty, to avoid repeated failed fetches)
                _cachedPackages = packages;
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
            return _selectedPackages.TryGetValue(key, out string? package) ? package : string.Empty;
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
            foreach (var sim in SupportedSimulators)
            {
                var key = $"{upperIcao}:{sim}";
                if (_selectedPackages.TryGetValue(key, out string? package) && !string.IsNullOrEmpty(package))
                {
                    result[sim] = package;
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

            // Avoid redundant writes/events if unchanged
            if (_selectedPackages.TryGetValue(key, out var existing) &&
                string.Equals(existing, packageName, StringComparison.Ordinal))
            {
                return;
            }

            _selectedPackages[key] = packageName;
            SaveSelectedPackages();

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
            await _removalGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    var changedSims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var sim in SupportedSimulators)
                    {
                        var communityPath = TryResolveCommunityPath(sim);
                        if (string.IsNullOrWhiteSpace(communityPath)) continue;

                        var removalsRoot = Path.Combine(communityPath, "bars-removals", "Scenery", "removals");
                        if (!Directory.Exists(removalsRoot)) continue;

                        foreach (var icaoDir in Directory.EnumerateDirectories(removalsRoot))
                        {
                            var icao = Path.GetFileName(icaoDir)?.ToUpperInvariant();
                            if (string.IsNullOrEmpty(icao)) continue;

                            var packageKey = $"{icao}:{sim}";
                            if (!savedPackages.TryGetValue(packageKey, out var selectedPackage) || string.IsNullOrWhiteSpace(selectedPackage))
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
                }).ConfigureAwait(false);
            }
            finally
            {
                _removalGate.Release();
            }
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

            var communityPath = TryResolveCommunityPath(normalizedSim);
            if (string.IsNullOrWhiteSpace(communityPath))
            {
                return false;
            }

            var removalsPath = Path.Combine(communityPath, "bars-removals", "Scenery", "removals", normalizedIcao);
            if (!Directory.Exists(removalsPath))
            {
                return false;
            }

            await _removalGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => ApplyRemovalState(removalsPath, normalizedIcao, normalizedPackage, enabled)).ConfigureAwait(false);
            }
            finally
            {
                _removalGate.Release();
            }
        }

        private static bool ApplyRemovalState(string removalsPath, string icao, string packageName, bool enabled)
        {
            var anyChanged = false;
            var normalizedPackageName = packageName?.Replace(' ', '-') ?? string.Empty;
            var expectedBase = string.IsNullOrWhiteSpace(normalizedPackageName)
                ? string.Empty
                : $"{normalizedPackageName}_{icao}";

            foreach (var file in Directory.EnumerateFiles(removalsPath))
            {
                var fileName = Path.GetFileName(file);
                if (string.IsNullOrEmpty(fileName))
                {
                    continue;
                }

                var isDisabled = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var candidatePath = isDisabled ? file[..^".disabled".Length] : file;

                if (!candidatePath.EndsWith(".bgl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(candidatePath);
                var shouldEnable = enabled && !string.IsNullOrEmpty(expectedBase)
                    && string.Equals(baseName, expectedBase, StringComparison.OrdinalIgnoreCase);

                if (shouldEnable)
                {
                    if (isDisabled)
                    {
                        if (File.Exists(candidatePath))
                        {
                            File.Delete(file);
                        }
                        else
                        {
                            File.Move(file, candidatePath);
                        }
                        anyChanged = true;
                    }
                }
                else
                {
                    if (!isDisabled)
                    {
                        var target = file + ".disabled";
                        if (File.Exists(target))
                        {
                            File.Delete(file);
                        }
                        else
                        {
                            File.Move(file, target);
                        }
                        anyChanged = true;
                    }
                }
            }
            return anyChanged;
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

                return simulator.Equals("msfs2024", StringComparison.OrdinalIgnoreCase)
                    ? pilotClient.Msfs2024Path
                    : pilotClient.Msfs2020Path;
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

                // Local type matching JsonSettingsStore serialization shape
                var persisted = new SettingsPersisted();

                if (File.Exists(settingsPath))
                {
                    try
                    {
                        string json = File.ReadAllText(settingsPath);
                        var loaded = JsonSerializer.Deserialize<SettingsPersisted>(json, options);
                        if (loaded != null) persisted = loaded;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to read settings.json; {ex.Message}");
                    }
                }

                var result = persisted.AirportPackages ?? new Dictionary<string, string>();
                return new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
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

                // Load existing to preserve unrelated fields (e.g., apiToken)
                var persisted = new SettingsPersisted();
                if (File.Exists(settingsPath))
                {
                    try
                    {
                        var current = JsonSerializer.Deserialize<SettingsPersisted>(File.ReadAllText(settingsPath), options);
                        if (current != null) persisted = current;
                    }
                    catch { /* ignore and overwrite minimal */ }
                }

                persisted.AirportPackages = new Dictionary<string, string>(_selectedPackages, StringComparer.OrdinalIgnoreCase);

                var json = JsonSerializer.Serialize(persisted, options);
                File.WriteAllText(settingsPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving scenery selections to settings.json: {ex.Message}");
            }
            finally
            {
                SettingsFileAccess.Gate.Release();
            }
        }

        private sealed class SettingsPersisted
        {
            public string? ApiToken { get; set; }
            /// <summary>
            /// Airport package selections. Key format: "ICAO:simulator" (e.g., "YSCB:msfs2020")
            /// </summary>
            public Dictionary<string, string>? AirportPackages { get; set; }
            /// <summary>
            /// Scenery removal toggle states. Key format: "ICAO:simulator:package"
            /// </summary>
            public Dictionary<string, bool>? SceneryRemovalToggles { get; set; }
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

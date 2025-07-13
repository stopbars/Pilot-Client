using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BARS_Client_V2.Services
{
    public class Airport
    {
        public string Icao { get; set; } = string.Empty;
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Continent { get; set; } = string.Empty;
    }

    public class AirportService
    {
        private const string API_BASE_URL = "https://v2.stopbars.com/airports";
        private const string CACHE_DIRECTORY = "BARS\\Client";
        private const string AIRPORTS_CACHE_FILENAME = "airports.json";
        private readonly string _airportsCacheFilePath;
        private readonly HttpClient _httpClient;
        private Dictionary<string, Airport> _airports;
        private static AirportService? _instance;

        public static AirportService Instance
        {
            get
            {
                _instance ??= new AirportService();
                return _instance;
            }
        }

        private AirportService()
        {
            _httpClient = new HttpClient();
            _airports = new Dictionary<string, Airport>(StringComparer.OrdinalIgnoreCase);
            
            // Set up the cache directory path
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string cacheDirectory = Path.Combine(localAppData, CACHE_DIRECTORY);
            
            // Ensure the directory exists
            if (!Directory.Exists(cacheDirectory))
            {
                Directory.CreateDirectory(cacheDirectory);
            }
            
            _airportsCacheFilePath = Path.Combine(cacheDirectory, AIRPORTS_CACHE_FILENAME);
            LoadCachedAirports();
        }

        /// <summary>
        /// Loads cached airports from local storage if available
        /// </summary>
        private void LoadCachedAirports()
        {
            try
            {
                if (File.Exists(_airportsCacheFilePath))
                {
                    string json = File.ReadAllText(_airportsCacheFilePath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    _airports = JsonSerializer.Deserialize<Dictionary<string, Airport>>(json, options) 
                        ?? new Dictionary<string, Airport>(StringComparer.OrdinalIgnoreCase);
                    
                    Console.WriteLine($"Loaded {_airports.Count} airports from cache at {_airportsCacheFilePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading cached airports: {ex.Message}");
                _airports = new Dictionary<string, Airport>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Saves the current airport cache to local storage
        /// </summary>
        private void SaveCachedAirports()
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                string json = JsonSerializer.Serialize(_airports, options);
                File.WriteAllText(_airportsCacheFilePath, json);
                Console.WriteLine($"Saved {_airports.Count} airports to cache at {_airportsCacheFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving airports cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the closest airport to the specified coordinates
        /// </summary>
        /// <param name="latitude">Latitude in decimal degrees</param>
        /// <param name="longitude">Longitude in decimal degrees</param>
        /// <returns>The closest airport, or null if no airports are available</returns>
        public Airport? GetClosestAirport(double latitude, double longitude)
        {
            if (_airports.Count == 0)
            {
                Console.WriteLine("No airports in cache to find closest match");
                return null;
            }

            Airport? closest = null;
            double minDistance = double.MaxValue;

            foreach (var airport in _airports.Values)
            {
                double distance = CalculateDistance(latitude, longitude, airport.Latitude, airport.Longitude);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    closest = airport;
                }
            }

            return closest;
        }

        /// <summary>
        /// Calculates the distance between two points using the Haversine formula
        /// </summary>
        private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double EarthRadiusKm = 6371.0;
            
            // Convert degrees to radians
            var dLat = DegreesToRadians(lat2 - lat1);
            var dLon = DegreesToRadians(lon2 - lon1);
            
            // Haversine formula
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return EarthRadiusKm * c;
        }

        private double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        /// <summary>
        /// Initializes the airport database with data from the API for airports with contributions
        /// </summary>
        public async Task InitializeAirportDatabaseAsync()
        {
            try
            {
                // Get all airports with contributions from SceneryService
                var contributionAirports = await SceneryService.Instance.GetAvailablePackagesAsync();
                if (contributionAirports == null || contributionAirports.Count == 0)
                {
                    Console.WriteLine("No contribution airports found");
                    return;
                }

                // Identify which airports we need to fetch
                var airportsToFetch = new List<string>();
                foreach (var icao in contributionAirports.Keys)
                {
                    if (!_airports.ContainsKey(icao))
                    {
                        airportsToFetch.Add(icao);
                    }
                }

                if (airportsToFetch.Count == 0)
                {
                    Console.WriteLine("All contribution airports are already cached");
                    return;
                }

                // Build the URL with airport ICAOs
                string icaosParam = string.Join(",", airportsToFetch);
                string url = $"{API_BASE_URL}?icao={icaosParam}";
                
                Console.WriteLine($"Fetching {airportsToFetch.Count} airports from API: {url}");
                
                // Fetch the airport data
                var response = await _httpClient.GetStringAsync(url);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var fetchedAirports = JsonSerializer.Deserialize<Dictionary<string, Airport>>(response, options);
                
                if (fetchedAirports != null)
                {
                    // Add the new airports to our cache
                    foreach (var airport in fetchedAirports)
                    {
                        _airports[airport.Key] = airport.Value;
                    }
                    
                    // Save the updated cache
                    SaveCachedAirports();
                    Console.WriteLine($"Added {fetchedAirports.Count} airports to cache");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing airport database: {ex.Message}");
            }
        }
    }
}

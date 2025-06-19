using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BARS_Client_V2.Services;
using System.Linq;

namespace BARS_Client_V2
{      public partial class MainWindow : Window
    {
        private ObservableCollection<AirportItem> airportItems = new();
        private ObservableCollection<AirportItem> filteredAirportItems = new();
        private readonly SceneryService _sceneryService;
          // Pagination properties
        private int _currentPage = 1;
        private int _itemsPerPage = 8;
        private int _totalPages = 1;
        private ObservableCollection<AirportItem> _currentPageItems = new();
        private string _apiToken = string.Empty;        public MainWindow()
        {
            InitializeComponent();
            _sceneryService = SceneryService.Instance;
            LoadApiToken(); // Load API token from settings
            InitializeAirportsCollectionAsync();
            InitializeAirportsAsync();

            // Hook up search button and text box enter key
            SearchButton.Click += SearchButton_Click;
            SearchAirportsTextBox.KeyDown += (s, e) => {
                if (e.Key == System.Windows.Input.Key.Enter)
                    SearchButton_Click(s, e);
            };
            
            // Hook up pagination buttons
            PreviousPageButton.Click += PreviousPageButton_Click;
            NextPageButton.Click += NextPageButton_Click;
            
            // Hook up API token save button
            SaveTokenButton.Click += SaveTokenButton_Click;
            
            // Hook up window loaded event to populate UI after initialization
            this.Loaded += MainWindow_Loaded;
        }
          /// <summary>
        /// Window loaded event handler - populate UI elements after window is fully loaded
        /// </summary>
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Populate API token textbox with the stored value after window is fully loaded
            if (!string.IsNullOrEmpty(_apiToken))
            {
                ApiTokenTextBox.Text = _apiToken;
            }
        }/// <summary>
        /// Loads the API token from application settings
        /// </summary>
        private void LoadApiToken()
        {
            try
            {
                _apiToken = Properties.Settings.Default.apiToken ?? string.Empty;
                Console.WriteLine("API Token loaded from settings");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading API token: {ex.Message}");
                _apiToken = string.Empty;
            }
        }
          /// <summary>
        /// Saves the API token to application settings
        /// </summary>
        /// <param name="token">The API token to save</param>
        public void SaveApiToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                // Don't save empty tokens
                return;
            }

            try
            {
                // Trim any whitespace from the token
                token = token.Trim();
                
                // Only save if token is not empty after trimming
                if (!string.IsNullOrEmpty(token))
                {
                    _apiToken = token;
                    Properties.Settings.Default.apiToken = token;
                    Properties.Settings.Default.Save();
                    Console.WriteLine("API Token saved to settings");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving API token: {ex.Message}");
                MessageBox.Show($"Error saving API token: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Gets the current API token
        /// </summary>
        /// <returns>The current API token</returns>
        public string GetApiToken()
        {
            return _apiToken;
        }
        
        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            ApplySearch(SearchAirportsTextBox.Text);
        }

        private async Task InitializeAirportsAsync()
        {
            await AirportService.Instance.InitializeAirportDatabaseAsync();
        }

        private void ApplySearch(string searchText)
        {
            // If search is empty, show all airports
            if (string.IsNullOrWhiteSpace(searchText))
            {
                filteredAirportItems = new ObservableCollection<AirportItem>(airportItems);
            }
            else
            {
                searchText = searchText.Trim().ToUpperInvariant();
                
                // Filter by ICAO code or package name
                filteredAirportItems.Clear();
                foreach (var airport in airportItems)
                {
                    bool matchesIcao = airport.ICAO.ToUpperInvariant().Contains(searchText);
                    bool matchesPackage = airport.SceneryPackages.Any(p => 
                        p.ToUpperInvariant().Contains(searchText));
                    
                    if (matchesIcao || matchesPackage)
                    {
                        filteredAirportItems.Add(airport);
                    }
                }
            }
            
            // Reset to first page after search
            _currentPage = 1;
            UpdatePagination();
        }
        
        private void UpdatePagination()
        {
            // Calculate total pages
            _totalPages = (int)Math.Ceiling(filteredAirportItems.Count / (double)_itemsPerPage);
            if (_totalPages == 0) _totalPages = 1;
            
            // Make sure current page is valid
            if (_currentPage < 1) _currentPage = 1;
            if (_currentPage > _totalPages) _currentPage = _totalPages;
            
            // Update page display
            PageInfoText.Text = $"Page {_currentPage} of {_totalPages}";
            
            // Enable/disable navigation buttons
            PreviousPageButton.IsEnabled = _currentPage > 1;
            NextPageButton.IsEnabled = _currentPage < _totalPages;
            
            // Get items for current page
            _currentPageItems.Clear();
            var pagedItems = filteredAirportItems
                .Skip((_currentPage - 1) * _itemsPerPage)
                .Take(_itemsPerPage);
                
            foreach (var item in pagedItems)
            {
                _currentPageItems.Add(item);
            }
            
            // Update the UI with paged results
            AirportsItemsControl.ItemsSource = _currentPageItems;
        }
        
        private void PreviousPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                UpdatePagination();
            }
        }
        
        private void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < _totalPages)
            {
                _currentPage++;
                UpdatePagination();
            }
        }

        private async void InitializeAirportsCollectionAsync()
        {
            try
            {
                // Get available packages for all airports from the service
                var availablePackages = await _sceneryService.GetAvailablePackagesAsync();

                // Clear any existing items
                airportItems.Clear();

                // Create airport items for each airport returned from the service
                foreach (var airportEntry in availablePackages)
                {
                    // Skip if no packages are available
                    if (airportEntry.Value.Count == 0)
                        continue;
                        
                    var airport = new AirportItem(airportEntry.Key, _sceneryService)
                    {
                        SceneryPackages = new ObservableCollection<string>(airportEntry.Value)
                    };

                    // Set the selected package from saved settings or select the first one as default
                    var savedPackage = _sceneryService.GetSelectedPackage(airportEntry.Key);
                    airport.SelectedPackage = !string.IsNullOrEmpty(savedPackage) ? savedPackage : airportEntry.Value[0];
                    airportItems.Add(airport);
                }
                
                // Sort airports alphabetically by ICAO
                airportItems = new ObservableCollection<AirportItem>(airportItems.OrderBy(a => a.ICAO));
                
                // Initialize filtered collection with all items
                filteredAirportItems = new ObservableCollection<AirportItem>(airportItems);
                
                // Set up initial pagination
                UpdatePagination();
                
                // Print the count to debug
                Console.WriteLine($"Loaded {airportItems.Count} airports");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading airport scenery packages: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
          /// <summary>
        /// Event handler for the Save Token button
        /// </summary>
        private void SaveTokenButton_Click(object sender, RoutedEventArgs e)
        {
            // Get token from textbox and trim it
            string token = ApiTokenTextBox.Text?.Trim() ?? string.Empty;
            
            // Check if token is valid
            if (!string.IsNullOrWhiteSpace(token))
            {
                // Save valid token
                SaveApiToken(token);
                MessageBox.Show("API Token saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("API Token cannot be empty.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    public class AirportItem : INotifyPropertyChanged
    {
        private readonly SceneryService _sceneryService;
        private string _icao = string.Empty;
        private ObservableCollection<string> _sceneryPackages = new();
        private string _selectedPackage = string.Empty;

        public AirportItem(string icao, SceneryService sceneryService)
        {
            _sceneryService = sceneryService;
            ICAO = icao;
            PropertyChanged += AirportItem_PropertyChanged;
        }

        private void AirportItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SelectedPackage) && !string.IsNullOrEmpty(SelectedPackage))
            {
                _sceneryService.SetSelectedPackage(ICAO, SelectedPackage);
            }
        }

        public string ICAO
        {
            get => _icao;
            set
            {
                if (_icao != value)
                {
                    _icao = value;
                    OnPropertyChanged(nameof(ICAO));
                }
            }
        }

        public ObservableCollection<string> SceneryPackages
        {
            get => _sceneryPackages;
            set
            {
                if (_sceneryPackages != value)
                {
                    _sceneryPackages = value;
                    OnPropertyChanged(nameof(SceneryPackages));
                }
            }
        }

        public string SelectedPackage
        {
            get => _selectedPackage;
            set
            {
                if (_selectedPackage != value)
                {
                    _selectedPackage = value;
                    OnPropertyChanged(nameof(SelectedPackage));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
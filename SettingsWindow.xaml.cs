using System.ComponentModel;
using System.Windows;
using BARS_Client_V2.Application;
using BARS_Client_V2.Infrastructure.Diagnostics;
using BARS_Client_V2.Presentation.ViewModels;
using BARS_Client_V2.Services;

namespace BARS_Client_V2;

public partial class SettingsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DiscordPresenceService _discordPresence;
    private readonly LightDrawDistanceSettings _drawDistance;
    private bool _saving;

    internal SettingsWindow(MainWindowViewModel viewModel, DiscordPresenceService discordPresence,
        LightDrawDistanceSettings drawDistance)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _discordPresence = discordPresence;
        _drawDistance = drawDistance;
        DrawDistanceSlider.Maximum = LightDrawDistanceSettings.MaximumMeters;
        DrawDistanceSlider.Minimum = LightDrawDistanceSettings.MinimumMeters;
        DrawDistanceSlider.TickFrequency = LightDrawDistanceSettings.StepMeters;
        DrawDistanceSlider.SmallChange = LightDrawDistanceSettings.StepMeters;
        DrawDistanceSlider.LargeChange = LightDrawDistanceSettings.StepMeters * 4;
        DrawDistanceSlider.Value = drawDistance.Meters;
        DrawDistanceMinimum.Text = FormatDistance(LightDrawDistanceSettings.MinimumMeters);
        DrawDistanceMaximum.Text = FormatDistance(LightDrawDistanceSettings.MaximumMeters);
        AutoMinimizeToggle.IsChecked = viewModel.AutoMinimizeOnStart;
        DiscordToggle.IsChecked = discordPresence.IsEnabled;
        Loaded += (_, _) => DrawDistanceSlider.Focus();
    }

    private static string FormatDistance(double meters) => meters < 1000 ? $"{meters:0} m" : $"{meters / 1000:0.##} km";

    private void DrawDistanceSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DrawDistanceValue != null) DrawDistanceValue.Text = FormatDistance(e.NewValue);
    }

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saving = true;
        PreferencesPanel.IsEnabled = false;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        SaveButton.Content = "Saving...";
        SaveError.Visibility = Visibility.Collapsed;
        try
        {
            var discordEnabled = DiscordToggle.IsChecked == true;
            var drawDistanceMeters = LightDrawDistanceSettings.Normalize((int)DrawDistanceSlider.Value);
            await _viewModel.SavePreferencesAsync(AutoMinimizeToggle.IsChecked == true, discordEnabled, drawDistanceMeters);
            _drawDistance.SetMeters(drawDistanceMeters);
            _discordPresence.SetEnabled(discordEnabled);
            _saving = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StartupTrace.Write($"Save preferences failed: {ex.Message}");
            SaveError.Text = "Couldn't save your settings. Please try again.";
            SaveError.Visibility = Visibility.Visible;
        }
        finally
        {
            _saving = false;
            PreferencesPanel.IsEnabled = true;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            SaveButton.Content = "Save settings";
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_saving) e.Cancel = true;
        base.OnClosing(e);
    }
}

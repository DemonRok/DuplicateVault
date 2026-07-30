using System.Windows;
using DuplicateVault.Core;

namespace DuplicateVault.App;

public partial class SettingsWindow : Window
{
    private readonly Func<Task>? _resetCurrentRootsAsync;

    public SettingsWindow(GuiSettings settings, Func<Task>? resetCurrentRootsAsync = null)
    {
        InitializeComponent();
        _resetCurrentRootsAsync = resetCurrentRootsAsync;
        QuickMode.IsChecked = string.Equals(settings.ScanMode, "Quick", StringComparison.OrdinalIgnoreCase);
        FullMode.IsChecked = string.Equals(settings.ScanMode, "Full", StringComparison.OrdinalIgnoreCase);
        StrictMode.IsChecked = string.Equals(settings.ScanMode, "Strict", StringComparison.OrdinalIgnoreCase);
        MinimumSizeBox.Text = settings.MinimumSizeText;
        RememberRootsBox.IsChecked = settings.RememberScanRoots;
        ResetCurrentRootsButton.IsEnabled = _resetCurrentRootsAsync is not null;
    }

    public GuiSettings Settings { get; private set; } = new();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = SizeParser.Parse(MinimumSizeBox.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show("La dimensione minima non è valida.\n\n" + ex.Message, "DuplicateVault", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Settings = new GuiSettings
        {
            ScanMode = StrictMode.IsChecked == true ? "Strict" : FullMode.IsChecked == true ? "Full" : "Quick",
            MinimumSizeText = MinimumSizeBox.Text.Trim(),
            RememberScanRoots = RememberRootsBox.IsChecked == true
        };
        DialogResult = true;
    }

    private async void ResetCurrentRoots_Click(object sender, RoutedEventArgs e)
    {
        if (_resetCurrentRootsAsync is null) return;
        var answer = MessageBox.Show(
            "Vuoi azzerare i dati salvati nel database per le radici attualmente presenti nella barra laterale?",
            "DuplicateVault",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        ResetCurrentRootsButton.IsEnabled = false;
        try
        {
            await _resetCurrentRootsAsync();
        }
        finally
        {
            ResetCurrentRootsButton.IsEnabled = true;
        }
    }
}

using System.Windows;
using DuplicateVault.Core;

namespace DuplicateVault.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow(GuiSettings settings)
    {
        InitializeComponent();
        QuickMode.IsChecked = string.Equals(settings.ScanMode, "Quick", StringComparison.OrdinalIgnoreCase);
        FullMode.IsChecked = string.Equals(settings.ScanMode, "Full", StringComparison.OrdinalIgnoreCase);
        StrictMode.IsChecked = string.Equals(settings.ScanMode, "Strict", StringComparison.OrdinalIgnoreCase);
        MinimumSizeBox.Text = settings.MinimumSizeText;
        RememberRootsBox.IsChecked = settings.RememberScanRoots;
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
}

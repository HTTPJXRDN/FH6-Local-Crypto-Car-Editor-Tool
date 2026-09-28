using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FH6LocalCryptoTool;

public partial class WidebodyMergeWindow : Window
{
    public long? SelectedCarId { get; private set; }
    public bool ReplaceConflicts => OverrideConflicts.IsChecked == true;

    public WidebodyMergeWindow(WidebodyMergePreview preview)
    {
        InitializeComponent();
        SourceText.Text = $"Base: {Path.GetFileName(preview.BasePath)}    Donor: {Path.GetFileName(preview.DonorPath)}";
        SourceText.ToolTip = $"Base: {preview.BasePath}\nDonor: {preview.DonorPath}";
        CarChoices.ItemsSource = preview.Cars;
        if (preview.Cars.Count == 1) CarChoices.SelectedIndex = 0;
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }

    private void CarChoices_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateImportButton();

    private void Override_Changed(object sender, RoutedEventArgs e) => UpdateImportButton();

    private void UpdateImportButton()
    {
        if (ImportButton is null) return;
        ImportButton.IsEnabled = CarChoices.SelectedItem is WidebodyMergeCar car &&
                                 (car.NewKitCount > 0 || car.NewStockPartCount > 0 || ReplaceConflicts);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (CarChoices.SelectedItem is not WidebodyMergeCar car) return;
        SelectedCarId = car.Ordinal;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

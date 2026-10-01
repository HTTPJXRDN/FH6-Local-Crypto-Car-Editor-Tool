using System.IO;
using System.Windows;
using System.Windows.Input;

namespace FH6LocalCryptoTool;

public partial class CarRelatedImportWindow : Window
{
    public bool ReplaceConflicts => OverrideConflicts.IsChecked == true;

    public CarRelatedImportWindow(CarRelatedDbExport.Car car, ModMergePreview preview,
                                  string basePath, string donorPath)
    {
        InitializeComponent();
        SourceText.Text = $"Base: {Path.GetFileName(basePath)}    Donor: {Path.GetFileName(donorPath)}";
        SourceText.ToolTip = $"Base: {basePath}\nDonor: {donorPath}";
        CarText.Text = car.ToString();
        int additions = 0, conflicts = 0;
        foreach (var table in preview.Tables)
            foreach (var row in table.Rows)
                if (row.IsConflict) conflicts++; else additions++;
        ChangesText.Text = $"{additions:n0} new row(s), {conflicts:n0} conflicting row(s) across {preview.Tables.Count} table(s).";
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }

    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

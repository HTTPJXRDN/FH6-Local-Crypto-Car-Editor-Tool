using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FH6LocalCryptoTool;

public partial class CarRelatedExportWindow : Window
{
    private readonly IReadOnlyList<CarRelatedDbExport.Car> _cars = Array.Empty<CarRelatedDbExport.Car>();
    public long? SelectedCarId { get; private set; }

    public CarRelatedExportWindow(string sourcePath, IReadOnlyList<CarRelatedDbExport.Car> cars)
    {
        InitializeComponent();
        _cars = cars;
        SourceText.Text = "Source: " + Path.GetFileName(sourcePath);
        SourceText.ToolTip = sourcePath;
        CarChoices.ItemsSource = cars;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (SearchBox is null || CarChoices is null) return;
        string query = SearchBox.Text.Trim();
        CarChoices.ItemsSource = query.Length == 0 ? _cars :
            _cars.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             c.Year.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             c.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void CarChoices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExportButton is not null) ExportButton.IsEnabled = CarChoices.SelectedItem is CarRelatedDbExport.Car;
    }
    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (CarChoices.SelectedItem is not CarRelatedDbExport.Car car) return;
        SelectedCarId = car.Id;
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

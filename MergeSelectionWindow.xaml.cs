using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FH6LocalCryptoTool;

public partial class MergeSelectionWindow : Window
{
    private const int PageSize = 200;
    private readonly ModMergePreview _preview;
    private readonly HashSet<ModMergeRow> _selected = new();
    private readonly HashSet<ModMergeTable> _replaceTables = new();
    private readonly Dictionary<ModMergeTable, CheckBox> _tableChecks = new();
    private readonly Dictionary<ModMergeTable, CheckBox> _replaceChecks = new();
    private readonly Dictionary<ModMergeRow, CheckBox> _rowChecks = new();
    private bool _refreshing;

    public IReadOnlyCollection<ModMergeRow> SelectedRows => _selected.ToArray();
    public IReadOnlyCollection<string> ReplacementTables => _replaceTables.Select(table => table.Name).ToArray();

    public MergeSelectionWindow(ModMergePreview preview)
    {
        InitializeComponent();
        _preview = preview;
        SourceText.Text = $"Mine: {Path.GetFileName(preview.BasePath)}    Donor: {Path.GetFileName(preview.OverlayPath)}";
        SourceText.ToolTip = $"Mine: {preview.BasePath}\nDonor: {preview.OverlayPath}";
        WarningText.Text = preview.Warnings.Count == 0 ? "" :
            $"{preview.Warnings.Count} table(s) skipped: {string.Join("  |  ", preview.Warnings.Take(2))}";
        WarningText.ToolTip = string.Join("\n", preview.Warnings);
        PopulateTables();
        UpdateCount();
    }

    private void PopulateTables()
    {
        string term = TableFilter.Text.Trim();
        TableTree.Items.Clear();
        _tableChecks.Clear();
        _replaceChecks.Clear();
        _rowChecks.Clear();
        foreach (var table in _preview.Tables.Where(t =>
                     t.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            var check = new CheckBox
            {
                Content = $"Merge rows — {table.Name}  ({table.AddedCount} new, {table.ConflictCount} changed, " +
                          $"{table.BaseOnlyCount} base-only)" +
                          (table.IsNew ? "  [new table]" : ""),
                ToolTip = "Select every new or changed donor row. Base-only rows remain. " +
                          "Use 'Replace whole table' when old rows must be removed.",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(3, 4, 0, 4),
                IsThreeState = true
            };
            check.Click += (_, _) =>
            {
                if (_refreshing) return;
                bool select = check.IsChecked == true;
                foreach (var row in table.Rows)
                    if (select) _selected.Add(row); else _selected.Remove(row);
                RefreshChecks();
            };
            var exact = new CheckBox
            {
                Content = "Replace whole table (drop + rebuild)",
                ToolTip = $"Drop this staged table, recreate the donor schema, and copy all {table.DonorRowCount:n0} donor rows. " +
                          "Use this only when the donor intentionally defines the complete table.",
                Foreground = (Brush)FindResource("Pink"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(18, 4, 3, 4),
                IsChecked = _replaceTables.Contains(table)
            };
            exact.Checked += (_, _) =>
            {
                if (_refreshing) return;
                _replaceTables.Add(table);
                foreach (var row in table.Rows) _selected.Remove(row);
                RefreshChecks();
            };
            exact.Unchecked += (_, _) =>
            {
                if (_refreshing) return;
                _replaceTables.Remove(table);
                RefreshChecks();
            };
            var header = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(exact, Dock.Right);
            header.Children.Add(exact);
            header.Children.Add(check);
            var item = new TreeViewItem { Header = header, Tag = table };
            item.Items.Add(new TreeViewItem { Header = "Loading rows..." });
            item.Expanded += (_, _) =>
            {
                if (item.Items.Count == 1 && item.Items[0] is TreeViewItem placeholder &&
                    (string?)placeholder.Header == "Loading rows...")
                {
                    item.Items.Clear();
                    LoadRows(item, table, 0);
                }
            };
            _tableChecks[table] = check;
            _replaceChecks[table] = exact;
            TableTree.Items.Add(item);
        }
        RefreshChecks();
    }

    private void LoadRows(TreeViewItem item, ModMergeTable table, int start)
    {
        int end = Math.Min(start + PageSize, table.Rows.Count);
        for (int i = start; i < end; i++)
        {
            var row = table.Rows[i];
            var check = new CheckBox
            {
                Content = $"{(row.IsConflict ? "Replace" : "New")}  {row.Label}" +
                          (row.ChangedColumns.Length == 0 ? "" :
                           $"    [{string.Join(", ", row.ChangedColumns.Take(8))}" +
                           (row.ChangedColumns.Length > 8 ? ", ..." : "") + "]"),
                ToolTip = row.ChangedColumns.Length == 0 ? row.Label :
                    $"{row.Label}\nChanged columns: {string.Join(", ", row.ChangedColumns)}",
                IsChecked = _selected.Contains(row),
                IsEnabled = !_replaceTables.Contains(table),
                Margin = new Thickness(2, 2, 0, 2)
            };
            check.Checked += (_, _) => { if (!_refreshing) { _selected.Add(row); RefreshChecks(); } };
            check.Unchecked += (_, _) => { if (!_refreshing) { _selected.Remove(row); RefreshChecks(); } };
            _rowChecks[row] = check;
            item.Items.Add(new TreeViewItem { Header = check });
        }
        if (end < table.Rows.Count)
        {
            var more = new Button
            {
                Content = $"Load more ({table.Rows.Count - end} remaining)",
                Margin = new Thickness(3),
                Style = (Style)FindResource("GhostButton")
            };
            var moreItem = new TreeViewItem { Header = more };
            more.Click += (_, _) =>
            {
                item.Items.Remove(moreItem);
                LoadRows(item, table, end);
            };
            item.Items.Add(moreItem);
        }
    }

    private void RefreshChecks()
    {
        _refreshing = true;
        foreach (var (table, check) in _tableChecks)
        {
            int count = table.Rows.Count(_selected.Contains);
            check.IsChecked = count == 0 ? false : count == table.Rows.Count ? true : null;
            check.IsEnabled = !_replaceTables.Contains(table) && table.Rows.Count > 0;
        }
        foreach (var (table, check) in _replaceChecks) check.IsChecked = _replaceTables.Contains(table);
        foreach (var (row, check) in _rowChecks)
        {
            check.IsChecked = _selected.Contains(row);
            check.IsEnabled = !_replaceTables.Any(table => table.Name == row.Table);
        }
        _refreshing = false;
        UpdateCount();
    }

    private void UpdateCount()
    {
        SelectionText.Text = $"{_selected.Count:n0} row(s) selected across " +
                             $"{_selected.Select(r => r.Table).Distinct().Count():n0} table(s)  ·  " +
                             $"{_replaceTables.Count:n0} whole-table rebuild(s)";
        ApplyButton.IsEnabled = _selected.Count > 0 || _replaceTables.Count > 0;
    }

    private void TableFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_preview is not null) PopulateTables();
    }
    private void SelectNew_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _preview.Tables.SelectMany(t => t.Rows).Where(r => !r.IsConflict)) _selected.Add(row);
        RefreshChecks();
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _selected.Clear();
        _replaceTables.Clear();
        RefreshChecks();
    }
    private void Apply_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is not null)
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }
        if (RootBorder is not null)
            RootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
    }
}

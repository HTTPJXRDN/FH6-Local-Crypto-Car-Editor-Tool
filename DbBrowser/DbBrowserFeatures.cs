using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace FH6LocalCryptoTool.DbBrowser;

public partial class DbBrowserView
{
    readonly Dictionary<string,string> _columnFilters = new();
    BrowserObject? _pageObject;
    (BrowserObject Obj, BrowserRow Row, BrowserColumn Column)? _cell;
    DataGridCellInfo _lastCell;
    bool _settingCell, _cellDirty;

    void AppendSqlLog(string text)
    {
        SqlLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n\n");
        if (SqlLog.Text.Length > 100000) SqlLog.Text = SqlLog.Text[^80000..];
        SqlLog.ScrollToEnd();
    }
    void ClearSqlLog_Click(object sender, RoutedEventArgs e) => SqlLog.Clear();
    bool DiscardCellDraft()
    {
        if (_cellDirty && !Confirm("Discard the unapplied value in Edit Cell? Apply cell first to keep it.")) return false;
        _cellDirty = false; ClearCell(); return true;
    }
    void ClearCell()
    {
        _settingCell = true; _cell = null; _cellDirty = false;
        if (CellText != null) { CellText.Clear(); CellType.SelectedIndex = -1; CellLocation.Text = "Select a cell in Browse Data"; ApplyCellButton.IsEnabled = false; CellText.IsEnabled = false; }
        _settingCell = false;
    }
    void Cell_Changed(object sender, SelectedCellsChangedEventArgs e)
    {
        if (_busy || _settingCell || _page == null || _pageObject == null) return;
        if (_cell != null && _lastCell.Equals(RowsGrid.CurrentCell)) return;
        if (_cellDirty && !DiscardCellDraft()) {
            _settingCell = true; RowsGrid.CurrentCell = _lastCell; RowsGrid.SelectedCells.Clear(); if (_lastCell.IsValid) RowsGrid.SelectedCells.Add(_lastCell); _settingCell = false; return;
        }
        int rowIndex = RowsGrid.Items.IndexOf(RowsGrid.CurrentCell.Item), columnIndex = RowsGrid.CurrentCell.Column == null ? -1 : RowsGrid.Columns.IndexOf(RowsGrid.CurrentCell.Column);
        if (rowIndex < 0 || rowIndex >= _page.Rows.Count || columnIndex < 0 || columnIndex >= _page.Columns.Count) { ClearCell(); return; }
        _lastCell = RowsGrid.CurrentCell;
        _cell = (_pageObject, _page.Rows[rowIndex], _page.Columns[columnIndex]);
        ShowCell();
    }
    void ShowCell()
    {
        if (_cell == null) return;
        var cell = _cell.Value; var value = BrowserValue.From(cell.Row.Values[_page!.Columns.ToList().IndexOf(cell.Column)]);
        _settingCell = true;
        CellLocation.Text = cell.Obj.Name + "\n" + cell.Column.Name + " • " + cell.Column.Type;
        CellType.SelectedItem = CellType.Items.Cast<ComboBoxItem>().First(i=>i.Content.ToString()==value.Kind); CellText.Text = value.Text;
        CellText.IsEnabled = value.Kind != "NULL" && _page.Editable && cell.Column.Hidden == 0;
        CellHelp.Text = !_page.Editable || cell.Column.Hidden != 0 ? "Read-only value. Generated columns and views cannot be edited here." : "Choose a storage type. BLOB uses hex; NULL is not the text ‘NULL’. Decimals accept dot or comma.";
        _cellDirty = false; ApplyCellButton.IsEnabled = false; _settingCell = false;
    }
    void CellText_Changed(object sender, TextChangedEventArgs e) { if (!_settingCell && _cell != null) { _cellDirty = true; ApplyCellButton.IsEnabled = _page?.Editable == true && _cell.Value.Column.Hidden == 0; } }
    void CellType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingCell || _cell == null || _page == null) return;
        _cellDirty = true; CellText.IsEnabled = (CellType.SelectedItem as ComboBoxItem)?.Content.ToString() != "NULL" && _page.Editable && _cell.Value.Column.Hidden == 0;
        ApplyCellButton.IsEnabled = _page.Editable && _cell.Value.Column.Hidden == 0;
    }
    async void ApplyCell_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _database == null || _cell == null || _page?.Editable != true || _cell.Value.Column.Hidden != 0) return;
        var cell = _cell.Value; var value = new BrowserValue((CellType.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "", CellText.Text);
        try { value.Parse(); } catch (Exception ex) { MessageBox.Show(OwnerWindow, ex.Message, "Invalid cell value", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        await Run("Applying cell value…", async token => {
            await Task.Run(()=>_database.UpdateCell(cell.Obj,cell.Row,cell.Column,value)); _cellDirty = false;
            AppendSqlLog($"Updated {cell.Obj.Name}.{cell.Column.Name} (parameterized {value.Kind} value)");
            await LoadPage(token); Status.Text = "Cell updated in working copy.";
        });
    }
    void DiscardCell_Click(object sender, RoutedEventArgs e) => ShowCell();
    void CopyCell_Click(object sender, RoutedEventArgs e) { if (_cell != null) Clipboard.SetText(CellType.SelectedItem is ComboBoxItem { Content: "NULL" } ? "NULL" : CellText.Text); }
    async void NullCell_Click(object sender, RoutedEventArgs e)
    {
        var current = CurrentCell(); if (current == null || _database == null || _page == null) return;
        var (obj,row,index) = current.Value;
        await Run("Setting cell to NULL…", async token => { await Task.Run(()=>_database.UpdateCell(obj,row,_page.Columns[index],new("NULL",""))); await LoadPage(token); Status.Text="Cell set to NULL."; });
    }
    void Rows_RightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? obj = e.OriginalSource as DependencyObject;
        while (obj != null && obj is not DataGridCell) obj = VisualTreeHelper.GetParent(obj);
        if (obj is DataGridCell cell) { RowsGrid.CurrentCell = new DataGridCellInfo(cell); RowsGrid.SelectedCells.Clear(); RowsGrid.SelectedCells.Add(RowsGrid.CurrentCell); }
    }
    void TableSelector_Changed(object sender, SelectionChangedEventArgs e) { if (!_refreshing && !_busy) Tables.SelectedItem = TableSelector.SelectedItem; }
    void BuildSchemaTree()
    {
        SchemaTree.Items.Clear();
        foreach (var type in new[] {"table","view","index","trigger"}) {
            var group = new TreeViewItem { Header = char.ToUpper(type[0]) + type[1..] + "s", IsExpanded = type == "table" };
            foreach (var obj in _objects.Where(o=>o.Type==type && o.Name.Contains(TableSearch.Text,StringComparison.OrdinalIgnoreCase)))
                group.Items.Add(new TreeViewItem { Header=obj.Name,Tag=obj });
            SchemaTree.Items.Add(group);
        }
    }
    void SchemaTree_Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_refreshing || _busy || e.NewValue is not TreeViewItem { Tag: BrowserObject obj }) return;
        if (obj.Type is "table" or "view") { Tables.SelectedItem = Tables.Items.Cast<BrowserObject>().FirstOrDefault(o=>o.Name==obj.Name); TableSelector.SelectedItem=Tables.SelectedItem; }
        else { BrowserTabs.SelectedIndex=0; SchemaObjectsGrid.SelectedItem=obj; }
    }
    void AddColumnFilters()
    {
        if (_page == null) return;
        for (int i=0;i<_page.Columns.Count;i++) {
            string name=_page.Columns[i].Name;
            var header=new StackPanel(); header.Children.Add(new TextBlock {Text=name,Margin=new Thickness(0,0,0,5)});
            var filter=new TextBox { Text=_columnFilters.GetValueOrDefault(name,""), MinWidth=75, Padding=new Thickness(4,2,4,2),ToolTip="Contains text; =exact; >10; <=0.05; NULL or NOT NULL. Enter applies all filters.", Tag=name };
            filter.TextChanged+=(_,_)=> { if(filter.Text.Length==0) _columnFilters.Remove(name); else _columnFilters[name]=filter.Text; };
            filter.KeyDown+=Search_KeyDown; header.Children.Add(filter); RowsGrid.Columns[i].Header=header; RowsGrid.Columns[i].SortMemberPath=name;
        }
    }
    async void Rows_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled=true; if (_busy || _page==null) return;
        string name=e.Column.SortMemberPath;
        bool descending=(SortColumn.SelectedItem as BrowserColumn)?.Name==name && Descending.IsChecked!=true;
        SortColumn.SelectedItem=_page.Columns.FirstOrDefault(c=>c.Name==name); Descending.IsChecked=descending; _pageIndex=0;
        await Run("Sorting table…", LoadPage);
    }
    async void ClearFilters_Click(object sender,RoutedEventArgs e) { _columnFilters.Clear(); RowSearch.Clear(); _pageIndex=0; await Run("Clearing filters…",LoadPage); }
    async void First_Click(object sender,RoutedEventArgs e) { _pageIndex=0; await Run("Loading first page…",LoadPage); }
    async void Last_Click(object sender,RoutedEventArgs e) { if(_page==null)return; _pageIndex=(int)Math.Max(0,(_page.Count-1)/BrowserDatabase.PageSize); await Run("Loading last page…",LoadPage); }
    async void GoPage_KeyDown(object sender,KeyEventArgs e) { if(e.Key!=Key.Enter)return; e.Handled=true; if(_page==null || !int.TryParse(GoPage.Text,out int page) || page<1) return; _pageIndex=(int)Math.Min(page-1,Math.Max(0,(_page.Count-1)/BrowserDatabase.PageSize)); await Run("Loading page…",LoadPage); }
    async void SqlSelection_Click(object sender,RoutedEventArgs e) { if (SqlEditor.SelectionLength==0) {Status.Text="Select SQL text to execute first.";return;} await ExecuteSql(SqlEditor.SelectedText); }
    void Browser_KeyDown(object sender,KeyEventArgs e) {
        if(e.Key==Key.F5){e.Handled=true;if(BrowserTabs.SelectedIndex!=3)Refresh_Click(sender,e);else if(SqlEditor.SelectionLength>0)SqlSelection_Click(sender,e);else Sql_Click(sender,e);}
        else if(e.Key==Key.Enter&&Keyboard.Modifiers==ModifierKeys.Control){e.Handled=true;if(CellText.IsKeyboardFocusWithin)ApplyCell_Click(sender,e);else if(BrowserTabs.SelectedIndex==3){if(SqlEditor.SelectionLength>0)SqlSelection_Click(sender,e);else Sql_Click(sender,e);}}
    }
    async void OpenSql_Click(object sender,RoutedEventArgs e) => await OpenSql(false);
    async void ImportSql_Click(object sender,RoutedEventArgs e) => await OpenSql(true);
    async Task OpenSql(bool import)
    {
        if (_busy || import && _database==null) return;
        var dialog=new OpenFileDialog {Title=import?"Import SQL dump":"Open SQL script",Filter="SQL scripts|*.sql;*.txt|All files|*.*"}; if(dialog.ShowDialog(OwnerWindow)!=true)return;
        string? sql=null;
        await Run("Reading SQL script…",async token=> { sql=await Task.Run(()=> {if(new FileInfo(dialog.FileName).Length>64*1024*1024)throw new InvalidDataException("SQL scripts are limited to 64 MB.");string text=File.ReadAllText(dialog.FileName);return import?BrowserDatabase.NormalizeSqlImport(text,token):text;}); SqlEditor.Text=sql; BrowserTabs.SelectedIndex=3; Status.Text="SQL script loaded. F5 runs it; Ctrl+Enter runs selected text when present."; });
        if(import && sql!=null && Confirm("Import this SQL script into the working copy? Changes roll back together on failure. The script is displayed in Execute SQL for review."))await ExecuteSql(sql);
    }
    async void SaveSql_Click(object sender,RoutedEventArgs e)
    {
        if(_busy)return; string sql=SqlEditor.Text;
        var dialog=new SaveFileDialog {Filter="SQL script|*.sql",FileName="script.sql"}; if(dialog.ShowDialog(OwnerWindow)!=true)return;
        await Run("Saving SQL script…",async _=>{await Task.Run(()=> {using var writer=new StreamWriter(new FileStream(dialog.FileName,FileMode.CreateNew));writer.Write(sql);});Status.Text="Saved SQL script: "+dialog.FileName;},false);
    }
    async void NewDatabase_Click(object sender,RoutedEventArgs e)
    {
        if (_busy || !CommitInlineEdit()) return;
        if(_busy || _database!=null && (_database.Pending||_database.HasChanges) && !Confirm("Discard the current working database and create a new empty SQLite database?"))return;
        await Run("Creating empty working database…",async token=> { var next=await Task.Run(BrowserDatabase.New); _database?.Dispose();_database=next;_objects=[];_columnFilters.Clear();ClearCell();RefreshObjects(null);await LoadPage(token);LoadedPath.Text="New SQLite database (not yet exported)";PropertiesGrid.ItemsSource=await Task.Run(_database.Properties);UserVersion.Text=ApplicationId.Text="0";Status.Text="New database ready. Create a table or import an SQL dump, then export a new file.";});
    }
    async void Pragmas_Click(object sender,RoutedEventArgs e)
    {
        if(_database==null)return;
        if(!int.TryParse(UserVersion.Text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int version) || !int.TryParse(ApplicationId.Text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int id)) {Status.Text="Pragma values must be signed 32-bit whole numbers.";return;}
        await ExecuteSql($"PRAGMA user_version={version.ToString(CultureInfo.InvariantCulture)}; PRAGMA application_id={id.ToString(CultureInfo.InvariantCulture)};");
    }
    async void ExportCsv_Click(object sender,RoutedEventArgs e)
    {
        if(_database==null||Selected==null)return; var obj=Selected;
        var dialog=new SaveFileDialog {Filter="CSV table|*.csv",FileName=obj.Name+".csv",Title="Export all records (not only the filtered page)"};if(dialog.ShowDialog(OwnerWindow)!=true)return;
        await Run("Exporting full table to CSV…",async token=>{await Task.Run(()=>_database.ExportCsv(obj,dialog.FileName,token));Status.Text="Exported all records from "+obj.Name+" to "+dialog.FileName;});
    }
    async void ImportCsv_Click(object sender,RoutedEventArgs e)
    {
        if(_database==null||Selected==null)return;var obj=Selected;
        var dialog=new OpenFileDialog {Filter="CSV table|*.csv",Title="Import CSV into "+obj.Name};if(dialog.ShowDialog(OwnerWindow)!=true)return;
        if(!Confirm("Append CSV records to "+obj.Name+"? Headers must match columns. A constraint or format error rolls back the whole import; existing records are not replaced."))return;
        await Run("Importing CSV records…",async token=>{int rows=await Task.Run(()=>_database.ImportCsv(obj,dialog.FileName,token));AppendSqlLog($"Imported {rows} CSV records into {obj.Name}");await LoadPage(token);Status.Text=$"Imported {rows} records into the working copy.";});
    }
    async void DumpSql_Click(object sender,RoutedEventArgs e)
    {
        if(_database==null)return;var dialog=new SaveFileDialog {Filter="SQL dump|*.sql",FileName="database.sql"};if(dialog.ShowDialog(OwnerWindow)!=true)return;
        await Run("Exporting database SQL dump…",async token=>{await Task.Run(()=>_database.ExportSql(dialog.FileName,token));Status.Text="Exported schema and records: "+dialog.FileName;});
    }
}

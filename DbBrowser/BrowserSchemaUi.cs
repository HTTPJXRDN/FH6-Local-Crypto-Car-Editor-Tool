using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace FH6LocalCryptoTool.DbBrowser;

public partial class DbBrowserView
{
    async void CreateTable_Click(object sender, RoutedEventArgs e)
    {
        if (_database == null || _busy) return;
        var dialog = new CreateTableWindow(Resources) { Owner = OwnerWindow };
        if (dialog.ShowDialog() != true) return;
        await ExecuteSql(dialog.Sql);
    }
    async void CreateIndex_Click(object sender, RoutedEventArgs e)
    {
        if (_database == null || _busy || Selected?.Type != "table") { Status.Text = "Select a table in Browse Data first."; return; }
        var obj=Selected;
        string? sql=null;
        await Run("Preparing index…",async _=>{
            var columns=await Task.Run(()=>_database.Columns(obj.Name));
            var dialog=new SchemaActionWindow("Create index • "+obj.Name,Resources) {Owner=OwnerWindow};
            var name=dialog.TextField("Index name", "IX_"+obj.Name);
            var unique=new CheckBox { Content="Unique index",Foreground=Brushes.White,Margin=new Thickness(0,8,0,8)};dialog.Add(unique);
            var fields=new ListBox {SelectionMode=SelectionMode.Multiple,ItemsSource=columns.Select(c=>c.Name).ToArray(),MinHeight=140,MaxHeight=260};dialog.Add(fields);
            dialog.Validate=()=> { if(string.IsNullOrWhiteSpace(name.Text)||fields.SelectedItems.Count==0)throw new InvalidOperationException("Enter an index name and select at least one column."); };
            if(dialog.ShowDialog()==true)sql="CREATE "+(unique.IsChecked==true?"UNIQUE ":"")+"INDEX "+BrowserDatabase.Quote(name.Text)+" ON "+BrowserDatabase.Quote(obj.Name)+" ("+string.Join(",",fields.SelectedItems.Cast<string>().Select(BrowserDatabase.Quote))+");";
        },false);
        if(sql!=null)await ExecuteSql(sql);
    }
    async void ModifyObject_Click(object sender,RoutedEventArgs e)
    {
        if(_database==null||_busy)return;
        var obj=SchemaObjectsGrid.SelectedItem as BrowserObject ?? Selected; if(obj==null)return;
        if(obj.Type!="table") {
            SqlEditor.Text="-- Review and run this atomic replacement.\nDROP "+obj.Type.ToUpperInvariant()+" "+BrowserDatabase.Quote(obj.Name)+";\n"+obj.Sql.TrimEnd(';')+";";
            BrowserTabs.SelectedIndex=3;Status.Text="Object definition loaded in Execute SQL. Edit it and run when ready.";return;
        }
        string? sql=null;
        await Run("Preparing table modification…",async _=>{
            var columns=await Task.Run(()=>_database.Columns(obj.Name));
            var dialog=new SchemaActionWindow("Modify table • "+obj.Name,Resources){Owner=OwnerWindow};
            var op=dialog.Choice("Operation",["Rename table","Add column","Rename column","Drop column"]);
            var column=dialog.Choice("Existing column (rename/drop)",columns.Select(c=>c.Name).ToArray());
            var name=dialog.TextField("New table / column name","");
            var type=dialog.Choice("Type for new column",["TEXT","INTEGER","REAL","BLOB","NUMERIC"]);
            dialog.Validate=()=>{if(op.SelectedIndex!=3&&string.IsNullOrWhiteSpace(name.Text))throw new InvalidOperationException("Enter a new name.");};
            if(dialog.ShowDialog()!=true)return;
            string prefix="ALTER TABLE "+BrowserDatabase.Quote(obj.Name);
            sql=op.SelectedIndex switch {
                0=>prefix+" RENAME TO "+BrowserDatabase.Quote(name.Text),
                1=>prefix+" ADD COLUMN "+BrowserDatabase.Quote(name.Text)+" "+type.SelectedItem,
                2=>prefix+" RENAME COLUMN "+BrowserDatabase.Quote(column.SelectedItem?.ToString()??"")+" TO "+BrowserDatabase.Quote(name.Text),
                _=>prefix+" DROP COLUMN "+BrowserDatabase.Quote(column.SelectedItem?.ToString()??"")
            }+";";
            if(op.SelectedIndex==3&&!Confirm("Drop column "+column.SelectedItem+" and its values from "+obj.Name+" in the working copy?"))sql=null;
        },false);
        if(sql!=null)await ExecuteSql(sql);
    }
    async void DropObject_Click(object sender,RoutedEventArgs e)
    {
        if(_database==null||_busy)return;
        var obj=SchemaObjectsGrid.SelectedItem as BrowserObject ?? Selected;if(obj==null)return;
        if(!Confirm("Delete "+obj.Type+" "+obj.Name+" from the working copy? Deleting a table removes all of its records. Revert changes can undo this before Write changes/export."))return;
        await ExecuteSql("DROP "+obj.Type.ToUpperInvariant()+" "+BrowserDatabase.Quote(obj.Name)+";");
    }
}

sealed class SchemaActionWindow : Window
{
    readonly StackPanel _fields=new();
    public Action? Validate {get;set;}
    public SchemaActionWindow(string title,ResourceDictionary theme)
    {
        Title=title;Width=580;Height=500;MinHeight=300;MinWidth=450;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.FromRgb(29,29,31));Foreground=Brushes.White;Resources.MergedDictionaries.Add(theme);
        var root=new DockPanel {Margin=new Thickness(16)};Content=root;
        var heading=new TextBlock {Text=title,FontSize=18,Foreground=new SolidColorBrush(Color.FromRgb(232,23,93)),Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        actions.Children.Add(new Button{Content="Cancel",IsCancel=true});var apply=new Button {Content="Apply to working copy",Background=new SolidColorBrush(Color.FromRgb(232,23,93))};actions.Children.Add(apply);
        DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);root.Children.Add(new ScrollViewer {Content=_fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        apply.Click+=(_,_)=>{try{Validate?.Invoke();DialogResult=true;}catch(Exception ex){MessageBox.Show(this,ex.Message,"Invalid definition",MessageBoxButton.OK,MessageBoxImage.Warning);}};
    }
    public void Add(UIElement element)=>_fields.Children.Add(element);
    public TextBox TextField(string label,string value){Add(new TextBlock {Text=label,Margin=new Thickness(0,8,0,5)});var box=new TextBox{Text=value};Add(box);return box;}
    public ComboBox Choice(string label,string[] choices){Add(new TextBlock{Text=label,Margin=new Thickness(0,8,0,5)});var combo=new ComboBox{ItemsSource=choices,SelectedIndex=0};Add(combo);return combo;}
}

public sealed class TableColumnDraft
{
    public string Name {get;set;}="";
    public string Type {get;set;}="TEXT";
    public bool PrimaryKey {get;set;}
    public bool NotNull {get;set;}
    public bool Unique {get;set;}
    public string Default {get;set;}="";
}

sealed class CreateTableWindow : Window
{
    public string Sql {get;private set;}="";
    public CreateTableWindow(ResourceDictionary theme)
    {
        Title="Create table";Width=900;Height=560;MinWidth=600;MinHeight=350;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.FromRgb(29,29,31));Foreground=Brushes.White;Resources.MergedDictionaries.Add(theme);
        var layout=new DockPanel{Margin=new Thickness(16)};Content=layout;
        var header=new StackPanel();header.Children.Add(new TextBlock{Text="Create table",Foreground=new SolidColorBrush(Color.FromRgb(232,23,93)),FontSize=18});
        header.Children.Add(new TextBlock{Text="Table name",Margin=new Thickness(0,8,0,5)});var name=new TextBox();header.Children.Add(name);
        header.Children.Add(new TextBlock{Text="Add rows below. Default is a literal value (NULL is SQL NULL); expressions and foreign-key constraints can be added through Execute SQL.",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGray,Margin=new Thickness(0,8,0,12)});
        DockPanel.SetDock(header,Dock.Top);layout.Children.Add(header);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        actions.Children.Add(new Button{Content="Cancel",IsCancel=true});var create=new Button{Content="Create table",Background=new SolidColorBrush(Color.FromRgb(232,23,93))};actions.Children.Add(create);
        DockPanel.SetDock(actions,Dock.Bottom);layout.Children.Add(actions);
        var drafts=new System.Collections.ObjectModel.ObservableCollection<TableColumnDraft>{new(){Name="Id",Type="INTEGER",PrimaryKey=true}};
        var grid=new DataGrid{AutoGenerateColumns=false,CanUserAddRows=true,CanUserDeleteRows=true,ItemsSource=drafts};
        grid.Columns.Add(new DataGridTextColumn{Header="Column name",Binding=new Binding("Name"),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        grid.Columns.Add(new DataGridComboBoxColumn{Header="Type",ItemsSource=new[]{"INTEGER","REAL","TEXT","BLOB","NUMERIC"},SelectedItemBinding=new Binding("Type"),Width=100});
        foreach(var property in new[]{"PrimaryKey","NotNull","Unique"})grid.Columns.Add(new DataGridCheckBoxColumn{Header=property,Binding=new Binding(property),Width=75});
        grid.Columns.Add(new DataGridTextColumn{Header="Default literal",Binding=new Binding("Default"),Width=160});layout.Children.Add(grid);
        create.Click+=(_,_)=>{
            if(!grid.CommitEdit(DataGridEditingUnit.Cell,true)||!grid.CommitEdit(DataGridEditingUnit.Row,true))return;
            try {
                if(string.IsNullOrWhiteSpace(name.Text)||drafts.Count==0||drafts.Any(c=>string.IsNullOrWhiteSpace(c.Name)))throw new InvalidOperationException("Enter a table name and a name for every column.");
                if(drafts.Select(c=>c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=drafts.Count)throw new InvalidOperationException("Column names must be unique.");
                var definitions=drafts.Select(c=>BrowserDatabase.Quote(c.Name)+" "+c.Type+(c.NotNull?" NOT NULL":"")+(c.Unique?" UNIQUE":"")+(c.Default.Length==0?"":" DEFAULT "+Default(c))).ToList();
                var keys=drafts.Where(c=>c.PrimaryKey).Select(c=>BrowserDatabase.Quote(c.Name)).ToArray();if(keys.Length>0)definitions.Add("PRIMARY KEY ("+string.Join(",",keys)+")");
                Sql="CREATE TABLE "+BrowserDatabase.Quote(name.Text)+" ("+string.Join(",",definitions)+");";DialogResult=true;
            } catch(Exception ex){MessageBox.Show(this,ex.Message,"Invalid table",MessageBoxButton.OK,MessageBoxImage.Warning);}
        };
    }
    static string Default(TableColumnDraft column) => column.Default.Equals("NULL",StringComparison.OrdinalIgnoreCase)?"NULL":BrowserDatabase.SqlLiteral(new BrowserValue(column.Type is "INTEGER" or "REAL" or "BLOB"?column.Type:"TEXT",column.Default).Parse());
}

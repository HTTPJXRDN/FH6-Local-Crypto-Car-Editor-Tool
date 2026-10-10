using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using FH6CarEditor;

namespace FH6LocalCryptoTool.GarageViewer;

public sealed class GarageRow(GarageEntry entry, string name, bool modelLocked) : INotifyPropertyChanged
{
    // Stable reference identity is essential for WPF selection dictionaries:
    // asynchronous thumbnail changes must not change a row's equality/hash.
    public GarageEntry Entry { get; } = entry;
    public string Name { get; } = name;
    public string MediaName { get; init; } = "";
    public bool ModelLocked { get; } = modelLocked;
    public BitmapSource? Thumbnail { get; private set; }
    public string ThumbnailStatus { get; private set; } = "Not cached";
    internal bool ThumbnailRequested;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void SetThumbnail(GarageThumbnailResult result) {
        Thumbnail=result.Image;ThumbnailStatus=result.Status;
        PropertyChanged?.Invoke(this,new(nameof(Thumbnail)));PropertyChanged?.Invoke(this,new(nameof(ThumbnailStatus)));
    }
    public string Manufacturer { get; init; } = "Unknown manufacturer";
    public long Id => Entry.Id;
    public long CarId => Entry.CarId;
    public string PI => (Entry.PerformanceIndex is > 0 and <= 2 ? Entry.PerformanceIndex * 1000 : Entry.PerformanceIndex).ToString("0", CultureInfo.InvariantCulture);
    public string Status => Entry.IsProtected ? Entry.Protection : ModelLocked ? "Removal locked" : Entry.Favorite ? "Favorite" : "";
}

public partial class GarageViewerView : UserControl
{
    GarageProfileSession? _session;
    GarageCarCatalog? _catalog;
    GarageThumbnailCache? _thumbnails;
    CarThumbnailLibrary? _stockThumbnails;
    string? _catalogSource;
    GarageThumbnailResolver? _resolver;
    GarageThumbnailCache? _resolverCache;
    CarThumbnailLibrary? _resolverStock;
    string _thumbnailMessage="Thumbnail cache not found. Choose its folder to load actual garage previews.";
    long[] _detailIds=Array.Empty<long>();
    long? _detailId=>_detailIds.Length==1?_detailIds[0]:null;
    Dictionary<string,string> _detailOriginal = new();
    readonly HashSet<string> _detailMixed = new();
    GarageRow? _previewRow;
    GarageThumbnailCache? _previewCache;
    CarThumbnailLibrary? _previewStock;
    int _previewGeneration;
    bool _updatingDetails, _restoringSelection;
    GridLength _carFieldsHeight = new(1.4,GridUnitType.Star);
    GarageRow[] _rows = Array.Empty<GarageRow>();
    bool _busy, _started;
    Window? _owner;
    public GarageViewerView() { InitializeComponent(); }
    void GarageFields_Expanded(object sender,RoutedEventArgs e) {
        if(GarageFieldsRow==null || GarageListHeightSplitter==null)return;
        GarageFieldsRow.MinHeight=60;GarageFieldsRow.Height=_carFieldsHeight;
        GarageListHeightSplitter.Visibility=Visibility.Visible;
    }
    void GarageFields_Collapsed(object sender,RoutedEventArgs e) {
        if(GarageFieldsRow==null || GarageListHeightSplitter==null)return;
        if(GarageFieldsRow.ActualHeight>=60)_carFieldsHeight=GarageFieldsRow.Height;
        GarageFieldsRow.MinHeight=0;GarageFieldsRow.Height=GridLength.Auto;
        GarageListHeightSplitter.Visibility=Visibility.Collapsed;
    }
    async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        if (TryFindResource("CardHeadGrad") != null) GarageHeader.SetResourceReference(Border.BackgroundProperty, "CardHeadGrad");
        if (TryFindResource("PinkAccentLine") != null) GarageHeader.SetResourceReference(Border.BorderBrushProperty, "PinkAccentLine");
        _owner = Window.GetWindow(this);
        if (_owner != null) { _owner.Closing += Owner_Closing; _owner.Closed += Owner_Closed; }
        await RunAsync(() => {
            _catalog = GarageCarCatalog.Embedded();
            _stockThumbnails = FindStockThumbnails();
            if(Directory.Exists(GarageThumbnailCache.DefaultDirectory)) {
                try { _thumbnails=new GarageThumbnailCache(GarageThumbnailCache.DefaultDirectory);_thumbnailMessage=$"Garage thumbnail cache: {_thumbnails.Count:N0} references (read-only)"; }
                catch(Exception ex) when(ex is IOException or SystemException) { _thumbnailMessage="Thumbnail cache unavailable; choose its folder to retry. Garage editing is still available."; }
            }
        }, "Loading car catalog and thumbnail references…");
    }
    void Owner_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; StatusText.Text = "Wait for the current operation to finish, then close again."; return; }
        if(!TryApplyCarFields()) {e.Cancel=true;return;}
        if (!e.Cancel && !ConfirmDiscard()) e.Cancel = true;
    }
    void Owner_Closed(object? sender, EventArgs e) { _previewGeneration++;_session?.Dispose(); _catalog?.Dispose(); _thumbnails?.Dispose(); }
    bool ConfirmDiscard() => _session?.HasChanges != true || MessageBox.Show("Discard unexported Garage Viewer edits? Your original save has not changed.", "Garage Viewer", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    async Task RunAsync(Action action, string message, string? success = null)
    {
        if (_busy) return;
        _busy = true; WorkArea.IsEnabled = false; StatusText.Text = message;
        try { await Task.Run(action); Refresh(); StatusText.Text = success ?? "Ready. Original files are unchanged."; }
        catch (Exception ex) { StatusText.Text = "Error: " + ex.Message; }
        finally { _busy = false; WorkArea.IsEnabled = true; UpdateButtons(); }
    }
    async Task LoadProfileAsync(string path)
    {
        if (_busy || !TryApplyCarFields() || !ConfirmDiscard()) return;
        ClearCarFields();
        await RunAsync(() => {
            var next = GarageProfileSession.Load(path);
            var previous = _session; _session = next; previous?.Dispose();
        }, "Reading garage on a private copy…");
    }
    async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "Load FH6 C_ProfileData", Filter = "Profile saves (C_ProfileData*)|C_ProfileData*|All files|*.*" };
        if (dialog.ShowDialog() == true) await LoadProfileAsync(dialog.FileName);
    }
    async void Catalog_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !TryApplyCarFields()) return;
        var dialog = new OpenFileDialog { Title = "Matching full FH6 GameDB catalog", Filter = "Game databases|*.slt;*.sqlite;*.db|All files|*.*" };
        if (dialog.ShowDialog() == true) await RunAsync(() => { var next = GarageCarCatalog.Load(dialog.FileName); var previous = _catalog; _catalog = next; _catalogSource=dialog.FileName; previous?.Dispose(); _stockThumbnails=FindStockThumbnails(); }, "Reading car names and stock parts…");
    }
    static string StockSourceSetting => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForzaModTool", "garage-stock-thumbnail-source.json");
    CarThumbnailLibrary? FindStockThumbnails(string? manual = null, bool resetAuto = false) {
        string? preferred=manual;
        if(!resetAuto && preferred==null) {
            try { if(File.Exists(StockSourceSetting) && new FileInfo(StockSourceSetting).Length<16384) preferred=System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(StockSourceSetting)); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        if(preferred!=null) {
            try {return new CarThumbnailLibrary(preferred);}
            catch(Exception ex) when(manual==null && ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        foreach(string path in CarThumbnailLibrary.FindSources(_catalogSource,false).Concat(new[]{CarThumbnailLibrary.SavedSource(false)}).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)) {
            try {return new CarThumbnailLibrary(path);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }
    async void StockThumbnailFolder_Click(object sender,RoutedEventArgs e) {
        if(_busy || !TryApplyCarFields())return;
        var dialog=new OpenFolderDialog{Title="Choose FH6 media/Stripped folder, or stock model image folder"};
        if(dialog.ShowDialog()==true)await RunAsync(()=>{
            var next=FindStockThumbnails(dialog.FolderName);
            Directory.CreateDirectory(Path.GetDirectoryName(StockSourceSetting)!);
            File.WriteAllText(StockSourceSetting,System.Text.Json.JsonSerializer.Serialize(dialog.FolderName));
            _stockThumbnails=next;
        },"Indexing stock model thumbnails…");
    }
    async void StockThumbnailAuto_Click(object sender,RoutedEventArgs e) {
        if(_busy || !TryApplyCarFields())return;
        await RunAsync(()=>{
            if(File.Exists(StockSourceSetting))File.Delete(StockSourceSetting);
            _stockThumbnails=FindStockThumbnails(resetAuto:true);
            // Re-read the same actual cache so newly generated garage thumbnails win.
            string directory=_thumbnails?.DirectoryPath ?? GarageThumbnailCache.DefaultDirectory;
            try {var next=new GarageThumbnailCache(directory);var previous=_thumbnails;_thumbnails=next;previous?.Dispose();_thumbnailMessage=$"Garage thumbnail cache: {next.Count:N0} references (read-only)";}
            catch(Exception ex) when(ex is IOException or SystemException) { }
        },"Finding stock thumbnails and refreshing garage cache…");
    }
    GarageThumbnailResolver ThumbnailResolver() {
        if(_resolver==null || !ReferenceEquals(_resolverCache,_thumbnails) || !ReferenceEquals(_resolverStock,_stockThumbnails)) {
            _resolverCache=_thumbnails;_resolverStock=_stockThumbnails;_resolver=new(_thumbnails,_stockThumbnails);
        }
        return _resolver;
    }
    async void ThumbnailFolder_Click(object sender, RoutedEventArgs e) {
        if(_busy || !TryApplyCarFields())return;
        var dialog=new OpenFolderDialog{Title="Choose FH6 CacheThumbnails folder (.manifest and WebP files)",InitialDirectory=Directory.Exists(GarageThumbnailCache.DefaultDirectory)?GarageThumbnailCache.DefaultDirectory:""};
        if(dialog.ShowDialog()==true) await RunAsync(()=>{
            var next=new GarageThumbnailCache(dialog.FolderName);var previous=_thumbnails;_thumbnails=next;previous?.Dispose();
            _thumbnailMessage=$"Garage thumbnail cache: {next.Count:N0} references (read-only)";
        },"Reading thumbnail manifest…");
    }
    async Task LoadThumbnailAsync(GarageRow row) {
        var cache=_thumbnails;var stock=_stockThumbnails;var resolver=ThumbnailResolver();
        if(row.ThumbnailRequested)return;row.ThumbnailRequested=true;
        var result=await resolver.GetAsync(row.Entry.ThumbnailReference,row.CarId,row.MediaName);
        if(ReferenceEquals(cache,_thumbnails) && ReferenceEquals(stock,_stockThumbnails))row.SetThumbnail(result);
    }
    void SyncPreview(GarageRow? car) {
        if(ReferenceEquals(_previewRow,car)&&ReferenceEquals(_previewCache,_thumbnails)&&ReferenceEquals(_previewStock,_stockThumbnails))return;
        _previewRow=car;_previewCache=_thumbnails;_previewStock=_stockThumbnails;int generation=++_previewGeneration;
        SelectedCarPreview.Source=null;SelectedPreviewStatus.Text="";SelectedPreviewPlaceholder.Visibility=Visibility.Visible;
        SelectedPreviewPlaceholder.Text=car==null?"Select one car for its preview":"Loading garage preview…";
        if(car!=null)_=LoadSelectedPreviewAsync(car,_thumbnails,_stockThumbnails,ThumbnailResolver(),generation);
    }
    async Task LoadSelectedPreviewAsync(GarageRow car,GarageThumbnailCache? cache,CarThumbnailLibrary? stock,GarageThumbnailResolver resolver,int generation) {
        var result=await resolver.GetAsync(car.Entry.ThumbnailReference,car.CarId,car.MediaName,true);
        if(generation!=_previewGeneration || !ReferenceEquals(cache,_thumbnails) || !ReferenceEquals(stock,_stockThumbnails))return;
        SelectedCarPreview.Source=result.Image;
        SelectedPreviewStatus.Text=result.Image==null?"":car.Name+" • "+result.Status;
        SelectedPreviewPlaceholder.Text=result.Status;SelectedPreviewPlaceholder.Visibility=result.Image==null?Visibility.Visible:Visibility.Collapsed;
    }
    async void Thumbnail_Loaded(object sender,RoutedEventArgs e) {
        if(sender is Image{DataContext:GarageRow row})await LoadThumbnailAsync(row);
    }
    async void Thumbnail_DataContextChanged(object sender,DependencyPropertyChangedEventArgs e) {
        if(sender is Image image && image.IsLoaded && e.NewValue is GarageRow row)await LoadThumbnailAsync(row);
    }
    void Refresh(long? selectId = null)
    {
        var models = _catalog?.Cars.ToDictionary(c => c.Id);
        _rows = _session?.Entries().Select(c => models != null && models.TryGetValue(c.CarId, out var model)
            ? new GarageRow(c, model.DisplayName + (model.IsInitialDrive ? " [Initial Drive variant]" : ""), model.RemovalLocked) { Manufacturer = model.Manufacturer, MediaName=model.MediaName } : new GarageRow(c, $"Unknown car ({c.CarId}) — load matching GameDB", false)).ToArray() ?? Array.Empty<GarageRow>();
        LoadedPath.Text = _session == null ? "Open or drop a C_ProfileData save here. The original file is never overwritten."
            : _session.SourcePath + (_session.HasChanges ? " • unexported edits" : " • private working copy") + (_session.CanEdit ? "" : "\n" + _session.EditRestriction);
        CatalogLabel.Text = _catalog == null ? "No car catalog loaded." : $"{_catalog.Label} • {_catalog.Cars.Count(c => !c.IsInitialDrive):N0} selectable models • load your GameDB for newer/custom cars";
        ThumbnailCacheLabel.Text=_thumbnailMessage;
        StockThumbnailLabel.Text=_stockThumbnails==null?"Stock fallback not found • Auto-find or choose a folder":$"Stock fallback: {_stockThumbnails.Count:N0} models • cached images first";
        StockThumbnailLabel.ToolTip=_stockThumbnails?.SourcePath;
        ApplyGarageFilter(); ApplyCatalogFilter();
        if (selectId.HasValue) { GarageGrid.SelectedItems.Clear(); GarageGrid.SelectedItem = _rows.FirstOrDefault(c => c.Id == selectId.Value); RevealGarageSelection(); }
        UpdateButtons();
    }
    static bool Matches(string name, long id, string query) => name.Contains(query, StringComparison.OrdinalIgnoreCase) || id.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase);
    void ApplyGarageFilter()
    {
        string query = GarageSearch.Text.Trim();
        var selected = GarageGrid.SelectedItems.Cast<GarageRow>().Select(c => c.Id).ToHashSet();
        var filtered = _rows.Where(c => Matches(c.Name, c.CarId, query) || c.Manufacturer.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Id.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool previous=_restoringSelection;_restoringSelection=true;
        try {GarageGrid.SelectedItems.Clear();GarageGrid.ItemsSource = Grouped(filtered);GarageGrid.SelectedItems.Clear();foreach(var car in filtered.Where(c => selected.Contains(c.Id))) GarageGrid.SelectedItems.Add(car);}
        finally {_restoringSelection=previous;}
        ExpandSearchResults(GarageGrid, query);
        CountLabel.Text = _session == null ? "No save loaded" : $"{filtered.Length:N0} shown / {_rows.Length:N0} owned";
        if(RemoveButton!=null)UpdateButtons();
    }
    void ApplyCatalogFilter()
    {
        var selected = CatalogList.SelectedItems.Cast<GarageCarDefinition>().Select(c => c.Id).ToHashSet();
        string query = CatalogSearch.Text.Trim();
        var filtered = _catalog?.Cars.Where(c => !c.IsInitialDrive && (Matches(c.DisplayName, c.Id, query) || c.Manufacturer.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray() ?? Array.Empty<GarageCarDefinition>();
        CatalogList.ItemsSource = Grouped(filtered);
        foreach(var car in filtered.Where(c => selected.Contains(c.Id))) CatalogList.SelectedItems.Add(car);
        ExpandSearchResults(CatalogList, query);
    }
    static ListCollectionView Grouped(System.Collections.IList items) {
        var view = new ListCollectionView(items);
        view.SortDescriptions.Add(new SortDescription("Manufacturer", ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
        view.GroupDescriptions.Add(new PropertyGroupDescription("Manufacturer") { CustomSort = new ManufacturerComparer() });
        return view;
    }
    sealed class ManufacturerComparer : System.Collections.IComparer {
        public int Compare(object? x, object? y) => StringComparer.OrdinalIgnoreCase.Compare(
            Convert.ToString((x as CollectionViewGroup)?.Name), Convert.ToString((y as CollectionViewGroup)?.Name));
    }
    void Manufacturer_Loaded(object sender, RoutedEventArgs e) {
        if(sender is Expander expander) {
            bool owned = Equals(expander.Tag, "Garage");
            string query = owned ? GarageSearch.Text : CatalogSearch.Text;
            if(!string.IsNullOrWhiteSpace(query)) expander.IsExpanded = true;
        }
    }
    void SelectGarageManufacturer_Click(object sender, RoutedEventArgs e) {
        if(_busy || sender is not Button { DataContext: CollectionViewGroup group } || !TryApplyCarFields()) return;
        _restoringSelection=true;
        try {GarageGrid.SelectedItems.Clear();foreach(var car in group.Items.Cast<GarageRow>().Where(Removable)) GarageGrid.SelectedItems.Add(car);}
        finally {_restoringSelection=false;}
        UpdateButtons(); e.Handled=true;
    }
    void SelectCatalogManufacturer_Click(object sender, RoutedEventArgs e) {
        if(_busy || sender is not Button { DataContext: CollectionViewGroup group }) return;
        CatalogList.SelectedItems.Clear();
        foreach(var car in group.Items.Cast<GarageCarDefinition>()) CatalogList.SelectedItems.Add(car);
        UpdateButtons(); e.Handled=true;
    }
    static bool Removable(GarageRow car) => !car.Entry.IsProtected && !car.ModelLocked && !car.Name.StartsWith("Unknown car (");
    static IEnumerable<Expander> Expanders(DependencyObject parent) {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is Expander expander) yield return expander;
            foreach(var nested in Expanders(child)) yield return nested;
        }
    }
    static void ExpandSearchResults(ItemsControl control, string query) {
        // Group containers may be recycled without firing Loaded again.
        // Apply search expansion explicitly to the current visual containers.
        control.UpdateLayout();
        foreach(var expander in Expanders(control)) expander.IsExpanded=query.Length>0;
    }
    void RevealGarageSelection() {
        GarageGrid.UpdateLayout();
        var makes=GarageGrid.SelectedItems.Cast<GarageRow>().Select(c=>c.Manufacturer).ToHashSet();
        foreach(var expander in Expanders(GarageGrid))
            if(expander.DataContext is CollectionViewGroup group && makes.Contains(Convert.ToString(group.Name)!)) expander.IsExpanded=true;
        if(GarageGrid.SelectedItem!=null)GarageGrid.ScrollIntoView(GarageGrid.SelectedItem);
    }
    void Search_Changed(object sender, TextChangedEventArgs e) { if (GarageGrid != null && !_busy && TryApplyCarFields()) ApplyGarageFilter(); }
    void CatalogSearch_Changed(object sender, TextChangedEventArgs e) { if (CatalogList != null && !_busy) ApplyCatalogFilter(); }
    void Selection_Changed(object sender, SelectionChangedEventArgs e) {
        if(RemoveButton==null || _restoringSelection)return;
        if(!_busy && ReferenceEquals(sender,GarageGrid) && _detailIds.Length>0 &&
            !_detailIds.SequenceEqual(GarageGrid.SelectedItems.Cast<GarageRow>().Select(c=>c.Id).Order()) && !TryApplyCarFields()) {
            var previous=_rows.Where(c=>_detailIds.Contains(c.Id)&&GarageGrid.Items.Contains(c)).ToArray();
            if(previous.Length!=0) {
                // Restore after WPF finishes its selection transaction, otherwise
                // its transient multi-selection can clear the invalid draft.
                _restoringSelection=true;
                Dispatcher.BeginInvoke(new Action(()=>{
                    try {GarageGrid.SelectedItems.Clear();foreach(var row in previous)GarageGrid.SelectedItems.Add(row);}
                    finally {_restoringSelection=false;}
                    UpdateButtons();
                }));
            }
            return;
        }
        UpdateButtons();
    }
    void UpdateButtons()
    {
        bool editable = !_busy && _session?.CanEdit == true;
        var selected = GarageGrid.SelectedItems.Cast<GarageRow>().ToArray();
        RemoveButton.IsEnabled = editable && selected.Length != 0 && selected.All(Removable);
        RemoveButton.Content = selected.Length > 0 ? $"Remove selected ({selected.Length})…" : "Remove selected…";
        DuplicateButton.IsEnabled = editable && selected.Length == 1;
        CurrentButton.IsEnabled = editable && _session?.CanChangeCurrentCar == true && selected.Length == 1 && selected[0].Id != _session.CurrentGarageId;
        int additions=CatalogList.SelectedItems.Count;
        AddButton.IsEnabled = editable && _catalog != null && additions > 0;
        AddButton.Content = additions > 0 ? $"Add selected cars ({additions})" : "Add selected cars";
        ExportButton.IsEnabled = !_busy && (_session?.HasChanges == true || CarFieldsDirty());
        RevertButton.IsEnabled = !_busy && (_session?.HasChanges == true || CarFieldsDirty());
        SelectionDetails.Text = selected.Length == 1 ? $"Garage ID {selected[0].Id} • Car ID {selected[0].CarId} • VIN {selected[0].Entry.Guid}" + (selected[0].Status.Length != 0 ? "\n" + selected[0].Status : "")
            : selected.Length > 1 ? $"{selected.Length} garage instances selected. Ctrl-click or Shift-click to change the selection." : "Expand a manufacturer. Ctrl-click or Shift-click to select multiple cars.";
        if(!_busy)SyncCarFields(selected);
    }
    CheckBox FieldToggle(string field)=>field switch{"OriginalOwner"=>OwnerFieldToggle,"DistanceDriven"=>DistanceFieldToggle,_=>SpeedFieldToggle};
    bool CarFieldsDirty() => _detailIds.Length>1 ? _detailOriginal.Keys.Any(field=>FieldToggle(field).IsChecked==true)
        : _detailIds.Length==1 && _detailOriginal.Any(field=>CarFieldText(field.Key)!=field.Value);
    string CarFieldText(string field)=>field switch{"OriginalOwner"=>OriginalOwnerBox.Text,"DistanceDriven"=>DistanceDrivenBox.Text,_=>TopSpeedBox.Text};
    void ClearCarFields() {
        bool previous=_updatingDetails;_updatingDetails=true;
        try {_detailIds=Array.Empty<long>();_detailOriginal.Clear();_detailMixed.Clear();OriginalOwnerBox.Text="";DistanceDrivenBox.Text="";TopSpeedBox.Text="";OwnerFieldToggle.IsChecked=false;DistanceFieldToggle.IsChecked=false;SpeedFieldToggle.IsChecked=false;UpdateMixedHints();ApplyCarFieldsButton.IsEnabled=false;}
        finally {_updatingDetails=previous;}
    }
    void SyncCarFields(GarageRow[] selected) {
        GarageRow? car=selected.Length==1?selected[0]:null;
        CarDetailsPanel.DataContext=car;
        SyncPreview(car);
        bool batch=selected.Length>1;
        BatchFieldHint.Visibility=batch?Visibility.Visible:Visibility.Collapsed;
        foreach(var toggle in new[]{OwnerFieldToggle,DistanceFieldToggle,SpeedFieldToggle})toggle.Visibility=batch?Visibility.Visible:Visibility.Collapsed;
        foreach(var label in new[]{OriginalOwnerLabel,DistanceDrivenLabel,TopSpeedLabel})label.Visibility=batch?Visibility.Collapsed:Visibility.Visible;
        bool supported=_session?.CanEditCarDetails==true && selected.Length>0;
        CarFieldsArea.IsEnabled=supported;
        ApplyCarFieldsButton.Content=batch?$"Apply checked fields to {selected.Length} cars":"Apply car fields";
        ApplyCarFieldsButton.IsEnabled=supported&&CarFieldsDirty();
        CarFieldHelp.Text=selected.Length==0 ? "Select garage cars to edit their fields." : !supported ? "Car fields are unavailable for this profile layout." : batch
            ? $"{selected.Length} cars selected. Only checked fields are replaced, in one transaction. Raw DB units; Original Owner is a label, not account identity. Export to keep edits."
            : "Raw Career_Garage values. Apply fields or select another car to save them to the working copy. Original Owner is a label, not your account identity.";
        if(car!=null)_=LoadThumbnailAsync(car);
        var ids=selected.Select(c=>c.Id).Order().ToArray();
        if(_detailIds.SequenceEqual(ids))return;
        _updatingDetails=true;
        try {
            ClearCarFields();OriginalOwnerBox.Text="";DistanceDrivenBox.Text="";TopSpeedBox.Text="";
            if(supported) {
                var details=ids.Select(id=>_session!.CarDetails(id)).ToArray();
                string Common(string field) {
                    var values=details.Select(row=>row[field] is DBNull?"":field=="TopSpeed"?Convert.ToDouble(row[field],CultureInfo.InvariantCulture).ToString("R",CultureInfo.CurrentCulture):Convert.ToString(row[field],CultureInfo.CurrentCulture)??"").Distinct(StringComparer.Ordinal).ToArray();
                    if(values.Length==1)return values[0];_detailMixed.Add(field);return "";
                }
                OriginalOwnerBox.Text=Common("OriginalOwner");DistanceDrivenBox.Text=Common("DistanceDriven");TopSpeedBox.Text=Common("TopSpeed");
                _detailIds=ids;
                _detailOriginal=new(){["OriginalOwner"]=OriginalOwnerBox.Text,["DistanceDriven"]=DistanceDrivenBox.Text,["TopSpeed"]=TopSpeedBox.Text};
                UpdateMixedHints();
            }
        } catch(Exception ex) {StatusText.Text="Car fields unavailable: "+ex.Message;ClearCarFields();CarFieldsArea.IsEnabled=false;}
        finally {_updatingDetails=false;ApplyCarFieldsButton.IsEnabled=false;}
    }
    void CarField_Changed(object sender,TextChangedEventArgs e) {
        if(_updatingDetails || ApplyCarFieldsButton==null)return;
        if(_detailIds.Length>1)FieldToggle(ReferenceEquals(sender,OriginalOwnerBox)?"OriginalOwner":ReferenceEquals(sender,DistanceDrivenBox)?"DistanceDriven":"TopSpeed").IsChecked=true;
        UpdateFieldButtons();
    }
    void BatchFieldToggle_Changed(object sender,RoutedEventArgs e) {if(!_updatingDetails&&ApplyCarFieldsButton!=null)UpdateFieldButtons();}
    void UpdateMixedHints() {
        if(OwnerMixedText==null)return;
        foreach(var pair in new[]{("OriginalOwner",OwnerMixedText),("DistanceDriven",DistanceMixedText),("TopSpeed",SpeedMixedText)})
            pair.Item2.Visibility=_detailMixed.Contains(pair.Item1)&&CarFieldText(pair.Item1).Length==0&&FieldToggle(pair.Item1).IsChecked!=true?Visibility.Visible:Visibility.Collapsed;
    }
    void UpdateFieldButtons() {
        UpdateMixedHints();
        bool dirty=CarFieldsDirty();ApplyCarFieldsButton.IsEnabled=!_busy&&_session?.CanEditCarDetails==true&&dirty;
        RevertButton.IsEnabled=!_busy&&(_session?.HasChanges==true||dirty);
        ExportButton.IsEnabled=!_busy&&(_session?.HasChanges==true||dirty);
    }
    bool TryApplyCarFields() {
        if(_updatingDetails || !CarFieldsDirty())return true;
        if(_busy || _session==null || _detailIds.Length==0)return false;
        var changes=_detailOriginal.Where(field=>_detailIds.Length>1?FieldToggle(field.Key).IsChecked==true:CarFieldText(field.Key)!=field.Value).ToDictionary(field=>field.Key,field=>CarFieldText(field.Key));
        try {
            int affected=_session.UpdateCarDetails(_detailIds,changes);
            _updatingDetails=true;
            try {foreach(string field in changes.Keys) {FieldToggle(field).IsChecked=false;_detailMixed.Remove(field);}}
            finally {_updatingDetails=false;}
            foreach(var change in changes)_detailOriginal[change.Key]=change.Value;
            UpdateFieldButtons();StatusText.Text=$"Car fields saved to working copy ({affected} cars changed). Export edited save to keep them.";return true;
        } catch(Exception ex) {StatusText.Text="Car fields not applied: "+ex.Message;return false;}
    }
    void ApplyCarFields_Click(object sender,RoutedEventArgs e) {
        if(_busy)return;
        long? id=_detailId;if(TryApplyCarFields()) {string status=StatusText.Text;ClearCarFields();Refresh(id);StatusText.Text=status;}
    }
    async Task RemoveSelectedAsync(Func<string, bool> confirm)
    {
        if (_busy || _session == null || !TryApplyCarFields()) return;
        var selected = GarageGrid.SelectedItems.Cast<GarageRow>().ToArray();
        if (selected.Length == 0 || !confirm($"Remove {selected.Length} selected garage instance(s) and their purchased parts from this working copy?\n\nNo live save, livery file, or tune file will be deleted. Export to a new save to keep the result.")) return;
        var ids = selected.Select(c => c.Id).ToArray();
        var locked = _catalog?.Cars.Where(c => c.RemovalLocked).Select(c => c.Id).ToHashSet();
        if (selected.Any(c => c.Name.StartsWith("Unknown car ("))) { StatusText.Text = "Load a matching GameDB before removing unknown models."; return; }
        await RunAsync(() => _session.Remove(ids, locked), "Removing selected garage instances…", "Removed from working copy. Export edited save to keep these changes.");
    }
    async void Remove_Click(object sender, RoutedEventArgs e) => await RemoveSelectedAsync(text => MessageBox.Show(text, "Remove garage cars", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
    async void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || GarageGrid.SelectedItem is not GarageRow selected || !TryApplyCarFields()) return;
        long id = 0;
        await RunAsync(() => id = _session.Duplicate(selected.Id), "Duplicating car with a new garage ID and VIN…", "Duplicated in working copy. Export edited save to keep it.");
        if (id != 0) Refresh(id);
    }
    async void Current_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || GarageGrid.SelectedItem is not GarageRow selected || !TryApplyCarFields()) return;
        await RunAsync(() => _session.SetCurrentCar(selected.Id), "Changing current-car reference…", "Current car changed in working copy. Export edited save to keep this selection.");
        Refresh(selected.Id);
    }
    async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || _catalog == null || !TryApplyCarFields()) return;
        var selected=CatalogList.SelectedItems.Cast<GarageCarDefinition>().Select(c=>c.Id).ToArray();
        if(selected.Length==0)return;
        IReadOnlyList<long> ids=Array.Empty<long>();
        await RunAsync(() => ids = _session.AddStocks(selected, _catalog), $"Creating {selected.Length} stock car configuration(s)…", $"Added {selected.Length} stock car(s) to working copy. Export a new save to keep them.");
        if(ids.Count>0) {
            _restoringSelection=true;
            try {GarageGrid.SelectedItems.Clear();foreach(var row in _rows.Where(c=>ids.Contains(c.Id))) GarageGrid.SelectedItems.Add(row);}
            finally {_restoringSelection=false;}
            RevealGarageSelection();UpdateButtons();
        }
    }
    async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || MessageBox.Show("Revert all garage edits to the snapshot taken when this save was loaded?", "Garage Viewer", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ClearCarFields();await RunAsync(() => _session.Revert(), "Restoring original garage snapshot…");
    }
    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || !TryApplyCarFields()) return;
        var dialog = new SaveFileDialog { Title = "Export a new C_ProfileData save — never overwrite your original", FileName = "C_ProfileData.garage-edited", Filter = "Profile save|*.*", OverwritePrompt = false };
        if (dialog.ShowDialog() == true) await RunAsync(() => _session.Export(dialog.FileName), "Exporting and verifying save payload…", "Exported: " + dialog.FileName + "\nOriginal save unchanged. Back up your whole save and close the game before installation.");
    }
    async void File_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && File.Exists(paths[0])) await LoadProfileAsync(paths[0]);
    }
    void File_DragOver(object sender, DragEventArgs e) { e.Effects = !_busy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && File.Exists(paths[0]) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
}

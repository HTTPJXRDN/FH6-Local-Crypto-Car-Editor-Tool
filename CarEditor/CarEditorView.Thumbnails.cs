using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace FH6CarEditor;

public partial class CarEditorView
{
    CarThumbnailLibrary? _thumbnailLibrary;
    int _thumbnailSourceRevision, _thumbnailPreviewRevision;
    bool ThumbnailMotorsport => HasNativeTrackOffsets;

    async Task ConfigureCarThumbnailsAsync(string? manualSource = null, bool resetAuto = false)
    {
        int revision = ++_thumbnailSourceRevision;
        ++_thumbnailPreviewRevision;
        bool motorsport = ThumbnailMotorsport;
        string? database = _origPath;
        _thumbnailLibrary = null;
        ThumbnailSource.Text = "Finding model thumbnails…";
        ShowCarPreviewPlaceholder("Finding model thumbnails…");
        try {
            if (resetAuto) CarThumbnailLibrary.SaveSource(motorsport, null);
            var library = await Task.Run(() => {
                string? preferred = manualSource ?? (resetAuto ? null : CarThumbnailLibrary.SavedSource(motorsport));
                // Upgrade the old auto/offline fallback to the complete stock packs.
                // Explicit picks during this request still win.
                if (manualSource == null && preferred != null && Path.GetFileName(preferred).Equals("OfflineThumbnails.zip",StringComparison.OrdinalIgnoreCase))
                    preferred = CarThumbnailLibrary.FindSources(database,motorsport).FirstOrDefault(Directory.Exists) ?? preferred;
                if (preferred != null) {
                    try { return new CarThumbnailLibrary(preferred); }
                    catch (Exception ex) when (manualSource == null && ex is IOException or UnauthorizedAccessException) { }
                }
                foreach (string path in CarThumbnailLibrary.FindSources(database, motorsport)) {
                    try { return new CarThumbnailLibrary(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                return null;
            });
            if (revision != _thumbnailSourceRevision) return;
            _thumbnailLibrary = library;
            if (library != null) {
                ThumbnailSource.Text = $"{Path.GetFileName(library.SourcePath)} · {library.Count} model thumbnails";
                ThumbnailSource.ToolTip = library.SourcePath;
                if (manualSource != null) {
                    try { CarThumbnailLibrary.SaveSource(motorsport, library.SourcePath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Preview source works, but its preference could not be saved.", "warn"); }
                }
            } else {
                ThumbnailSource.Text = motorsport ? "FM8: choose converted PNG/WebP thumbnails" : "Thumbnail source not found · choose a folder/archive";
                ThumbnailSource.ToolTip = motorsport ? "Native FM8 LZX archives are not decoded here. Extracted PC BC7 swatchbin or converted images can be named thumbnail_<Car ID>_big.png or <MediaName>.png." : "Choose media/Stripped to read RC*.zip thumbnails in memory, or choose an image folder/archive manually.";
            }
            await UpdateCarThumbnailAsync();
        } catch (Exception ex) {
            if (revision != _thumbnailSourceRevision) return;
            ThumbnailSource.Text = "Thumbnail source unavailable";
            ThumbnailSource.ToolTip = ex.Message;
            ShowCarPreviewPlaceholder(ex.Message);
            Log("Car preview: " + ex.Message, "warn");
        }
    }

    void ShowCarPreviewPlaceholder(string text)
    {
        CarPreviewImage.Source = null;
        CarPreviewImage.ToolTip = null;
        CarPreviewPlaceholder.Text = text;
        CarPreviewPlaceholder.Visibility = Visibility.Visible;
        CarPreviewName.Text = "";
        CarPreviewStatus.Text = "";
    }

    async Task UpdateCarThumbnailAsync()
    {
        if (CarPreviewImage == null || CarList == null) return;
        int revision = ++_thumbnailPreviewRevision;
        if (CarList.SelectedItems.Count != 1 || CarList.SelectedItem is not CarItem car) {
            ShowCarPreviewPlaceholder(CarList.SelectedItems.Count > 1 ? "Multiple cars selected · select one for its preview" : "Select one car to see its default thumbnail");
            return;
        }
        var library = _thumbnailLibrary;
        ShowCarPreviewPlaceholder(library == null ? "No thumbnail source available · use Auto-find, Folder or Archive" : "Loading thumbnail…");
        CarPreviewName.Text = $"{car.DisplayYear} {Naming.Friendly(car.Media)}";
        if (library == null) return;
        var result = await Task.Run(() => library.Load(car.Id, car.Media));
        // Slow disk/image decoding must never paint a previously selected car.
        if (revision != _thumbnailPreviewRevision || library != _thumbnailLibrary) return;
        CarPreviewImage.Source = result.Image;
        CarPreviewImage.ToolTip = result.Source;
        CarPreviewStatus.Text = result.Status;
        CarPreviewPlaceholder.Text = "Thumbnail unavailable for this car";
        CarPreviewPlaceholder.Visibility = result.Image == null ? Visibility.Visible : Visibility.Collapsed;
    }

    async void ThumbnailAuto_Click(object sender, RoutedEventArgs e) => await ConfigureCarThumbnailsAsync(resetAuto: true);
    async void ThumbnailFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose FH6 media/Stripped folder, or model thumbnail image folder" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await ConfigureCarThumbnailsAsync(dialog.FolderName);
    }
    async void ThumbnailArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose car thumbnail archive (RC*.zip or OfflineThumbnails.zip)", Filter = "Thumbnail archives (*.zip)|*.zip" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await ConfigureCarThumbnailsAsync(dialog.FileName);
    }
}

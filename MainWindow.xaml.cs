using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FH6LocalCryptoTool;

public partial class MainWindow : Window
{
    private string? _templateSlt;          // original .slt used as the re-encrypt template
    private string? _templateIni;          // original encrypted .ini used as its re-encrypt template
    private readonly Dictionary<string, string> _assetTemplates = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastDecryptedSqlite;  // base DB for merges
    private string? _outputDir;            // if set, all outputs go here instead of next to the input

    private string? _pendingInput;         // file dropped on the main zone, waiting for Decrypt/Re-encrypt
    private string? _pendingOverlay;       // file dropped on the merge zone, waiting for Merge
    private bool _cryptoBusy;
    private string _fh6KeyUsage = "GameDB"; // Automatically selected by the staged file; no manual override.

    private Paragraph _logPara = null!;    // colored log sink

    // drag-highlight brushes (pink palette)
    private static readonly Brush IdleBorder = Freeze(Color.FromRgb(0xCC, 0x52, 0x7A));
    private static readonly Brush HotBorder  = Freeze(Color.FromRgb(0xE8, 0x17, 0x5D));
    // log run colors
    private static readonly Brush TimeBrush  = Freeze(Color.FromRgb(0xE8, 0x17, 0x5D));
    private static readonly Brush TextBrush  = Freeze(Color.FromRgb(0xD6, 0xD6, 0xD6));

    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // Load the window/taskbar icon whether the asset is embedded (Resource) or a loose file (Content).
    private void ApplyAppIcon()
    {
        // 1) embedded resource (Build Action: Resource) — reached via pack URI
        foreach (var uri in new[]
        {
            "pack://application:,,,/Assets/Logo/dbtool_icon.png",
            "pack://application:,,,/Assets/ICO/dbtool_icon.ico",
        })
        {
            try { Icon = BitmapFrame.Create(new Uri(uri, UriKind.Absolute)); return; }
            catch { /* not embedded with that path — try the next candidate */ }
        }

        // 2) loose file next to the .exe (Build Action: Content, Copy if newer)
        foreach (var rel in new[]
        {
            Path.Combine("Assets", "Logo", "dbtool_icon.png"),
            Path.Combine("Assets", "ICO", "dbtool_icon.ico"),
        })
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, rel);
                if (!File.Exists(path)) continue;

                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;   // load now so the file isn't locked
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.EndInit();
                bi.Freeze();
                Icon = bi;
                return;
            }
            catch { /* keep trying */ }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        ApplyAppIcon();

        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        _logPara = new Paragraph { Margin = new Thickness(0), LineHeight = 18 };
        doc.Blocks.Add(_logPara);
        LogBox.Document = doc;

        Log("Ready. Drop a file onto a zone, then click Decrypt, Re-encrypt, Export Car Related DB, or Merge.");
    }

    // ---------- dark native title bar (Win10 1809+ / Win11) ----------
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (fallback 19 on older builds)
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch { /* non-Windows-11 or older: harmless */ }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is not null)
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized
                ? "\uE923"
                : "\uE922";
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized
                ? "Restore"
                : "Maximize";
        }
        if (RootBorder is not null)
            RootBorder.BorderThickness = WindowState == WindowState.Maximized
                ? new Thickness(0)
                : new Thickness(1);
    }

    private Fh6Keys.MethodKey DetectedFh6Key()
        => Fh6Keys.Get(_fh6KeyUsage);

    // ---------- drag visuals ----------
    private void Drop_DragEnter(object sender, DragEventArgs e) => SetHot(sender, true);
    private void Drop_DragLeave(object sender, DragEventArgs e) => SetHot(sender, false);

    private void Drop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void SetHot(object sender, bool hot)
    {
        var brush = hot ? HotBorder : IdleBorder;
        if (ReferenceEquals(sender, DropBorder))      DropDash.Stroke = brush;
        else if (ReferenceEquals(sender, MergeBorder)) MergeBorder.BorderBrush = hot ? HotBorder : new SolidColorBrush(Color.FromRgb(0x47, 0x47, 0x47));
    }

    private static string[] GetFiles(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop) ? (string[])e.Data.GetData(DataFormats.FileDrop)! : Array.Empty<string>();

    // ---------- drop = stage only; work happens on button click ----------
    private void Drop_Drop(object sender, DragEventArgs e)
    {
        SetHot(sender, false);
        var files = GetFiles(e);
        if (files.Length == 0) return;
        StageInput(files[0]);
    }

    private void StageInput(string path)
    {
        ProfileTemplateRow.Visibility = Visibility.Collapsed;
        DbTemplateRow.Visibility = Visibility.Visible;
        if (MotorsportTrackArchive.IsExtractOnlyFolder(path))
        {
            _pendingInput = path; // Keep it staged so buttons cannot fall back to an older SQLite.
            StagedText.Text = $"{Path.GetFileName(path)}   (extract-only ZIP output; no repacking)";
            StagedText.Visibility = Visibility.Visible;
            Log("This large/deduplicated ZIP was extracted for inspection only. Re-encrypt/repack is not supported; no ZIP template was created.");
            Status("Extract-only output, not a repack workspace.");
            return;
        }
        if (Fh6ZipArchive.IsWorkspace(path))
        {
            _pendingInput = path;
            _fh6KeyUsage = "General";
            StagedText.Text = $"Staged: {Path.GetFileName(path)}   (FH6 ZIP workspace → Re-encrypt / rebuild)";
            StagedText.Visibility = Visibility.Visible;
            Log("FH6 ZIP workspace staged. Click Re-encrypt to rebuild; keep the original template and workspace metadata.");
            Status("FH6 ZIP workspace staged.");
            return;
        }
        if (MotorsportLzxArchive.IsWorkspace(path))
        {
            _pendingInput = path;
            _fh6KeyUsage = "General";
            StagedText.Text = $"Staged: {Path.GetFileName(path)}   (LZX ZIP workspace → Re-encrypt / repack)";
            StagedText.Visibility = Visibility.Visible;
            Log("LZX workspace staged. Click Re-encrypt to rebuild the original ZIP entry set; no encryption key is used.");
            Status("ZIP workspace staged.");
            return;
        }
        if (Directory.Exists(path))
        {
            _pendingInput = path; // Invalid folders must never fall back to a previously staged DB.
            Log($"Drop a file or an extracted ZIP workspace containing {MotorsportLzxArchive.ManifestName} or {Fh6ZipArchive.ManifestName}.");
            Status("Not a ZIP round-trip workspace.");
            return;
        }
        _pendingInput = path;
        if (StageProfileInput(path)) return;
        bool skeld = IsSkeld(path);
        bool skeldJson = IsSkeldJson(path);
        bool zip = string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase) || MotorsportCmsArchive.HasZipSignature(path);
        bool sqlite = IsSqlite(path);
        bool plainText = !skeld && !skeldJson && !zip && !sqlite && IsTextIni(path);  // decrypted text asset → re-encrypt
        bool asset = !skeld && !skeldJson && !zip && !sqlite && !plainText && IsAssetContainer(path);
        // Asset/zip/plain-text use the General key; everything else (a gamedb .slt, a
        // decrypted gamedb .sqlite, or an unknown container) uses GameDB. Setting GameDB
        // explicitly here — rather than leaving whatever was selected before — stops a
        // prior .ini/.zip General selection from carrying over onto a .slt and corrupting
        // the decrypt.
        if (!skeld && !skeldJson)
            _fh6KeyUsage = (zip || asset || plainText) ? "General" : "GameDB";
        var dbFormat = !skeld && !skeldJson && !zip && !sqlite
            ? TryDetectGameDbFormat(path) : GameDbContainerFormat.Kind.Unknown;
        string what = skeld ? ".skeld → decode editable JSON (no key)"
                    : skeldJson ? ".skeld.json → rebuild .skeld (no key)"
                    : zip ? ".zip → decrypt & extract"
                    : plainText ? "decrypted text → Re-encrypt"
                    : sqlite ? ".sqlite → Re-encrypt"
                    : asset ? "encrypted asset → Decrypt"
                    : dbFormat == GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32
                        ? ".slt → Forza Motorsport TransformIT GameDB"
                    : ".slt → Decrypt";
        StagedText.Text = $"Staged: {Path.GetFileName(path)}   ({what})";
        StagedText.Visibility = Visibility.Visible;
        if (sqlite) Log("Decrypted DB staged. Re-encrypt it, or click Export Car Related DB to make a one-car merge donor.");
        Log($"Staged {Path.GetFileName(path)} — click {((sqlite || plainText || skeldJson) ? "Re-encrypt" : "Decrypt")} to run.{((zip || asset || plainText) ? " General key selected automatically." : "")}");
        Status("File staged.");
    }

    private void Merge_Drop(object sender, DragEventArgs e)
    {
        SetHot(sender, false);
        var files = GetFiles(e);
        if (files.Length == 0) return;
        _pendingOverlay = files[0];
        MergeStagedText.Text = $"Overlay: {Path.GetFileName(_pendingOverlay)}";
        MergeStagedText.Visibility = Visibility.Visible;
        Log($"Staged overlay {Path.GetFileName(_pendingOverlay)} — click Merge to apply.");
        Status("Overlay staged.");
    }

    // ---------- action buttons ----------
    private async void Decrypt_Click(object sender, RoutedEventArgs e)
    {
        if (_cryptoBusy) return;
        if (_pendingInput is null)
        {
            Log("Nothing staged — drop a gamedbRC.slt onto the drop zone first.");
            Status("Nothing to decrypt.");
            return;
        }
        if (MotorsportTrackArchive.IsExtractOnlyFolder(_pendingInput))
        {
            Log("This is an extract-only output folder; drop the original ZIP to extract again.");
            Status("Extract-only output: no decrypt/repack action.");
            return;
        }
        if (ProfileCrypto.IsPlainPayload(_pendingInput))
        {
            Log("This is already a decrypted FH6 profile — use Re-encrypt with a profile template.");
            Status("Wrong action for a decrypted profile.");
            return;
        }
        if (IsSqlite(_pendingInput))
        {
            Log("Staged file is a .sqlite — use Re-encrypt, not Decrypt.");
            Status("Wrong action for a .sqlite.");
            return;
        }
        if (Fh6ZipArchive.IsWorkspace(_pendingInput) || MotorsportLzxArchive.IsWorkspace(_pendingInput) || IsSkeldJson(_pendingInput) || IsTextIni(_pendingInput))
        {
            Log("Staged file is already plain text — use Re-encrypt.");
            Status("Wrong action for a decrypted file.");
            return;
        }
        try
        {
            SetCryptoBusy(true);
            string ext = Path.GetExtension(_pendingInput);
            if (ProfileCrypto.IsProfilePath(_pendingInput))
                await DecryptProfileFlow(_pendingInput);
            else if (IsSkeld(_pendingInput))
                DecodeSkeldFlow(_pendingInput);
            else if (string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase) || MotorsportCmsArchive.HasZipSignature(_pendingInput))
                await ExtractZipFlow(_pendingInput);
            else if (string.Equals(ext, ".slt", StringComparison.OrdinalIgnoreCase))
                await DecryptFlow(_pendingInput);                             // gamedb container (explicit)
            else if (string.Equals(ext, ".ini", StringComparison.OrdinalIgnoreCase))
                await DecryptAssetFlow(_pendingInput);                       // asset container (explicit)
            // otherwise decide by structure so any extension works (AnimResourceConfig, etc.)
            else if (IsAssetContainer(_pendingInput) && !IsGamedbContainer(_pendingInput))
                await DecryptAssetFlow(_pendingInput);
            else if (IsGamedbContainer(_pendingInput))
                await DecryptFlow(_pendingInput);
            else if (IsAssetContainer(_pendingInput))
                await DecryptAssetFlow(_pendingInput);
            else
                await DecryptFlow(_pendingInput);                             // fall back to gamedb
        }
        catch (Exception ex) { Fail(ex, _pendingInput); }
        finally { SetCryptoBusy(false); }
    }

    private async void ReEncrypt_Click(object sender, RoutedEventArgs e)
    {
        if (_cryptoBusy) return;
        // prefer the explicitly staged file; fall back to the last decrypted/merged DB
        string? src = _pendingInput ?? _lastDecryptedSqlite;
        if (src is null)
        {
            Log("Nothing staged — drop a .sqlite onto the drop zone first (or decrypt one).");
            Status("Nothing to re-encrypt.");
            return;
        }
        if (MotorsportTrackArchive.IsExtractOnlyFolder(src))
        {
            Log("Repacking/re-encryption is disabled for this extract-only ZIP output. No files were written.");
            Status("Extract-only output: repacking is not supported.");
            return;
        }
        bool fh6Workspace = Fh6ZipArchive.IsWorkspace(src);
        bool profilePayload = !fh6Workspace && (ProfileCrypto.IsDatabaseInput(src) || ProfileCrypto.IsPlainPayload(src));
        bool zipWorkspace = fh6Workspace || MotorsportLzxArchive.IsWorkspace(src);
        bool skeldJson = !zipWorkspace && IsSkeldJson(src);
        bool plainText = !zipWorkspace && !skeldJson && IsTextIni(src);
        if (!profilePayload && !zipWorkspace && !IsSqlite(src) && !plainText && !skeldJson)
        {
            Log("Staged file is not a decrypted SQLite or text asset.");
            Status("Wrong action for this file.");
            return;
        }
        try
        {
            SetCryptoBusy(true);
            if (profilePayload) await EncryptProfileFlow(src);
            else if (fh6Workspace) await RepackFh6ZipFlow(src);
            else if (zipWorkspace) await RepackLzxZipFlow(src);
            else if (skeldJson) EncodeSkeldFlow(src);
            else if (plainText) await EncryptAssetFlow(src);
            else await EncryptFlow(src);
        }
        catch (Exception ex) { Fail(ex, src); }
        finally { SetCryptoBusy(false); }
    }

    private async void ExportCarRelated_Click(object sender, RoutedEventArgs e)
    {
        if (!ExportCarRelatedButton.IsEnabled) return;
        string? src = _pendingInput;
        if (src is null || (!IsSqlite(src) && !GameDbSqliteBridge.IsSlt(src)) || !File.Exists(src))
        {
            Log("Export Car Related DB needs a GameDB .slt or decrypted .sqlite dropped on the top Crypto zone.");
            Status("Stage a GameDB first.");
            return;
        }
        ExportCarRelatedButton.IsEnabled = false;
        try
        {
            Status("Preparing staged GameDB...");
            using var sourceDb = await Task.Run(() => GameDbSqliteBridge.Materialize(src));
            Status("Reading cars from staged database...");
            var cars = await Task.Run(() => CarRelatedDbExport.ListCars(sourceDb.SqlitePath));
            if (cars.Count == 0) throw new InvalidOperationException("No cars were found in the staged database.");
            var picker = new CarRelatedExportWindow(src, cars) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedCarId is not long carId)
            {
                Status("Car-related export cancelled.");
                return;
            }
            var save = new SaveFileDialog
            {
                Title = "Save car-related merge donor",
                Filter = "SQLite database (*.sqlite)|*.sqlite",
                DefaultExt = ".sqlite",
                AddExtension = true,
                InitialDirectory = OutputDirFor(src),
                FileName = $"car-{carId}.related.sqlite",
                OverwritePrompt = false
            };
            if (save.ShowDialog(this) != true)
            {
                Status("Car-related export cancelled.");
                return;
            }
            if (File.Exists(save.FileName))
                throw new IOException("That output file already exists. Please choose a new name; no file was overwritten.");
            sourceDb.VerifySourceUnchanged();
            Status($"Exporting all related rows for car {carId}...");
            Log($"Exporting all related rows for car {carId} from {Path.GetFileName(src)}...");
            var result = await Task.Run(() => CarRelatedDbExport.Export(sourceDb.SqlitePath, carId, save.FileName));
            Log($"Created single-car donor: {Path.GetFileName(result.Path)} ({result.Rows:n0} rows in {result.Tables} tables).");
            foreach (string warning in result.Warnings) Log("    " + warning);
            Log("Drop this .sqlite in the merge donor zone and click Import car DB. It is not game-ready by itself.");
            Done(result.Path, "Car-related donor exported.");
        }
        catch (Exception ex) { Fail(ex, src); }
        finally { ExportCarRelatedButton.IsEnabled = true; }
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (!MergeBtn.IsEnabled) return;
        if (_pendingOverlay is null)
        {
            Log("No donor staged — drop a .slt or decrypted .sqlite onto the merge zone first.");
            Status("Nothing to merge.");
            return;
        }
        MergeBtn.IsEnabled = UpdateMergeBtn.IsEnabled = CarRelatedImportBtn.IsEnabled = false;
        try { await MergeFlow(_pendingOverlay); } catch (Exception ex) { Fail(ex, _pendingOverlay); }
        finally { MergeBtn.IsEnabled = UpdateMergeBtn.IsEnabled = CarRelatedImportBtn.IsEnabled = true; }
    }

    private async void UpdateMerge_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdateMergeBtn.IsEnabled) return;
        if (_pendingOverlay is null)
        {
            Log("Drop your old modded GameDB onto the merge zone first.");
            Status("No modded DB staged.");
            return;
        }
        MergeBtn.IsEnabled = UpdateMergeBtn.IsEnabled = CarRelatedImportBtn.IsEnabled = false;
        try { await GameUpdateMergeFlow(_pendingOverlay); }
        catch (Exception ex) { Fail(ex, _pendingOverlay); }
        finally { MergeBtn.IsEnabled = UpdateMergeBtn.IsEnabled = CarRelatedImportBtn.IsEnabled = true; }
    }

    private async void CarRelatedImport_Click(object sender, RoutedEventArgs e)
    {
        if (!CarRelatedImportBtn.IsEnabled) return;
        if (_pendingOverlay is null)
        {
            Log("No donor staged - drop an exported single-car .sqlite onto the merge zone first.");
            Status("Nothing to import.");
            return;
        }
        MergeBtn.IsEnabled = UpdateMergeBtn.IsEnabled = CarRelatedImportBtn.IsEnabled = false;
        try { await CarRelatedImportFlow(_pendingOverlay); }
        catch (Exception ex) { Fail(ex, _pendingOverlay); }
        finally { MergeBtn.IsEnabled = UpdateMergeBtn.IsEnabled = CarRelatedImportBtn.IsEnabled = true; }
    }

    // ---------- flows ----------
    private void DecodeSkeldFlow(string path)
    {
        Status($"Decoding {Path.GetFileName(path)}…");
        byte[] source = File.ReadAllBytes(path);
        string json = SkeldCodec.Decode(source);
        string name = Path.GetFileNameWithoutExtension(path) + ".decrypted.skeld.json";
        string output = UniqueOutputPath(Path.Combine(OutputDirFor(path), name));
        File.WriteAllText(output, json, new System.Text.UTF8Encoding(false));
        Log($"Decoded BSI skeleton (not encrypted): {Path.GetFileName(path)} -> {Path.GetFileName(output)}");
        Log("    Edit existing bone IDs, parents, and transforms; keep the embedded original bytes intact.");
        Done(output, "SKELD decoded to JSON.");
    }

    private void EncodeSkeldFlow(string path)
    {
        Status($"Rebuilding {Path.GetFileName(path)}…");
        byte[] outputBytes = SkeldCodec.Encode(File.ReadAllText(path));
        string output = UniqueOutputPath(Path.Combine(OutputDirFor(path), SkeldOutputFileName(path)));
        File.WriteAllBytes(output, outputBytes);
        Log($"Rebuilt BSI skeleton (no encryption): {Path.GetFileName(path)} -> {Path.GetFileName(output)}");
        Done(output, "SKELD rebuilt and validated.");
    }

    private static string SkeldOutputFileName(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        foreach (string suffix in new[] { ".modded", ".skeld", ".decrypted" })
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                stem = stem[..^suffix.Length];
        return stem + ".modded.skeld";
    }

    private static string UniqueOutputPath(string path)
    {
        if (!File.Exists(path)) return path;
        string directory = Path.GetDirectoryName(path)!;
        string fileName = Path.GetFileName(path);
        string extension = fileName.EndsWith(".skeld.json", StringComparison.OrdinalIgnoreCase)
            ? ".skeld.json" : Path.GetExtension(path);
        string stem = fileName[..^extension.Length];
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(directory, $"{stem}-{i}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private async Task DecryptFlow(string sltPath)
    {
        Status($"Decrypting {Path.GetFileName(sltPath)}…");
        string outPath = UniqueOutputPath(Path.Combine(
            OutputDirFor(sltPath),
            Path.GetFileNameWithoutExtension(sltPath) + ".decrypted.sqlite"));
        var format = await Task.Run(() =>
        {
            using var materialized = GameDbSqliteBridge.Materialize(sltPath, allowMotorsport: true);
            if (materialized.TemplateSlt is null)
                throw new InvalidDataException("This is already a valid SQLite database; use Re-encrypt instead.");
            File.Copy(materialized.SqlitePath, outPath);
            return materialized.Format;
        });

        _templateSlt = sltPath;
        _lastDecryptedSqlite = outPath;
        TemplateText.Text = sltPath;

        long encryptedSize = new FileInfo(sltPath).Length;
        long sqliteSize = new FileInfo(outPath).Length;
        Log($"Decrypted [{GameDbContainerFormat.DisplayName(format)}]  {Path.GetFileName(sltPath)}  ->  {Path.GetFileName(outPath)}");
        Log($"    {encryptedSize:n0} -> {sqliteSize:n0} bytes.  SQLite integrity: OK");
        Log($"    template set to this .slt (used when you re-encrypt).");
        Done(outPath, "Decrypted OK.");
    }

    private async Task EncryptFlow(string sqlitePath)
    {
        string? template = ResolveTemplate(sqlitePath);
        if (template is null)
        {
            Log("Re-encrypt needs the original .slt as a template. Pick it with Browse… or decrypt one first.");
            Status("No template .slt set.");
            return;
        }

        Status($"Re-encrypting {Path.GetFileName(sqlitePath)}…");

        string stem = Path.GetFileNameWithoutExtension(sqlitePath);
        if (stem.EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase)) stem = stem[..^10];
        string outPath = UniqueOutputPath(Path.Combine(
            OutputDirFor(sqlitePath),
            stem + ".re-encrypted.slt"));
        var format = await Task.Run(() =>
        {
            GameDbSqliteBridge.EncryptSnapshot(sqlitePath, template, outPath, allowMotorsport: true);
            return GameDbContainerFormat.Detect(template);
        });
        Log($"Re-encrypted [{GameDbContainerFormat.DisplayName(format)}]  {Path.GetFileName(sqlitePath)}  ->  {Path.GetFileName(outPath)}");
        Log($"    template: {Path.GetFileName(template)}");
        Log($"    {new FileInfo(sqlitePath).Length:n0} -> {new FileInfo(outPath).Length:n0} bytes.  Copy it into the game as the matching GameDB .slt.");
        Done(outPath, "Re-encrypted OK.");
    }

    private async Task ExtractZipFlow(string zipPath)
    {
        var key = DetectedFh6Key();
        string outDir = Path.Combine(OutputDirFor(zipPath), Path.GetFileNameWithoutExtension(zipPath) + ".extracted");
        Status($"Decrypting and extracting {Path.GetFileName(zipPath)}…");
        if (await Task.Run(() => MotorsportTrackArchive.HasExtractOnlyEntries(zipPath)))
        {
            for (int number = 1; Directory.Exists(outDir) || File.Exists(outDir); number++)
                outDir = Path.Combine(OutputDirFor(zipPath), Path.GetFileNameWithoutExtension(zipPath) + $".extracted.{number}");
            var progress = new Progress<MotorsportTrackArchive.ExtractionProgress>(p =>
                Status($"Extracting LZX ZIP: {p.Completed:n0}/{p.EntryCount:n0} entries, {p.OutputBytes / 1048576d:n1} MiB…"));
            var extracted = await Task.Run(() => MotorsportTrackArchive.Extract(zipPath, outDir, progress: progress));
            Log($"Extracted [Forza LZX ZIP / extract-only, all lengths/CRCs verified] {extracted.EntryCount:n0} entries, {extracted.OutputBytes:n0} bytes -> {outDir}");
            if (extracted.SharedEntries != 0) Log($"    Resolved {extracted.SharedEntries:n0} exact shared-data IDs from {extracted.DedupePath}; Dedupe.zip was read only, not copied in full.");
            Log("    Extract-only output: no repacking/re-encryption or game installation. Original game files were not changed.");
            Done(outDir, "LZX ZIP extracted and verified (extract-only).");
            return;
        }
        if (await Task.Run(() => MotorsportLzxArchive.HasLzxEntries(zipPath)))
        {
            for (int number = 1; Directory.Exists(outDir) || File.Exists(outDir); number++)
                outDir = Path.Combine(OutputDirFor(zipPath), Path.GetFileNameWithoutExtension(zipPath) + $".extracted.{number}");
            var lzx = await Task.Run(() => MotorsportLzxArchive.Extract(zipPath, outDir));
            Log($"Extracted [Forza LZX ZIP / all CRCs checked, no encryption] {lzx.EntryCount:n0} entries, {lzx.OutputBytes:n0} bytes -> {outDir}");
            Log($"    Edit the extracted files, then drop this folder or {MotorsportLzxArchive.ManifestName} and click Re-encrypt to repack.");
            Log("    Keep the workspace metadata and .__original.zip template. Untouched files preserve their original compressed bytes.");
            Done(outDir, "LZX ZIP extracted and verified OK.");
            return;
        }
        if (await Task.Run(() => MotorsportCmsArchive.HasCmsEntries(zipPath)))
        {
            for (int number = 1; Directory.Exists(outDir) || File.Exists(outDir); number++)
                outDir = Path.Combine(OutputDirFor(zipPath), Path.GetFileNameWithoutExtension(zipPath) + $".extracted.{number}");
            var cms = await Task.Run(() => MotorsportCmsArchive.Extract(zipPath, outDir));
            foreach (var asset in cms.Assets) _assetTemplates[Path.GetFullPath(asset.PlaintextPath)] = asset.TemplatePath;
            Log($"Extracted [Forza Motorsport CMS / gzip CRC checked, no MAC] {cms.EntryCount:n0} entries -> {outDir}");
            Log("    Edit the .decrypted files, then drop them onto Crypto for Re-encrypt. The encrypted originals beside them are templates.");
            Log("    Inspection/export only: archive repacking and snapshot/cache-manifest checksums are not updated. No game files were installed.");
            Done(outDir, "CMS entries decrypted and extracted OK.");
            return;
        }
        for (int number = 1; Directory.Exists(outDir) || File.Exists(outDir); number++)
            outDir = Path.Combine(OutputDirFor(zipPath), Path.GetFileNameWithoutExtension(zipPath) + $".extracted.{number}");
        Log($"Extracting FH6 ZIP [{key.Usage}] {Path.GetFileName(zipPath)} …");
        var result = await Task.Run(() => Fh6ZipArchive.Extract(zipPath, outDir, key.DataKey, key.MacKey));
        Log($"    {result.EntryCount:n0} entries, {result.OutputBytes:n0} bytes -> {outDir}");
        Log($"    Edit the extracted files, drop the whole folder or {Fh6ZipArchive.ManifestName}, then click Re-encrypt. Keep .__original.zip; encrypted entries have authenticated headers and chunk MACs.");
        Done(outDir, "FH6 ZIP extracted and verified OK.");
    }

    private async Task RepackFh6ZipFlow(string workspace)
    {
        var key = DetectedFh6Key();
        Status("Re-encrypting and verifying FH6 ZIP…");
        var result = await Task.Run(() => Fh6ZipArchive.Repack(workspace, key.DataKey, key.MacKey));
        string outPath = UniqueOutputPath(Path.Combine(OutputDirFor(workspace), Path.GetFileNameWithoutExtension(result.OriginalName) + ".modded.zip"));
        await Task.Run(() => WriteNewAsset(outPath, result.Archive));
        Log($"Rebuilt [FH6 ZIP / all lengths, CRCs and encrypted MACs verified] {result.EntryCount:n0} entries, {result.ChangedEntries:n0} changed -> {outPath}");
        if (result.ChangedEntries == 0) Log("    Unchanged workspace: output is byte-for-byte identical to the original ZIP.");
        Log("    Entry formats and untouched payloads preserved. Original archive and prior outputs were not overwritten; no game files installed.");
        Done(outPath, "FH6 ZIP rebuilt and verified OK.");
    }

    private async Task RepackLzxZipFlow(string workspace)
    {
        Status("Repacking and verifying LZX ZIP…");
        var result = await Task.Run(() => MotorsportLzxArchive.Repack(workspace));
        string outPath = UniqueOutputPath(Path.Combine(OutputDirFor(workspace), Path.GetFileNameWithoutExtension(result.OriginalName) + ".modded.zip"));
        await Task.Run(() => WriteNewAsset(outPath, result.Archive));
        Log($"Repacked [Forza LZX ZIP / all entries and CRCs verified] {result.EntryCount:n0} entries, {result.ChangedEntries:n0} changed -> {outPath}");
        if (result.ChangedEntries == 0) Log("    Unchanged workspace: output is byte-for-byte identical to the original ZIP.");
        Log("    Original archive and previous outputs were not overwritten. No game files were installed.");
        Done(outPath, "LZX ZIP repacked and verified OK.");
    }

    private static void WriteNewAsset(string output, byte[] bytes)
    {
        string partial = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool owned = false;
        try
        {
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(partial, output); // Never overwrite an original or previous output.
        }
        finally { if (owned && File.Exists(partial)) File.Delete(partial); }
    }

    // Decrypt an FH6 AES or FM TransformIT General asset (PhysicsSettings.ini, etc.)
    // to its editable plaintext. Works for any file extension — routing is by structure.
    private async Task DecryptAssetFlow(string srcPath)
    {
        var key = DetectedFh6Key();
        Status($"Decrypting {Path.GetFileName(srcPath)}…");
        string stem = Path.GetFileNameWithoutExtension(srcPath);
        string ext = Path.GetExtension(srcPath);                  // preserved so re-encrypt keeps the original type
        string outPath = UniqueOutputPath(Path.Combine(OutputDirFor(srcPath), stem + ".decrypted" + ext));
        var result = await Task.Run(() =>
        {
            byte[] encrypted = File.ReadAllBytes(srcPath);
            bool cms = MotorsportCmsContainer.HasPrefix(encrypted);
            bool fm = !cms && MotorsportAssetContainer.HasFraming(encrypted.Length);
            bool cache = fm && MotorsportCmsCacheContainer.HasHeaderAuthentication(encrypted);
            byte[] padded = cms ? MotorsportCmsContainer.Decrypt(encrypted)
                : cache ? MotorsportCmsCacheContainer.Decrypt(encrypted)
                : fm ? MotorsportAssetContainer.Decrypt(encrypted) : ForzaZip.DecryptContainer(encrypted, key.DataKey);
            int length = padded.Length;
            while (!cms && length > 0 && padded[length - 1] == 0) length--;
            byte[] plaintext = padded[..length];
            int controls = plaintext.Count(b => b < 0x09 || (b > 0x0D && b < 0x20));
            if (plaintext.Length == 0 || controls > Math.Max(2, plaintext.Length / 100))
                throw new InvalidDataException("Decryption did not produce plausible text. Check the selected key or game version.");
            WriteNewAsset(outPath, plaintext);
            return (fm, encrypted.Length, plaintext.Length, cms, cache);
        });
        _templateIni = srcPath;
        _assetTemplates[Path.GetFullPath(outPath)] = srcPath;
        Log($"Decrypted [{(result.cms ? "Forza Motorsport CMS / gzip CRC checked, no MAC" : result.cache ? "Forza Motorsport CMS cache / authenticated" : result.fm ? "Forza Motorsport General / authenticated" : key.Usage)}] {Path.GetFileName(srcPath)} -> {Path.GetFileName(outPath)}");
        Log($"    {result.Item2:n0} -> {result.Item3:n0} bytes (container padding removed)");
        if (result.cms) Log("    CMS deployment also needs matching archive and snapshot-manifest sizes/checksums. This output alone is not a verified in-game override.");
        Done(outPath, "Asset decrypted OK.");
    }

    // Re-encrypt an edited text asset back into its container, using the original encrypted file
    // as the header/IV/nonce template. Per-slot MACs are recomputed and the length field is fixed
    // by ForzaZip.EncryptContainer (macKey required).
    private async Task EncryptAssetFlow(string plainPath)
    {
        var key = DetectedFh6Key();
        string? template = ResolveAssetTemplate(plainPath);
        if (template is null)
        {
            Log("Re-encrypt needs the original encrypted asset as a template.");
            Status("No encrypted template set.");
            return;
        }
        Status($"Re-encrypting {Path.GetFileName(plainPath)}…");
        string stem = Path.GetFileNameWithoutExtension(plainPath);
        if (stem.EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase)) stem = stem[..^10];
        string ext = Path.GetExtension(plainPath);
        string outPath = UniqueOutputPath(Path.Combine(OutputDirFor(plainPath), stem + ".modded" + ext));
        var result = await Task.Run(() =>
        {
            byte[] plaintext = File.ReadAllBytes(plainPath);
            byte[] original = File.ReadAllBytes(template);
            bool cms = MotorsportCmsContainer.HasPrefix(original);
            bool fm = !cms && MotorsportAssetContainer.HasFraming(original.Length);
            bool cache = fm && MotorsportCmsCacheContainer.HasHeaderAuthentication(original);
            byte[] encrypted = cms ? MotorsportCmsContainer.Encrypt(plaintext, original)
                : cache ? MotorsportCmsCacheContainer.Encrypt(plaintext, original)
                : fm ? MotorsportAssetContainer.Encrypt(plaintext, original)
                : ForzaZip.EncryptContainer(plaintext, original, key.DataKey, key.MacKey);
            WriteNewAsset(outPath, encrypted);
            return (fm, plaintext.Length, encrypted.Length, cms, cache);
        });
        Log($"Re-encrypted [{(result.cms ? "Forza Motorsport CMS / round-trip verified, no MAC" : result.cache ? "Forza Motorsport CMS cache / authenticated and verified" : result.fm ? "Forza Motorsport General / verified" : key.Usage)}] {Path.GetFileName(plainPath)} -> {Path.GetFileName(outPath)}");
        Log($"    template: {Path.GetFileName(template)}; {result.Item2:n0} -> {result.Item3:n0} bytes");
        if (result.cms) Log("    Repacking CMS and updating its snapshot-manifest checksums is a separate step. Do not replace a General-format INI/JSON with this CMS entry.");
        Done(outPath, "Asset re-encrypted OK.");
    }

    private string? ResolveMergeBase() =>
        _pendingInput is not null && (IsSqlite(_pendingInput) || GameDbSqliteBridge.IsSlt(_pendingInput))
            ? _pendingInput : _lastDecryptedSqlite;

    private static void RequireMergeInput(string path)
    {
        if (!File.Exists(path) || (!IsSqlite(path) && !GameDbSqliteBridge.IsSlt(path)))
            throw new InvalidOperationException("Merge needs a GameDB .slt or decrypted SQLite donor.");
    }

    private async Task MergeFlow(string overlayPath)
    {
        string? basePath = ResolveMergeBase();
        if (basePath is null || !File.Exists(basePath))
            throw new InvalidOperationException("Drop a base .slt or decrypted .sqlite onto the top Crypto zone first.");
        RequireMergeInput(overlayPath);
        if (string.Equals(Path.GetFullPath(basePath), Path.GetFullPath(overlayPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Base and donor must be different files.");

        Status("Preparing databases for merge...");
        using var baseDb = await Task.Run(() => GameDbSqliteBridge.Materialize(basePath));
        using var donorDb = await Task.Run(() => GameDbSqliteBridge.Materialize(overlayPath));
        Status($"Comparing {Path.GetFileName(overlayPath)}...");
        Log($"Previewing donor {Path.GetFileName(overlayPath)} against {Path.GetFileName(basePath)}...");
        var preview = await Task.Run(() => Merge.PreviewMods(baseDb.SqlitePath, donorDb.SqlitePath));
        foreach (string warning in preview.Warnings) Log("    Skipped: " + warning);
        if (preview.Tables.Count == 0)
        {
            Log("No selectable row differences found. Check any skipped-table warnings below.");
            Status("No selectable changes.");
            return;
        }
        var picker = new MergeSelectionWindow(preview, basePath, overlayPath) { Owner = this };
        if (picker.ShowDialog() != true) { Status("Merge cancelled."); return; }

        bool sltOutput = GameDbSqliteBridge.IsSlt(basePath);
        string outPath = UniqueMergeOutput(basePath, sltOutput ? ".slt" : ".sqlite");
        string sqliteOutput = sltOutput
            ? Path.Combine(FH6LocalCryptoTool.TempWorkspace.Root, "fh6_mod_studio_slt_merge_" + Guid.NewGuid().ToString("N") + ".sqlite")
            : outPath;
        using var tempOutput = sltOutput
            ? new GameDbSqliteBridge.MaterializedDatabase(sqliteOutput, null, true) : null;
        baseDb.VerifySourceUnchanged();
        donorDb.VerifySourceUnchanged();
        Status($"Merging {picker.SelectedRows.Count:n0} selected donor rows and " +
               $"{picker.ReplacementTables.Count:n0} whole-table rebuild(s)...");
        Log($"Importing {picker.SelectedRows.Count:n0} selected rows and " +
            $"{picker.ReplacementTables.Count:n0} whole-table rebuild(s) into a new database...");
        await Task.Run(() => Merge.RunSelected(preview, picker.SelectedRows, picker.ReplacementTables, sqliteOutput,
            msg => Dispatcher.Invoke(() => Log("    " + msg))));
        if (sltOutput)
        {
            baseDb.VerifySourceUnchanged();
            Status("Encrypting merged GameDB...");
            await Task.Run(() => GameDbSqliteBridge.EncryptSnapshot(sqliteOutput, basePath, outPath));
        }

        _lastDecryptedSqlite = sltOutput ? null : outPath;
        _pendingInput = outPath;
        if (sltOutput) { _templateSlt = outPath; TemplateText.Text = outPath; }
        StagedText.Text = sltOutput
            ? $"Staged: {Path.GetFileName(outPath)}   (.slt — ready for another merge)"
            : $"Staged: {Path.GetFileName(outPath)}   (.sqlite → Re-encrypt)";
        StagedText.Visibility = Visibility.Visible;
        Log($"    -> {Path.GetFileName(outPath)} (staged for another merge{(sltOutput ? "" : " or Re-encrypt")})");
        Done(outPath, "Merge complete.");
    }

    private async Task GameUpdateMergeFlow(string moddedPath)
    {
        string? updatedPath = ResolveMergeBase();
        if (updatedPath is null || !File.Exists(updatedPath))
            throw new InvalidOperationException("Drop the clean updated GameDB .slt or .sqlite onto the top Crypto zone first.");
        RequireMergeInput(moddedPath);
        if (string.Equals(Path.GetFullPath(moddedPath), Path.GetFullPath(updatedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Your modded DB and clean updated DB must be different files.");
        Status("Preparing clean updated and old modded databases...");
        using var updatedDb = await Task.Run(() => GameDbSqliteBridge.Materialize(updatedPath));
        using var moddedDb = await Task.Run(() => GameDbSqliteBridge.Materialize(moddedPath));
        Status("Matching the old modded DB to a versioned clean reference...");
        string? baselinePath;
        try { baselinePath = await Task.Run(() => StockDatabaseCatalog.Find(moddedDb.SqlitePath)); }
        catch (InvalidDataException ex) { Log(ex.Message); baselinePath = null; }
        using var externalBaseline = baselinePath is null ? await SelectUpdateBaseline(moddedDb.SqlitePath) : null;
        if (baselinePath is null && externalBaseline is null) { Status("Update merge cancelled: a matching clean baseline is required."); return; }
        baselinePath ??= externalBaseline!.SqlitePath;
        Status("Comparing additions, changes and deletions against the old clean DB...");
        var preview = await Task.Run(() => UpdateDbMerge.Preview(updatedDb.SqlitePath, moddedDb.SqlitePath, baselinePath));
        var picker = new UpdateDbMergeWindow(preview, updatedPath, moddedPath) { Owner = this };
        if (picker.ShowDialog() != true) { Status("Update merge cancelled."); return; }

        bool sltOutput = GameDbSqliteBridge.IsSlt(updatedPath);
        string stem = Path.GetFileNameWithoutExtension(updatedPath) + ".updatedmerge." +
                      DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string outPath = UniqueOutputPath(Path.Combine(OutputDirFor(updatedPath), stem + (sltOutput ? ".slt" : ".sqlite")));
        string sqliteOutput = sltOutput
            ? Path.Combine(FH6LocalCryptoTool.TempWorkspace.Root, "fh6_mod_studio_slt_update_" + Guid.NewGuid().ToString("N") + ".sqlite")
            : outPath;
        using var tempOutput = sltOutput
            ? new GameDbSqliteBridge.MaterializedDatabase(sqliteOutput, null, true) : null;
        updatedDb.VerifySourceUnchanged();
        moddedDb.VerifySourceUnchanged();
        externalBaseline?.VerifySourceUnchanged();
        Status("Applying modded changes and deliberate deletions onto the clean update...");
        await Task.Run(() => UpdateDbMerge.Run(preview, sqliteOutput,
            msg => Dispatcher.Invoke(() => Log("    " + msg))));
        if (sltOutput)
        {
            updatedDb.VerifySourceUnchanged();
            Status("Encrypting the updated GameDB...");
            await Task.Run(() => GameDbSqliteBridge.EncryptSnapshot(sqliteOutput, updatedPath, outPath));
        }
        _lastDecryptedSqlite = sltOutput ? null : outPath;
        _pendingInput = outPath;
        if (sltOutput) { _templateSlt = outPath; TemplateText.Text = outPath; }
        StagedText.Text = $"Staged: {Path.GetFileName(outPath)}";
        StagedText.Visibility = Visibility.Visible;
        externalBaseline?.VerifySourceUnchanged();
        Log($"Updated DB merged with {Path.GetFileName(moddedPath)} -> {Path.GetFileName(outPath)}. " +
            $"Clean baseline stamp {preview.BaselineVersion}; {preview.DeletedRows:n0} deletions carried across.");
        Done(outPath, "Updated DB merge complete.");
    }

    private async Task<GameDbSqliteBridge.MaterializedDatabase?> SelectUpdateBaseline(string moddedSqlite)
    {
        string stamp = StockDatabaseCatalog.Version(moddedSqlite);
        MessageBox.Show(this, $"No unique embedded clean reference matches database stamp {stamp}.\n\n" +
            "Select an UNMODIFIED full GameDB .slt or .sqlite from the same game version as your OLD modded DB. " +
            "Do not select your modded DB or a car export. This reference is used to identify deliberate deletions.",
            "Old clean GameDB required", MessageBoxButton.OK, MessageBoxImage.Information);
        var dialog = new OpenFileDialog { Title = $"Old CLEAN GameDB — stamp {stamp}",
            Filter = "GameDB (*.slt;*.sqlite)|*.slt;*.sqlite|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return null;
        return await Task.Run(() => GameDbSqliteBridge.Materialize(dialog.FileName));
    }

    private async Task WidebodyMergeFlow(string donorPath)
    {
        string? basePath = ResolveMergeBase();
        if (basePath is null || !File.Exists(basePath))
            throw new InvalidOperationException("Drop a base .slt or decrypted .sqlite onto the top Crypto zone first.");
        RequireMergeInput(donorPath);
        if (string.Equals(Path.GetFullPath(basePath), Path.GetFullPath(donorPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Base and donor must be different files.");

        Status("Preparing databases for widebody import...");
        using var baseDb = await Task.Run(() => GameDbSqliteBridge.Materialize(basePath));
        using var donorDb = await Task.Run(() => GameDbSqliteBridge.Materialize(donorPath));
        Status("Finding donor widebody kits and stock-body options...");
        var preview = await Task.Run(() => Merge.PreviewWidebodyCars(baseDb.SqlitePath, donorDb.SqlitePath));
        if (preview.Cars.Count == 0)
        {
            Log("No donor cars with widebody kits or new stock-body options were found in the loaded base DB.");
            Status("No new donor body options.");
            return;
        }
        var picker = new WidebodyMergeWindow(preview, basePath, donorPath) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedCarId is not long carId)
        { Status("Widebody import cancelled."); return; }

        bool sltOutput = GameDbSqliteBridge.IsSlt(basePath);
        string dir = OutputDirFor(basePath);
        string stem = Path.GetFileNameWithoutExtension(basePath) + ".widebodymerge." +
                      carId + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string outPath = UniqueOutputPath(Path.Combine(dir, stem + (sltOutput ? ".slt" : ".sqlite")));
        string sqliteOutput = sltOutput
            ? Path.Combine(FH6LocalCryptoTool.TempWorkspace.Root, "fh6_mod_studio_slt_widebody_" + Guid.NewGuid().ToString("N") + ".sqlite")
            : outPath;
        using var tempOutput = sltOutput
            ? new GameDbSqliteBridge.MaterializedDatabase(sqliteOutput, null, true) : null;
        baseDb.VerifySourceUnchanged();
        donorDb.VerifySourceUnchanged();
        Status($"Importing car {carId} widebody rows...");
        int rows = await Task.Run(() => Merge.RunWidebodyCar(preview, carId, sqliteOutput,
            picker.ReplaceConflicts,
            msg => Dispatcher.Invoke(() => Log("    " + msg))));
        if (sltOutput)
        {
            baseDb.VerifySourceUnchanged();
            Status("Encrypting widebody-import GameDB...");
            await Task.Run(() => GameDbSqliteBridge.EncryptSnapshot(sqliteOutput, basePath, outPath));
        }
        _lastDecryptedSqlite = sltOutput ? null : outPath;
        _pendingInput = outPath;
        if (sltOutput) { _templateSlt = outPath; TemplateText.Text = outPath; }
        StagedText.Text = sltOutput
            ? $"Staged: {Path.GetFileName(outPath)}   (.slt — ready for another merge)"
            : $"Staged: {Path.GetFileName(outPath)}   (.sqlite → Re-encrypt)";
        StagedText.Visibility = Visibility.Visible;
        Log($"Imported {rows:n0} row(s) for car {carId} into {Path.GetFileName(outPath)}.");
        Done(outPath, "Widebody car import complete.");
    }

    private async Task CarRelatedImportFlow(string donorPath)
    {
        string? basePath = ResolveMergeBase();
        if (basePath is null || !File.Exists(basePath))
            throw new InvalidOperationException("Drop a base GameDB .slt or .sqlite onto the top Crypto zone first.");
        RequireMergeInput(donorPath);
        if (string.Equals(Path.GetFullPath(basePath), Path.GetFullPath(donorPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Base and donor must be different files.");

        Status("Preparing single-car import...");
        using var baseDb = await Task.Run(() => GameDbSqliteBridge.Materialize(basePath));
        using var donorDb = await Task.Run(() => GameDbSqliteBridge.Materialize(donorPath));
        var car = await Task.Run(() => CarRelatedDbExport.ReadSingleCarDonor(donorDb.SqlitePath));
        Status($"Comparing car {car.Id} rows...");
        var preview = await Task.Run(() => Merge.PreviewMods(baseDb.SqlitePath, donorDb.SqlitePath));
        if (preview.Warnings.Count > 0)
            throw new InvalidDataException("Some donor tables could not be compared; import stopped to avoid a partial car: " +
                                           string.Join("; ", preview.Warnings.Take(3)));
        var picker = new CarRelatedImportWindow(car, preview, basePath, donorPath) { Owner = this };
        if (picker.ShowDialog() != true) { Status("Car import cancelled."); return; }
        var selected = preview.Tables.SelectMany(t => t.Rows)
            .Where(r => picker.ReplaceConflicts || !r.IsConflict).ToArray();
        if (selected.Length == 0)
        {
            Log("No new or selected conflicting rows to import; the base already has these rows.");
            Status("No car changes to import.");
            return;
        }

        bool sltOutput = GameDbSqliteBridge.IsSlt(basePath);
        string stem = Path.GetFileNameWithoutExtension(basePath) + ".carmerge." + car.Id + "." +
                      DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string outPath = UniqueOutputPath(Path.Combine(OutputDirFor(basePath), stem + (sltOutput ? ".slt" : ".sqlite")));
        string sqliteOutput = sltOutput
            ? Path.Combine(FH6LocalCryptoTool.TempWorkspace.Root, "fh6_mod_studio_slt_carmerge_" + Guid.NewGuid().ToString("N") + ".sqlite")
            : outPath;
        using var tempOutput = sltOutput
            ? new GameDbSqliteBridge.MaterializedDatabase(sqliteOutput, null, true) : null;
        baseDb.VerifySourceUnchanged();
        donorDb.VerifySourceUnchanged();
        Status($"Importing {selected.Length:n0} rows for car {car.Id}...");
        int written = await Task.Run(() => Merge.RunSelected(preview, selected, sqliteOutput,
            msg => Dispatcher.Invoke(() => Log("    " + msg))));
        if (sltOutput)
        {
            baseDb.VerifySourceUnchanged();
            Status("Encrypting imported GameDB...");
            await Task.Run(() => GameDbSqliteBridge.EncryptSnapshot(sqliteOutput, basePath, outPath));
        }
        _lastDecryptedSqlite = sltOutput ? null : outPath;
        _pendingInput = outPath;
        if (sltOutput) { _templateSlt = outPath; TemplateText.Text = outPath; }
        StagedText.Text = $"Staged: {Path.GetFileName(outPath)}";
        StagedText.Visibility = Visibility.Visible;
        Log($"Imported {written:n0} row(s) for car {car.Id} into {Path.GetFileName(outPath)}.");
        Done(outPath, "Car DB import complete.");
    }

    private string UniqueMergeOutput(string baseDb, string extension)
    {
        string dir = OutputDirFor(baseDb);
        string stem = Path.GetFileNameWithoutExtension(baseDb) + ".modmerge." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string path = Path.Combine(dir, stem + extension);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, stem + $"-{n}" + extension);
        return path;
    }

    // ---------- helpers ----------
    private string? ResolveTemplate(string sqlitePath)
    {
        if (_templateSlt is not null && File.Exists(_templateSlt)) return _templateSlt;

        string dir = Path.GetDirectoryName(sqlitePath) ?? ".";
        var slts = Directory.GetFiles(dir, "*.slt");
        if (slts.Length == 1) { _templateSlt = slts[0]; TemplateText.Text = slts[0]; return slts[0]; }

        var dlg = new OpenFileDialog { Title = "Select the original GameDB .slt template (same game/build)", Filter = "Forza GameDB container (*.slt)|*.slt|All files|*.*" };
        if (dlg.ShowDialog() == true) { _templateSlt = dlg.FileName; TemplateText.Text = dlg.FileName; return dlg.FileName; }
        return null;
    }

    private string? ResolveAssetTemplate(string plainPath)
    {
        if (_assetTemplates.TryGetValue(Path.GetFullPath(plainPath), out string? saved) && File.Exists(saved)) return saved;
        string dir = Path.GetDirectoryName(plainPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(plainPath);
        if (stem.EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase)) stem = stem[..^10];
        string ext = Path.GetExtension(plainPath);
        if (_templateIni is not null && File.Exists(_templateIni) &&
            Path.GetFileName(_templateIni).Equals(stem + ext, StringComparison.OrdinalIgnoreCase)) return _templateIni;
        string candidate = Path.Combine(dir, stem + ext);
        if (File.Exists(candidate) && !IsTextIni(candidate)) return _templateIni = candidate;
        var dlg = new OpenFileDialog { Title = "Select the original encrypted asset", Filter = "All files|*.*" };
        if (dlg.ShowDialog() == true) return _templateIni = dlg.FileName;
        return null;
    }

    private void BrowseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select the original GameDB .slt template (same game/build)", Filter = "Forza GameDB container (*.slt)|*.slt|All files|*.*" };
        if (dlg.ShowDialog() == true)
        {
            _templateSlt = dlg.FileName;
            TemplateText.Text = dlg.FileName;
            Log($"Template set: {dlg.FileName}");
        }
    }

    private static string? BrowseProfile(string title)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = "FH6 ProfileData (C_ProfileData*)|C_ProfileData*|All files|*.*" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void BrowseDonorSave_Click(object sender, RoutedEventArgs e)
    {
        string? path = BrowseProfile("Select the donor C_ProfileData save");
        if (path is not null) DonorSaveText.Text = path;
    }

    private void BrowseTargetSave_Click(object sender, RoutedEventArgs e)
    {
        string? path = BrowseProfile("Select your target C_ProfileData save");
        if (path is not null) TargetSaveText.Text = path;
    }

    private void SwapSave_Click(object sender, RoutedEventArgs e)
    {
        string donor = DonorSaveText.Text.Trim();
        string target = TargetSaveText.Text.Trim();
        try
        {
            if (!File.Exists(donor)) throw new FileNotFoundException("Choose a valid donor C_ProfileData file.");
            if (!File.Exists(target)) throw new FileNotFoundException("Choose a valid target C_ProfileData file.");
            if (string.Equals(Path.GetFullPath(donor), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Donor and target must be different files.");

            SwapStatusText.Text = "Decrypting and validating both saves…";
            byte[] swapped = ProfileData.Swap(File.ReadAllBytes(donor), File.ReadAllBytes(target));
            string outputDir = Path.GetDirectoryName(Path.GetFullPath(target))!;
            string outPath = Path.Combine(outputDir, "C_ProfileData.swapped");
            if (File.Exists(outPath))
                outPath = Path.Combine(outputDir, $"C_ProfileData.swapped.{DateTime.Now:yyyyMMdd-HHmmss}");
            File.WriteAllBytes(outPath, swapped);
            SwapStatusText.Text = $"Verified swapped save created with the target account identity retained:\n{outPath}\n\nThe originals were not changed. Back up the original C_ProfileData, then rename this file to C_ProfileData when ready.";
            Log($"Save swap: {Path.GetFileName(donor)} payload + target framing -> {outPath}");
            if (OpenFolderCheck.IsChecked == true) OpenFolder(outPath);
        }
        catch (Exception ex)
        {
            SwapStatusText.Text = "ERROR: " + ex.Message;
            MessageBox.Show(ex.Message, "FH6 Local Mod Tool — Save Swap", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string OutputDirFor(string inputPath)
        => (_outputDir is not null && Directory.Exists(_outputDir))
            ? _outputDir
            : (Path.GetDirectoryName(inputPath) ?? ".");

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose an output folder for decrypted / re-encrypted files" };
        if (_outputDir is not null && Directory.Exists(_outputDir)) dlg.InitialDirectory = _outputDir;
        if (dlg.ShowDialog() == true)
        {
            _outputDir = dlg.FolderName;
            OutputText.Text = _outputDir;
            Log($"Output folder set: {_outputDir}");
            Status("Output folder set.");
        }
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        _outputDir = null;
        OutputText.Text = "(same folder as the dropped file)";
        Log("Output folder cleared — files save next to the dropped file.");
        Status("Output folder cleared.");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logPara.Inlines.Clear();
        Log("Log cleared.");
    }

    private static bool IsSqlite(string path)
    {
        return GameDbSqliteBridge.IsSqliteDatabase(path);
    }

    private static bool IsSkeld(string path) =>
        Path.GetExtension(path).Equals(".skeld", StringComparison.OrdinalIgnoreCase);

    private static bool IsSkeldJson(string path)
    {
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) return false;
        // Route even damaged SKELD-named JSON through the SKELD validator, never
        // through generic asset encryption (which produces a non-SKELD container).
        if (Path.GetFileName(path).Contains(".skeld.", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var stream = File.OpenRead(path);
            using var json = JsonDocument.Parse(stream);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                   json.RootElement.TryGetProperty("Format", out var format) &&
                   format.ValueKind == JsonValueKind.String &&
                   format.GetString() == "FH6-SKELD-BSI-v1";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    // A General-key asset CryptoContainer (PhysicsSettings.ini, AnimResourceConfig, …):
    // 36-byte header + a whole number of 512+16 = 528-byte slots, and NOT a gamedb
    // container. The gamedb exclusion is essential: a gamedbRC.slt whose slot count is a
    // multiple of 11 is also divisible by 528 (e.g. a 121-slot update DB), so without the
    // "% 131088 != 0" guard such a .slt would be mis-detected as a General-key asset and
    // decrypted with the wrong key, producing garbage.
    private static bool IsAssetContainer(string path)
    {
        try
        {
            long length = new FileInfo(path).Length;
            if (length >= 32 && length % 16 == 0)
            {
                using var stream = File.OpenRead(path);
                byte[] prefix = new byte[16];
                stream.ReadExactly(prefix);
                if (MotorsportCmsContainer.HasPrefix(prefix)) return true;
            }
            if (MotorsportAssetContainer.HasFraming(length)) return !IsGamedbContainer(path);
            long p = length - 36;
            return p >= 528 && p % 528 == 0 && p % 131088 != 0;
        }
        catch { return false; }
    }

    // A gamedbRC.slt container: 36-byte header + a whole number of 131072+16 = 131088-byte slots.
    private static bool IsGamedbContainer(string path)
    {
        try
        {
            var kind = GameDbContainerFormat.Detect(path);
            return kind is GameDbContainerFormat.Kind.Fh6Aes36 or
                GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32;
        }
        catch { return false; }
    }

    private static GameDbContainerFormat.Kind TryDetectGameDbFormat(string path)
    {
        try { return GameDbContainerFormat.Detect(path); }
        catch { return GameDbContainerFormat.Kind.Unknown; }
    }

    private static bool IsTextIni(string path)
    {
        try
        {
            byte[] sample = File.ReadAllBytes(path).Take(4096).ToArray();
            if (sample.Length == 0 || sample.Contains((byte)0)) return false;
            int controls = sample.Count(b => b < 0x09 || (b > 0x0D && b < 0x20));
            return controls <= Math.Max(1, sample.Length / 100);
        }
        catch { return false; }
    }

    private void Done(string outPath, string status)
    {
        Status(status);
        if (OpenFolderCheck.IsChecked == true) OpenFolder(outPath);
    }

    private static void OpenFolder(string filePath)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void Fail(Exception ex, string path)
    {
        Log($"ERROR on {Path.GetFileName(path)}: {ex.Message}");
        Status("Error — see log.");
        MessageBox.Show(ex.Message, "FH6 Local Mod Tool", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void Log(string msg)
    {
        _logPara.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = TimeBrush });
        _logPara.Inlines.Add(new Run(msg) { Foreground = TextBrush });
        _logPara.Inlines.Add(new LineBreak());
        LogBox.ScrollToEnd();
    }

    private void Status(string msg) => StatusText.Text = msg;

    private void SetCryptoBusy(bool busy)
    {
        _cryptoBusy = busy;
        CryptoWorkspace.IsEnabled = !busy;
        CryptoBusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}

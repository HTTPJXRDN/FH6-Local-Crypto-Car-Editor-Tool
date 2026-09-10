using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private string? _lastDecryptedSqlite;  // base DB for merges
    private string? _outputDir;            // if set, all outputs go here instead of next to the input

    private string? _pendingInput;         // file dropped on the main zone, waiting for Decrypt/Re-encrypt
    private string? _pendingOverlay;       // file dropped on the merge zone, waiting for Merge

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

        KeyCombo.ItemsSource = Fh6Keys.Keys.Select(k => k.Usage).ToArray();
        KeyCombo.SelectedItem = "GameDB";
        Log("Ready. Drop a file onto a zone, then click Decrypt, Re-encrypt, or Merge.");
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

    private Fh6Keys.MethodKey SelectedKey()
        => Fh6Keys.Get(KeyCombo.SelectedItem as string ?? "GameDB");

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
        _pendingInput = path;
        bool zip = string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);
        bool sqlite = IsSqlite(path);
        bool plainText = !zip && !sqlite && IsTextIni(path);                      // decrypted text asset → re-encrypt
        bool asset = !zip && !sqlite && !plainText && IsAssetContainer(path);      // encrypted General asset → decrypt
        // Asset/zip/plain-text use the General key; everything else (a gamedb .slt, a
        // decrypted gamedb .sqlite, or an unknown container) uses GameDB. Setting GameDB
        // explicitly here — rather than leaving whatever was selected before — stops a
        // prior .ini/.zip General selection from carrying over onto a .slt and corrupting
        // the decrypt.
        KeyCombo.SelectedItem = (zip || asset || plainText) ? "General" : "GameDB";
        string what = zip ? ".zip → decrypt & extract"
                    : plainText ? "decrypted text → Re-encrypt"
                    : sqlite ? ".sqlite → Re-encrypt"
                    : asset ? "encrypted asset → Decrypt"
                    : ".slt → Decrypt";
        StagedText.Text = $"Staged: {Path.GetFileName(path)}   ({what})";
        StagedText.Visibility = Visibility.Visible;
        Log($"Staged {Path.GetFileName(path)} — click {((sqlite || plainText) ? "Re-encrypt" : "Decrypt")} to run.{((zip || asset || plainText) ? " General key selected automatically." : "")}");
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
    private void Decrypt_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingInput is null)
        {
            Log("Nothing staged — drop a gamedbRC.slt onto the drop zone first.");
            Status("Nothing to decrypt.");
            return;
        }
        if (IsSqlite(_pendingInput))
        {
            Log("Staged file is a .sqlite — use Re-encrypt, not Decrypt.");
            Status("Wrong action for a .sqlite.");
            return;
        }
        if (IsTextIni(_pendingInput))
        {
            Log("Staged file is already plain text — use Re-encrypt.");
            Status("Wrong action for a decrypted file.");
            return;
        }
        try
        {
            string ext = Path.GetExtension(_pendingInput);
            if (string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
                ExtractZipFlow(_pendingInput);
            else if (string.Equals(ext, ".slt", StringComparison.OrdinalIgnoreCase))
                DecryptFlow(_pendingInput);                                   // gamedb container (explicit)
            else if (string.Equals(ext, ".ini", StringComparison.OrdinalIgnoreCase))
                DecryptAssetFlow(_pendingInput);                             // asset container (explicit)
            // otherwise decide by structure so any extension works (AnimResourceConfig, etc.)
            else if (IsAssetContainer(_pendingInput) && !IsGamedbContainer(_pendingInput))
                DecryptAssetFlow(_pendingInput);
            else if (IsGamedbContainer(_pendingInput))
                DecryptFlow(_pendingInput);
            else if (IsAssetContainer(_pendingInput))
                DecryptAssetFlow(_pendingInput);
            else
                DecryptFlow(_pendingInput);                                   // fall back to gamedb
        }
        catch (Exception ex) { Fail(ex, _pendingInput); }
    }

    private void ReEncrypt_Click(object sender, RoutedEventArgs e)
    {
        // prefer the explicitly staged file; fall back to the last decrypted/merged DB
        string? src = _pendingInput ?? _lastDecryptedSqlite;
        if (src is null)
        {
            Log("Nothing staged — drop a .sqlite onto the drop zone first (or decrypt one).");
            Status("Nothing to re-encrypt.");
            return;
        }
        bool plainText = IsTextIni(src);
        if (!IsSqlite(src) && !plainText)
        {
            Log("Staged file is not a decrypted SQLite or text asset.");
            Status("Wrong action for this file.");
            return;
        }
        try { if (plainText) EncryptAssetFlow(src); else EncryptFlow(src); } catch (Exception ex) { Fail(ex, src); }
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingOverlay is null)
        {
            Log("No overlay staged — drop a .sqlite or .sql onto the merge zone first.");
            Status("Nothing to merge.");
            return;
        }
        try { MergeFlow(_pendingOverlay); } catch (Exception ex) { Fail(ex, _pendingOverlay); }
    }

    // ---------- flows ----------
    private void DecryptFlow(string sltPath)
    {
        var key = SelectedKey();
        byte[] file = File.ReadAllBytes(sltPath);
        Status($"Decrypting {Path.GetFileName(sltPath)}…");
        byte[] sqlite = GameDb.Decrypt(file, key.DataKey);

        string outPath = Path.Combine(
            OutputDirFor(sltPath),
            Path.GetFileNameWithoutExtension(sltPath) + ".decrypted.sqlite");
        File.WriteAllBytes(outPath, sqlite);

        _templateSlt = sltPath;
        _lastDecryptedSqlite = outPath;
        TemplateText.Text = sltPath;

        bool ok = GameDb.LooksLikeSqlite(sqlite);
        Log($"Decrypted [{key.Usage}]  {Path.GetFileName(sltPath)}  ->  {Path.GetFileName(outPath)}");
        Log($"    {file.Length:n0} -> {sqlite.Length:n0} bytes.  SQLite header: {(ok ? "OK" : "NOT FOUND — wrong key/version?")}");
        Log($"    template set to this .slt (used when you re-encrypt).");
        Done(outPath, ok ? "Decrypted OK." : "Decrypted, but no SQLite header (check key/version).");
    }

    private void EncryptFlow(string sqlitePath)
    {
        var key = SelectedKey();
        string? template = ResolveTemplate(sqlitePath);
        if (template is null)
        {
            Log("Re-encrypt needs the original .slt as a template. Pick it with Browse… or decrypt one first.");
            Status("No template .slt set.");
            return;
        }

        byte[] sqlite = File.ReadAllBytes(sqlitePath);
        byte[] original = File.ReadAllBytes(template);
        Status($"Re-encrypting {Path.GetFileName(sqlitePath)}…");
        byte[] outBuf = GameDb.Encrypt(sqlite, original, key.DataKey);

        string stem = Path.GetFileNameWithoutExtension(sqlitePath);
        if (stem.EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase)) stem = stem[..^10];
        string outPath = Path.Combine(
            OutputDirFor(sqlitePath),
            stem + ".re-encrypted.slt");
        File.WriteAllBytes(outPath, outBuf);

        Log($"Re-encrypted [{key.Usage}]  {Path.GetFileName(sqlitePath)}  ->  {Path.GetFileName(outPath)}");
        Log($"    template: {Path.GetFileName(template)}");
        Log($"    {sqlite.Length:n0} -> {outBuf.Length:n0} bytes.  Copy it into the game as gamedbRC.slt.");
        Done(outPath, "Re-encrypted OK.");
    }

    private void ExtractZipFlow(string zipPath)
    {
        var key = SelectedKey();
        string outDir = Path.Combine(OutputDirFor(zipPath), Path.GetFileNameWithoutExtension(zipPath) + ".extracted");
        Status($"Decrypting and extracting {Path.GetFileName(zipPath)}…");
        Log($"Extracting [{key.Usage}] {Path.GetFileName(zipPath)} …");
        var result = ForzaZip.Extract(zipPath, outDir, key.DataKey, msg => Log("    " + msg));
        Log($"    {result.EntryCount:n0} entries, {result.OutputBytes:n0} bytes -> {outDir}");
        Done(outDir, "ZIP decrypted and extracted OK.");
    }

    // Decrypt a General-key CryptoContainer asset (PhysicsSettings.ini, AnimResourceConfig, …)
    // to its editable plaintext. Works for any file extension — routing is by structure.
    private void DecryptAssetFlow(string srcPath)
    {
        var key = SelectedKey();
        byte[] encrypted = File.ReadAllBytes(srcPath);
        Status($"Decrypting {Path.GetFileName(srcPath)}…");
        byte[] padded = ForzaZip.DecryptContainer(encrypted, key.DataKey);
        int length = padded.Length;
        while (length > 0 && padded[length - 1] == 0) length--;   // strip the container's trailing zero padding
        byte[] plaintext = padded[..length];

        // These assets are text. Reject a likely wrong key instead of writing garbage.
        int controls = plaintext.Count(b => b < 0x09 || (b > 0x0D && b < 0x20));
        if (plaintext.Length == 0 || controls > Math.Max(2, plaintext.Length / 100))
            throw new InvalidDataException("Decryption did not produce plausible text. Check the selected key or game version.");

        string stem = Path.GetFileNameWithoutExtension(srcPath);
        string ext = Path.GetExtension(srcPath);                  // preserved so re-encrypt keeps the original type
        string outPath = Path.Combine(OutputDirFor(srcPath), stem + ".decrypted" + ext);
        File.WriteAllBytes(outPath, plaintext);
        _templateIni = srcPath;
        Log($"Decrypted [{key.Usage}] {Path.GetFileName(srcPath)} -> {Path.GetFileName(outPath)}");
        Log($"    {encrypted.Length:n0} -> {plaintext.Length:n0} bytes (container padding removed)");
        Done(outPath, "Asset decrypted OK.");
    }

    // Re-encrypt an edited text asset back into its container, using the original encrypted file
    // as the header/IV/nonce template. Per-slot MACs are recomputed and the length field is fixed
    // by ForzaZip.EncryptContainer (macKey required).
    private void EncryptAssetFlow(string plainPath)
    {
        var key = SelectedKey();
        string? template = ResolveAssetTemplate(plainPath);
        if (template is null)
        {
            Log("Re-encrypt needs the original encrypted asset as a template.");
            Status("No encrypted template set.");
            return;
        }
        byte[] plaintext = File.ReadAllBytes(plainPath);
        byte[] original = File.ReadAllBytes(template);
        Status($"Re-encrypting {Path.GetFileName(plainPath)}…");
        byte[] encrypted = ForzaZip.EncryptContainer(plaintext, original, key.DataKey, key.MacKey);
        string stem = Path.GetFileNameWithoutExtension(plainPath);
        if (stem.EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase)) stem = stem[..^10];
        string ext = Path.GetExtension(plainPath);
        string outPath = Path.Combine(OutputDirFor(plainPath), stem + ".modded" + ext);
        File.WriteAllBytes(outPath, encrypted);
        Log($"Re-encrypted [{key.Usage}] {Path.GetFileName(plainPath)} -> {Path.GetFileName(outPath)}");
        Log($"    template: {Path.GetFileName(template)}; {plaintext.Length:n0} -> {encrypted.Length:n0} bytes");
        Done(outPath, "Asset re-encrypted OK.");
    }

    private void MergeFlow(string overlayPath)
    {
        // Base = whatever .sqlite is in the top zone: prefer the file staged there,
        // otherwise fall back to the last DB we decrypted/merged.
        string? baseDb = (_pendingInput is not null && IsSqlite(_pendingInput))
            ? _pendingInput
            : _lastDecryptedSqlite;
        if (baseDb is null || !File.Exists(baseDb))
        {
            Log("Merge needs a base DB in the top zone — decrypt a .slt or drop a .sqlite there first, then drop the overlay here.");
            Status("No base DB for merge.");
            return;
        }
        string outPath = Path.Combine(
            OutputDirFor(baseDb),
            Path.GetFileNameWithoutExtension(baseDb) + ".merged.sqlite");

        var mode = AddOnlyCheck.IsChecked == true ? MergeMode.AddOnly : MergeMode.OverlayWins;
        Status($"Merging {Path.GetFileName(overlayPath)}…");
        Log($"Merging {Path.GetFileName(overlayPath)} into {Path.GetFileName(baseDb)} …");
        Merge.Run(baseDb, overlayPath, outPath, tables: null, log: msg => Log("    " + msg), mode: mode);

        _lastDecryptedSqlite = outPath; // chain further merges onto the result
        _pendingInput = outPath;        // stage it so Re-encrypt is ready immediately
        StagedText.Text = $"Staged: {Path.GetFileName(outPath)}   (.sqlite → Re-encrypt)";
        StagedText.Visibility = Visibility.Visible;
        Log($"    -> {Path.GetFileName(outPath)}   (staged — click Re-encrypt, or drop another overlay to keep merging)");
        Done(outPath, "Merge complete.");
    }

    // ---------- helpers ----------
    private string? ResolveTemplate(string sqlitePath)
    {
        if (_templateSlt is not null && File.Exists(_templateSlt)) return _templateSlt;

        string dir = Path.GetDirectoryName(sqlitePath) ?? ".";
        var slts = Directory.GetFiles(dir, "*.slt");
        if (slts.Length == 1) { _templateSlt = slts[0]; TemplateText.Text = slts[0]; return slts[0]; }

        var dlg = new OpenFileDialog { Title = "Select the original gamedbRC.slt template", Filter = "FH6 container (*.slt)|*.slt|All files|*.*" };
        if (dlg.ShowDialog() == true) { _templateSlt = dlg.FileName; TemplateText.Text = dlg.FileName; return dlg.FileName; }
        return null;
    }

    private string? ResolveAssetTemplate(string plainPath)
    {
        if (_templateIni is not null && File.Exists(_templateIni)) return _templateIni;
        string dir = Path.GetDirectoryName(plainPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(plainPath);
        if (stem.EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase)) stem = stem[..^10];
        string ext = Path.GetExtension(plainPath);
        string candidate = Path.Combine(dir, stem + ext);
        if (File.Exists(candidate) && !IsTextIni(candidate)) return _templateIni = candidate;
        var dlg = new OpenFileDialog { Title = "Select the original encrypted asset", Filter = "All files|*.*" };
        if (dlg.ShowDialog() == true) return _templateIni = dlg.FileName;
        return null;
    }

    private void BrowseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select the original gamedbRC.slt template", Filter = "FH6 container (*.slt)|*.slt|All files|*.*" };
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
            MessageBox.Show(ex.Message, "FH6 Local Crypto Tool — Save Swap", MessageBoxButton.OK, MessageBoxImage.Error);
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
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> hdr = stackalloc byte[16];
            int n = fs.Read(hdr);
            return n >= 15 && System.Text.Encoding.ASCII.GetString(hdr[..15].ToArray()) == "SQLite format 3";
        }
        catch { return false; }
    }

    // A General-key asset CryptoContainer (PhysicsSettings.ini, AnimResourceConfig, …):
    // 36-byte header + a whole number of 512+16 = 528-byte slots, and NOT a gamedb
    // container. The gamedb exclusion is essential: a gamedbRC.slt whose slot count is a
    // multiple of 11 is also divisible by 528 (e.g. a 121-slot update DB), so without the
    // "% 131088 != 0" guard such a .slt would be mis-detected as a General-key asset and
    // decrypted with the wrong key, producing garbage.
    private static bool IsAssetContainer(string path)
    {
        try { long p = new FileInfo(path).Length - 36; return p >= 528 && p % 528 == 0 && p % 131088 != 0; }
        catch { return false; }
    }

    // A gamedbRC.slt container: 36-byte header + a whole number of 131072+16 = 131088-byte slots.
    private static bool IsGamedbContainer(string path)
    {
        try { long p = new FileInfo(path).Length - 36; return p >= 131088 && p % 131088 == 0; }
        catch { return false; }
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
        MessageBox.Show(ex.Message, "FH6 Local Crypto Tool", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void Log(string msg)
    {
        _logPara.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = TimeBrush });
        _logPara.Inlines.Add(new Run(msg) { Foreground = TextBrush });
        _logPara.Inlines.Add(new LineBreak());
        LogBox.ScrollToEnd();
    }

    private void Status(string msg) => StatusText.Text = msg;
}

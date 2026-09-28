using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using ClipdScissorTool;

namespace FH6AnimationSwap;

public partial class AnimationSwapView : UserControl
{
    ClipdFile? _target;
    readonly ObservableCollection<Row> _others = new();   // non-door "other" channels (advanced list)
    readonly ObservableCollection<Donor> _donors = new();
    readonly ObservableCollection<Row> _allChannels = new();
    readonly ObservableCollection<Row> _donorChannels = new();
    ClipdFile? _channelDonor;
    int? _stockEnd;                               // stock node-stream-end for this car (null if unknown)
    string? _activePair;                          // "front" | "rear" | null (null = an Other channel is active)
    string? _loadedPath;                          // raw CLIPD path or source car ZIP path
    string? _zipClipdEntryName;                   // null in raw CLIPD mode

    // Door channels grouped into pairs. Rear falls back to same-side FRONT door when a donor lacks rears.
    static readonly (string tgt, string donor, string? fallback)[] FrontMap =
    {
        ("doorlf_open","doorlf_open",null), ("doorlf_close","doorlf_close",null),
        ("doorrf_open","doorrf_open",null), ("doorrf_close","doorrf_close",null),
    };
    static readonly (string tgt, string donor, string? fallback)[] RearMap =
    {
        ("doorlr_open","doorlr_open","doorlf_open"), ("doorlr_close","doorlr_close","doorlf_close"),
        ("doorrr_open","doorrr_open","doorrf_open"), ("doorrr_close","doorrr_close","doorrf_close"),
    };
    static string Hash(string chan) { var p = chan.Split('_'); return ClipdFile.PartHash(p[0], p[1]); }
    static readonly HashSet<string> DoorHashes =
        new(FrontMap.Concat(RearMap).Select(m => Hash(m.tgt)));
    static readonly string[] SuspensionChannelIds =
    {
        "d214e3d7", // suspensionlf
        "b627e3e3", // suspensionlr
        "14022d08", // suspensionrf
        "98e22cf4", // suspensionrr
        "d1f59988", // suspensionturnlf
        "4f6d5098", // suspensionturnrf
    };

    public AnimationSwapView()
    {
        InitializeComponent();
        OtherList.ItemsSource = _others;
        DonorList.ItemsSource = _donors;
        AllChannelList.ItemsSource = _allChannels;
        DonorChannelList.ItemsSource = _donorChannels;
    }

    void Browse_Click(object s, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Car ZIP or CLIPD (*.zip;*.clipd)|*.zip;*.clipd|Car archives (*.zip)|*.zip|Clip data (*.clipd)|*.clipd|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        try { LoadTargetFile(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Load error", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void AnyDrag(object s, DragEventArgs e)
    { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }

    void TargetDrop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] f || f.Length == 0) return;
        try { LoadTargetFile(f[0]); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Load error", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void LoadTargetFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        ClipdFile loaded;
        string? embeddedClipd = null;

        if (string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            LoadedCarZip carZip = CarZipContainer.Load(fullPath);
            loaded = ClipdFile.TryParse(carZip.ClipdBytes)
                ?? throw new InvalidDataException("The embedded CLIPD could not be parsed.");
            loaded.Path = fullPath;
            embeddedClipd = carZip.ClipdEntryName;
        }
        else
        {
            loaded = ClipdFile.Load(fullPath);
        }

        _target = loaded;
        _loadedPath = fullPath;
        _zipClipdEntryName = embeddedClipd;
        _channelDonor = null;
        _donorChannels.Clear();
        DonorChannelName.Text = "No donor loaded";
        TargetName.Text = embeddedClipd == null
            ? Path.GetFileName(fullPath)
            : $"{Path.GetFileName(fullPath)}  →  {embeddedClipd}";
        RefreshChannels();

        // Detect a starting file that was already modified (against the stock baseline).
        string identityPath = embeddedClipd ?? fullPath;
        string? carId = ClipdFile.CarIdFromPath(identityPath);
        _stockEnd = StockBaseline.StockEnd(carId);
        int actual = _target.NodeStreamEnd();
        if (_stockEnd.HasValue && actual != _stockEnd.Value)
        {
            MessageBox.Show(
                $"Heads up — the embedded animation data looks ALREADY MODIFIED.\n\n"
                + $"It is {actual} bytes, but the stock car {carId} is {_stockEnd.Value} bytes. "
                + "For the safest result, start from a clean game archive or CLIPD.\n\n"
                + $"Expected a clean carclips_{carId}.clipd.",
                "Non-stock starting file", MessageBoxButton.OK, MessageBoxImage.Warning);
            Say($"⚠ {Path.GetFileName(fullPath)} looks already modified ({actual} vs stock {_stockEnd.Value}). Start from a clean file.");
        }
        else
        {
            string rear = RearBtn.IsEnabled ? "" : " (no rear doors)";
            string mode = embeddedClipd == null ? "CLIPD" : "car ZIP";
            Say($"Loaded {mode}: {Path.GetFileName(fullPath)}{(_stockEnd.HasValue ? " (stock ✓)" : "")}{rear}. Apply animation swaps or the DB stance fix, then Save As.");
        }
    }

    bool HasChannel(string chan) => _target != null && _target.Channels().Any(c => c.IdHash == Hash(chan));
    Channel? Chan(string chan) => _target?.Channels().FirstOrDefault(c => c.IdHash == Hash(chan));

    void RefreshChannels()
    {
        _others.Clear();
        _allChannels.Clear();
        _donors.Clear();
        _activePair = null;
        FrontBtn.IsChecked = RearBtn.IsChecked = false;
        DonorHeader.Text = "2) Donor animations (pick FRONT or REAR doors first)";
        if (_target == null) return;

        // Front/Rear availability (2-door cars have no rear doors → grey it out).
        FrontBtn.IsEnabled = FrontMap.Any(m => HasChannel(m.tgt));
        RearBtn.IsEnabled  = RearMap.Any(m => HasChannel(m.tgt));
        DbStanceFixBtn.IsEnabled = _target.Channels().Any(c =>
            SuspensionChannelIds.Contains(c.IdHash, StringComparer.OrdinalIgnoreCase));

        // Everything that isn't a door goes in the advanced "Other channels" list.
        foreach (var c in _target.Channels())
        {
            _allChannels.Add(new Row(c));
            if (!DoorHashes.Contains(c.IdHash)) _others.Add(new Row(c));
        }
    }

    void LoadChannelDonor_Click(object s, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Car ZIP or CLIPD (*.zip;*.clipd)|*.zip;*.clipd|Car archives (*.zip)|*.zip|Clip data (*.clipd)|*.clipd"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            ClipdFile donor = Path.GetExtension(dialog.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? ClipdFile.TryParse(CarZipContainer.Load(dialog.FileName).ClipdBytes)
                    ?? throw new InvalidDataException("Donor ZIP contains an invalid CLIPD.")
                : ClipdFile.Load(dialog.FileName);
            _channelDonor = donor;
            _donorChannels.Clear();
            foreach (Channel channel in donor.Channels()) _donorChannels.Add(new Row(channel));
            DonorChannelName.Text = Path.GetFileName(dialog.FileName);
            Say($"Donor loaded: {_donorChannels.Count} animation channels. Select the ones to add, then Save As.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Donor load error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void AddChannels_Click(object s, RoutedEventArgs e)
    {
        if (_target == null || _channelDonor == null) { Say("Load a target and a donor CLIPD first."); return; }
        string[] selected = DonorChannelList.SelectedItems.Cast<Row>().Select(row => row.Id).ToArray();
        if (selected.Length == 0) { Say("Select one or more donor animations to add."); return; }
        try
        {
            int added = EditChannelDictionary(file => file.AddMissingChannelsFrom(_channelDonor, selected));
            Say(added == 0 ? "All selected donor animations already exist; nothing changed."
                : $"Added {added} animation channel(s); {selected.Length - added} already present. Save As to write a new CLIPD or ZIP. Matching skeleton bones and Mojo events may also be needed in-game.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Channel import error", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void RemoveChannels_Click(object s, RoutedEventArgs e)
    {
        if (_target == null) { Say("Load a CLIPD first."); return; }
        string[] selected = AllChannelList.SelectedItems.Cast<Row>().Select(row => row.Id).ToArray();
        if (selected.Length == 0) { Say("Select one or more animations to remove."); return; }
        if (selected.Length >= _target.Channels().Count)
        { Say("Keep at least one animation channel; removing every channel is not supported."); return; }
        if (MessageBox.Show($"Remove {selected.Length} selected animation channel(s) from this CLIPD?\n\nThe original file stays untouched. Reset undoes this before Save As.",
                "Remove animations", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            int removed = EditChannelDictionary(file => file.RemoveChannelsByIdHashes(selected));
            Say($"Removed {removed} animation channel(s). Save As to write a new CLIPD or ZIP. Matching Mojo events may still refer to removed animations.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Channel removal error", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    async void EditEndpoints_Click(object s, RoutedEventArgs e)
    {
        if (_target == null || AllChannelList.SelectedItems.Count != 1 || AllChannelList.SelectedItem is not Row row)
        { Say("Select exactly one animation under This car to adjust its endpoints."); return; }
        try
        {
            DecodedClip decoded = ClipdEndpointCodec.Decode(row.Chan);
            var dialog = new EndpointEditorWindow(row.Label, decoded) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            string id = row.Id;
            byte[] startingBytes = _target.Build();
            EndpointOffset offset = dialog.Offset;
            int trackIndex = dialog.TrackIndex;
            IsEnabled = false;
            Say($"Recompressing {row.Label}…");
            ClipdFile verified = await Task.Run(() =>
            {
                ClipdFile candidate = ClipdFile.TryParse(startingBytes)
                    ?? throw new InvalidDataException("Current CLIPD could not be rebuilt.");
                var before = candidate.Channels().ToDictionary(c => c.IdHash,
                    c => (Desc: c.Desc.Serialize(), Curve: c.Curve.Serialize()), StringComparer.OrdinalIgnoreCase);
                Channel selected = candidate.Channels().Single(c => c.IdHash == id);
                selected.Curve.LeafBytes = ClipdEndpointCodec.RebuildCurve(selected, decoded, trackIndex, offset);
                ClipdFile checkedFile = ClipdFile.TryParse(candidate.Build())
                    ?? throw new InvalidDataException("Edited CLIPD failed structural validation.");
                if (checkedFile.PreambleValue() != checkedFile.Root1ContentLen() - 16 ||
                    checkedFile.DictionaryChannelCount() != checkedFile.Channels().Count)
                    throw new InvalidDataException("Edited CLIPD dictionary metadata is inconsistent.");
                var after = checkedFile.Channels().ToDictionary(c => c.IdHash, StringComparer.OrdinalIgnoreCase);
                foreach (var (oldId, old) in before)
                    if (oldId != id &&
                        (!after[oldId].Desc.Serialize().AsSpan().SequenceEqual(old.Desc) ||
                         !after[oldId].Curve.Serialize().AsSpan().SequenceEqual(old.Curve)))
                        throw new InvalidDataException($"Unselected animation {ClipdFile.Label(oldId)} changed.");
                return checkedFile;
            });
            _target = verified;
            RefreshChannels();
            Say($"Adjusted {row.Label} start/end poses. Other animations are unchanged. Save As, then test the new motion in-game.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Endpoint edit failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            Say("Endpoint edit was not applied; the loaded CLIPD is unchanged.");
        }
        finally { IsEnabled = true; }
    }

    // Apply edits to a clone, then validate the complete dictionary before replacing
    // the in-memory target. This leaves the user's loaded state intact on any failure.
    int EditChannelDictionary(Func<ClipdFile, int> edit)
    {
        if (_target == null) throw new InvalidOperationException("No CLIPD loaded.");
        var originals = _target.Channels().ToDictionary(c => c.IdHash,
            c => (Desc: c.Desc.Serialize(), Curve: c.Curve.Serialize()), StringComparer.OrdinalIgnoreCase);
        ClipdFile candidate = ClipdFile.TryParse(_target.Build())
            ?? throw new InvalidDataException("Current CLIPD could not be rebuilt before editing.");
        int changed = edit(candidate);
        if (changed == 0) return 0;
        ClipdFile verified = ClipdFile.TryParse(candidate.Build())
            ?? throw new InvalidDataException("Edited CLIPD failed structural validation.");
        if (verified.PreambleValue() != verified.Root1ContentLen() - 16 ||
            verified.DictionaryChannelCount() != verified.Channels().Count)
            throw new InvalidDataException("Edited CLIPD dictionary size or channel count is inconsistent.");
        var after = verified.Channels().ToDictionary(c => c.IdHash, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, original) in originals)
            if (after.TryGetValue(id, out Channel? kept) &&
                (!kept.Desc.Serialize().AsSpan().SequenceEqual(original.Desc) ||
                 !kept.Curve.Serialize().AsSpan().SequenceEqual(original.Curve)))
                throw new InvalidDataException($"Unselected animation {ClipdFile.Label(id)} changed unexpectedly.");
        _target = verified;
        RefreshChannels();
        return changed;
    }

    // Remove the suspension channel pairs that make some animated cars ignore DB visual stance.
    void DbStanceFix_Click(object s, RoutedEventArgs e)
    {
        if (_target == null) { Say("Load a car first."); return; }

        int detected = _target.Channels().Count(c =>
            SuspensionChannelIds.Contains(c.IdHash, StringComparer.OrdinalIgnoreCase));
        if (detected == 0)
        {
            Say("This clipd has no supported suspension animation channels to remove.");
            DbStanceFixBtn.IsEnabled = false;
            return;
        }

        var answer = MessageBox.Show(
            $"This compatibility patch removes {detected} suspension animation channel(s).\n\n"
            + "Use it on cars whose visual ride height or wheel spacers do not follow database edits. "
            + "The database handling changes will remain, but animated suspension travel and suspension-driven steering will be disabled.\n\n"
            + "You can use Reset before saving to undo this action. Continue?",
            "Fix DB Ride Height / Spacers",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            int removed = _target.RemoveChannelsByIdHashes(SuspensionChannelIds);
            RefreshChannels();
            Say($"DB stance compatibility patch applied: removed {removed} suspension channel(s). Ride height/spacers can render from DB; suspension rig animation is disabled. Save As to finish.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Compatibility patch error", MessageBoxButton.OK, MessageBoxImage.Warning);
            Say("DB stance compatibility patch failed; no file was saved.");
        }
    }

    // Front / Rear pair selected → show the door donor list (correct-handedness, best scissors first).
    void Pair_Click(object s, RoutedEventArgs e)
    {
        if (_target == null) { Say("Load a car first."); ((ToggleButton)s).IsChecked = false; return; }
        var btn = (ToggleButton)s;
        _activePair = btn.IsChecked == true ? (string)btn.Tag : null;
        if (btn == FrontBtn) RearBtn.IsChecked = false; else FrontBtn.IsChecked = false;
        OtherList.SelectedItem = null;
        _donors.Clear();
        if (_activePair == null) { DonorHeader.Text = "2) Donor animations (pick FRONT or REAR doors first)"; return; }
        foreach (var d in DonorLibrary.ForChannel(Hash("doorlf_open"))) _donors.Add(d);
        DonorHeader.Text = $"2) {_activePair.ToUpper()} door donors ({_donors.Count}) — 'lift' opens upward. Pick a car, then Apply.";
    }

    // An advanced "other" channel selected → show that channel's donors.
    void Other_Changed(object s, SelectionChangedEventArgs e)
    {
        if (OtherList.SelectedItem is not Row row) return;
        _activePair = null;
        FrontBtn.IsChecked = RearBtn.IsChecked = false;
        _donors.Clear();
        foreach (var d in DonorLibrary.ForChannel(row.Chan.IdHash)) _donors.Add(d);
        DonorHeader.Text = $"2) {ClipdFile.Label(row.Chan.IdHash)} donors ({_donors.Count}) — experimental, test direction.";
    }

    void Apply_Click(object s, RoutedEventArgs e)
    {
        if (_target == null) { Say("Load a car first."); return; }
        if (DonorList.SelectedItem is not Donor donor) { Say("Pick a donor car on the right."); return; }

        if (_activePair == "front") ApplyPair(FrontMap, donor, "Front");
        else if (_activePair == "rear") ApplyPair(RearMap, donor, "Rear");
        else if (OtherList.SelectedItem is Row row)
        {
            var d = DonorLibrary.Find(row.Chan.IdHash, donor.CarName) ?? donor;
            ClipdFile.FullSwap(row.Chan, d.Curve, d.IdHash);
            row.Status = $"swapped ← {donor.CarName}"; row.Refresh();
            Say($"Swapped {ClipdFile.Label(row.Chan.IdHash)} from '{donor.CarName}'. Experimental — save and test the direction.");
        }
        else Say("Pick FRONT or REAR doors (or an Other channel) first, then a donor car, then Apply.");
    }

    // Apply a donor car's door animations to a pair, side-aware and open/close-paired. Rear channels
    // fall back to the donor's same-side FRONT door when the donor has no rear doors.
    void ApplyPair((string tgt, string donor, string? fallback)[] map, Donor donor, string label)
    {
        int applied = 0, missing = 0; bool usedFallback = false;
        foreach (var (tgt, donorChan, fallback) in map)
        {
            var targetChan = Chan(tgt);
            if (targetChan == null) continue;                 // car doesn't have this door channel
            var rec = DonorLibrary.Find(Hash(donorChan), donor.CarName);
            string usedHash = Hash(donorChan);
            if (rec == null && fallback != null)              // donor lacks rears → use its front door
            { rec = DonorLibrary.Find(Hash(fallback), donor.CarName); usedHash = Hash(fallback); if (rec != null) usedFallback = true; }
            if (rec == null) { missing++; continue; }
            ClipdFile.FullSwap(targetChan, rec.Curve, usedHash);   // patches embedded id to the target channel
            applied++;
        }
        if (applied == 0) { Say($"{donor.CarName} has no usable animation for the {label.ToLower()} doors."); return; }
        string note = usedFallback ? " (rears used the donor's front-door animation)" : "";
        string miss = missing > 0 ? $" {missing} channel(s) had no donor match." : "";
        Say($"{label} doors ← {donor.CarName}: {applied} channel(s) swapped{note}.{miss} Save and test.");
    }


    void Reset_Click(object s, RoutedEventArgs e)
    {
        if (_loadedPath == null) return;
        try { LoadTargetFile(_loadedPath); Say("Reloaded source file (edits discarded)."); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    void Save_Click(object s, RoutedEventArgs e)
    {
        if (_target == null) { Say("Load a car first."); return; }

        // ---- safety check: build, re-parse, and confirm structure + exact sizes before writing ----
        byte[] output;
        try { output = _target.Build(); }
        catch (Exception ex) { Fail($"Could not build the file: {ex.Message}"); return; }

        // Growth is expected now, so we don't compare to stock size. We verify the output is
        // structurally valid and that the preamble "size trailer" matches (the field that null-cars
        // if left stale) — and that the file didn't overflow its padding.
        var problems = new List<string>();
        var reparsed = ClipdFile.TryParse(output);
        if (reparsed == null)
            problems.Add("output no longer parses as a valid .clipd (structure broken)");
        else
        {
            long pv = reparsed.PreambleValue(), expect = reparsed.Root1ContentLen() - 16;
            if (pv != expect)
                problems.Add($"preamble size trailer is {pv}, expected {expect} (would null-car)");
            if (reparsed.DictionaryChannelCount() != reparsed.Channels().Count)
                problems.Add("dictionary animation count does not match the actual channels");
        }
        // sentinel must sit right after the node stream, inside the padded file
        int nse = _target.NodeStreamEnd();
        if (nse + 4 > output.Length)
            problems.Add("grew past the file allocation");

        if (problems.Count > 0)
        {
            MessageBox.Show(
                "Not saving — the result failed the safety check, so it would likely null-car:\n\n • "
                + string.Join("\n • ", problems)
                + "\n\nTip: Reset and start from a clean stock carclips file.",
                "Integrity check failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            Say("Save blocked: output failed integrity check (see dialog).");
            return;
        }

        bool zipMode = _zipClipdEntryName != null && _loadedPath != null;
        var dlg = new SaveFileDialog
        {
            Filter = zipMode ? "Car archive (*.zip)|*.zip|All files (*.*)|*.*"
                             : "Clip data (*.clipd)|*.clipd|All files (*.*)|*.*",
            FileName = zipMode
                ? Path.GetFileNameWithoutExtension(_loadedPath) + "_patched.zip"
                : Path.GetFileNameWithoutExtension(_loadedPath ?? _target.Path) + "_patched.clipd"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (_loadedPath != null && Path.GetFullPath(dlg.FileName).Equals(Path.GetFullPath(_loadedPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a different output file so the original car animation stays available as a backup.");
            if (zipMode)
            {
                CarZipContainer.SavePatched(_loadedPath!, dlg.FileName, _zipClipdEntryName!, output);
                Say($"Saved ✓ complete patched car ZIP (layout and contents verified): {dlg.FileName}");
            }
            else
            {
                File.WriteAllBytes(dlg.FileName, output);
                Say($"Saved ✓ (integrity verified): {dlg.FileName} — rename to the car's carclips_<ID>.clipd to deploy.");
            }
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    void Fail(string msg) => MessageBox.Show(msg, "Save error", MessageBoxButton.OK, MessageBoxImage.Warning);

    void Say(string s) => Status.Text = s;
}

public sealed class Row : INotifyPropertyChanged
{
    public Channel Chan { get; }
    public Row(Channel c) { Chan = c; }
    public string Id => Chan.IdHash;
    public string Label => ClipdFile.Label(Chan.IdHash);
    public string Sig => Chan.Sig;
    public int Size => Chan.Size;
    string _status = "";
    public string Status { get => _status; set { _status = value; OnPC(nameof(Status)); } }
    public void Refresh() { OnPC(nameof(Size)); OnPC(nameof(Status)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

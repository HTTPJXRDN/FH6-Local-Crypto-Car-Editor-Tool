using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipdScissorTool;
using FH6LocalCryptoTool;

namespace FH6AnimationSwap;

public partial class EndpointEditorWindow : Window
{
    const ulong SpeedBone = 0x5F5447E47D2783AA;
    const ulong TachBone = 0x01310F5909B4F1C3;
    readonly DecodedClip _clip;
    readonly Dictionary<TextBox, string> _initialText = new();
    public int TrackIndex => TrackBox.SelectedIndex;
    public EndpointOffset Offset { get; private set; }

    public EndpointEditorWindow(string channelName, DecodedClip clip)
    {
        _clip = clip;
        InitializeComponent();
        ChannelTitle.Text = $"{channelName}  •  {clip.Samples} frames, {clip.Tracks.Length} track(s)";
        TrackBox.ItemsSource = clip.BoneHashes.Select((hash, index) =>
            $"Track {index + 1} — {(hash == SpeedBone ? "boneSpeed (speed needle)" : hash == TachBone ? "boneTach (tach needle)" : "bone")} 0x{hash:X16}").ToArray();
        int preferred = channelName.Contains("speed", StringComparison.OrdinalIgnoreCase)
            ? Array.IndexOf(clip.BoneHashes, SpeedBone)
            : channelName.Contains("tach", StringComparison.OrdinalIgnoreCase)
                ? Array.IndexOf(clip.BoneHashes, TachBone) : -1;
        TrackBox.SelectedIndex = preferred >= 0 ? preferred : 0;
    }

    void TrackBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_clip is null || TrackIndex < 0 || TrackIndex >= _clip.Tracks.Length) return;
        var track = _clip.Tracks[TrackIndex];
        FillPosition(track[0].Position, StartX, StartY, StartZ);
        FillPosition(track[^1].Position, EndX, EndY, EndZ);
        FillPosition(ClipdEndpointCodec.RotationToDegrees(track[0].Rotation), StartRX, StartRY, StartRZ);
        FillPosition(ClipdEndpointCodec.RotationToDegrees(track[^1].Rotation), EndRX, EndRY, EndRZ);
        WholeTrack.IsChecked = _clip.BoneHashes[TrackIndex] is SpeedBone or TachBone;
    }

    void WholeTrack_Changed(object sender, RoutedEventArgs e)
    {
        bool endpointsEnabled = WholeTrack.IsChecked != true;
        foreach (TextBox box in new[] { EndX, EndY, EndZ, StartRX, StartRY, StartRZ, EndRX, EndRY, EndRZ })
            box.IsEnabled = endpointsEnabled;
    }

    void FillPosition(Vector3 value, TextBox x, TextBox y, TextBox z)
    {
        Fill(x, value.X);
        Fill(y, value.Y);
        Fill(z, value.Z);
    }

    void Fill(TextBox box, float value)
    {
        box.Text = value.ToString("0.######", CultureInfo.CurrentCulture);
        _initialText[box] = box.Text;
    }

    void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TrackIndex < 0) throw new InvalidOperationException("Choose a track first.");
            var track = _clip.Tracks[TrackIndex];
            var initialStart = track[0];
            var initialEnd = track[^1];
            Vector3 start = ReadVector(initialStart.Position, StartX, StartY, StartZ) - initialStart.Position;
            bool wholeTrack = WholeTrack.IsChecked == true;
            Vector3 end = wholeTrack ? start : ReadVector(initialEnd.Position, EndX, EndY, EndZ) - initialEnd.Position;
            Quaternion startRotation = wholeTrack ? Quaternion.Identity : ReadRotation(initialStart.Rotation, StartRX, StartRY, StartRZ);
            Quaternion endRotation = wholeTrack ? Quaternion.Identity : ReadRotation(initialEnd.Rotation, EndRX, EndRY, EndRZ);
            if (start == Vector3.Zero && end == Vector3.Zero &&
                IsIdentity(startRotation) && IsIdentity(endRotation))
                throw new InvalidOperationException("Nothing changed. Edit a start or end value first.");
            Offset = new EndpointOffset(start, end, startRotation, endRotation);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Check endpoint values", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    Vector3 ReadVector(Vector3 original, TextBox x, TextBox y, TextBox z) =>
        new(ReadValue(x, original.X), ReadValue(y, original.Y), ReadValue(z, original.Z));

    float ReadValue(TextBox box, float original) =>
        box.Text == _initialText[box] ? original : Parse(box);

    Quaternion ReadRotation(Quaternion original, TextBox x, TextBox y, TextBox z)
    {
        if (boxUnchanged(x) && boxUnchanged(y) && boxUnchanged(z)) return Quaternion.Identity;
        Vector3 degrees = ReadVector(ClipdEndpointCodec.RotationToDegrees(original), x, y, z);
        Quaternion desired = ClipdEndpointCodec.RotationFromDegrees(degrees);
        return Quaternion.Normalize(desired * Quaternion.Inverse(original));

        bool boxUnchanged(TextBox box) => box.Text == _initialText[box];
    }

    static bool IsIdentity(Quaternion value) => MathF.Abs(Quaternion.Dot(value, Quaternion.Identity)) > 0.9999999f;

    static float Parse(TextBox box)
    {
        if (!NumericText.TryParseFloat(box.Text, out float value))
            throw new FormatException($"Enter a valid number for {box.Name}.");
        if (!float.IsFinite(value)) throw new FormatException($"{box.Name} must be a finite number.");
        return value;
    }
}

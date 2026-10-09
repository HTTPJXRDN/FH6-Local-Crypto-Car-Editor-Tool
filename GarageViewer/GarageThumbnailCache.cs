using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FH6LocalCryptoTool.GarageViewer;

public sealed record GarageThumbnailResult(BitmapSource? Image, string Status);

/// <summary>Read-only exact CTN -> manifest GUID -> WebP lookup. No model-ID guesses.</summary>
public sealed class GarageThumbnailCache : IDisposable
{
    readonly string _directory;
    readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Task<GarageThumbnailResult>> _images = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Task<GarageThumbnailResult>> _previews = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<string> _order = new();
    readonly object _sync = new();
    readonly SemaphoreSlim _gate = new(2);
    readonly CancellationTokenSource _stop = new();
    static readonly Regex ReferenceName = new(@"^[0-9]+_[A-Za-z0-9_]+_bigThumb\.webp$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    const int MaxManifestBytes = 16 * 1024 * 1024, MaxImageBytes = 8 * 1024 * 1024;
    public string DirectoryPath => _directory;
    public int Count => _files.Count;
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForzaHorizon6", "LocalStorage_Cache", "CacheThumbnails");

    public GarageThumbnailCache(string directory)
    {
        _directory = Path.GetFullPath(directory);
        byte[] bytes = ReadBounded(Path.Combine(_directory, ".manifest"), MaxManifestBytes);
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        if (reader.ReadUInt32() != 2) throw new InvalidDataException("Unsupported thumbnail cache manifest version.");
        uint count = reader.ReadUInt32();
        if (count > 20000) throw new InvalidDataException("Thumbnail manifest has too many entries.");
        // Only the verified first lookup dictionary is needed. Trailing cache
        // bookkeeping is never interpreted, rewritten or exported by the tool.
        for (int i = 0; i < count; i++) {
            int length = reader.ReadInt32();
            if (length is < 1 or > 2048 || stream.Length - stream.Position < length + 16)
                throw new InvalidDataException("Incomplete thumbnail manifest record.");
            string name = new UTF8Encoding(false, true).GetString(reader.ReadBytes(length));
            var guid = new Guid(reader.ReadBytes(16));
            if (!ReferenceName.IsMatch(name) || guid == Guid.Empty) continue;
            if (!_files.TryAdd(name, Path.Combine(_directory, guid.ToString() + ".webp")))
                throw new InvalidDataException("Ambiguous thumbnail cache reference.");
        }
    }
    static byte[] ReadBounded(string path, int limit) {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length <= 0 || input.Length > limit) throw new InvalidDataException("Thumbnail input exceeds safe size limits.");
        byte[] bytes = new byte[checked((int)input.Length)];input.ReadExactly(bytes);return bytes;
    }
    public Task<GarageThumbnailResult> GetAsync(string? reference) => GetAsync(reference, false);
    public Task<GarageThumbnailResult> GetPreviewAsync(string? reference) => GetAsync(reference, true);
    Task<GarageThumbnailResult> GetAsync(string? reference, bool preview)
    {
        if (reference == null || !reference.StartsWith("CTN:", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new GarageThumbnailResult(null, "No cached garage preview"));
        string key = reference[4..] + "_bigThumb.webp";
        if (!ReferenceName.IsMatch(key) || !_files.TryGetValue(key, out string? file))
            return Task.FromResult(new GarageThumbnailResult(null, "Not cached"));
        lock (_sync) {
            if (_stop.IsCancellationRequested) return Task.FromResult(new GarageThumbnailResult(null, "Preview unavailable"));
            if(preview) {
                if(_previews.TryGetValue(key,out var current))return current;
                // Large previews are for the selection only, not retained on every row.
                if(_previews.Count>=4)_previews.Clear();
                var selected=LoadAsync(file,1600,900);_previews[key]=selected;return selected;
            }
            if (_images.TryGetValue(key, out var existing)) return existing;
            // Bounded image cache; rows themselves retain only the scaled preview.
            while (_images.Count >= 128 && _order.TryDequeue(out string? oldest)) _images.Remove(oldest);
            var task = LoadAsync(file,256,160);_images[key] = task;_order.Enqueue(key);return task;
        }
    }
    async Task<GarageThumbnailResult> LoadAsync(string file,int maxWidth,int maxHeight)
    {
        bool acquired = false;
        try {
            await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);acquired = true;
            return await Task.Run(() => {
                _stop.Token.ThrowIfCancellationRequested();
                byte[] bytes = ReadBounded(file, MaxImageBytes);
                if (bytes.Length < 16 || !bytes.AsSpan(0,4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8,4).SequenceEqual("WEBP"u8) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4,4)) != bytes.Length - 8)
                    throw new InvalidDataException("Invalid cached WebP image.");
                using var input = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count != 1) throw new InvalidDataException("Unexpected thumbnail frame layout.");
                var frame = decoder.Frames[0];
                if (frame.PixelWidth is < 1 or > 4096 || frame.PixelHeight is < 1 or > 4096 || (long)frame.PixelWidth * frame.PixelHeight > 8_000_000)
                    throw new InvalidDataException("Thumbnail dimensions exceed safe limits.");
                double scale = Math.Min(1, Math.Min((double)maxWidth / frame.PixelWidth, (double)maxHeight / frame.PixelHeight));
                // BitmapImage's native-size WebP path can choose opaque Bgr32,
                // discarding the source alpha. Convert the decoder frame explicitly
                // before scaling: keep car paint, glass and soft shadows intact.
                BitmapSource source = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
                if (scale < 1) source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                int stride = checked(source.PixelWidth * 4);
                byte[] pixels = new byte[checked(stride * source.PixelHeight)];
                source.CopyPixels(pixels, stride, 0);
                // A detached scaled snapshot avoids keeping full decoder images
                // alive on every small list row and never changes the cache file.
                var image = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
                image.Freeze();
                return new GarageThumbnailResult(image, "Cached garage preview");
            }, _stop.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) { return new(null, "Preview unavailable"); }
        catch (FileNotFoundException) { return new(null, "Not cached"); }
        catch (DirectoryNotFoundException) { return new(null, "Not cached"); }
        catch (NotSupportedException) { return new(null, "Windows WebP decoder unavailable"); }
        catch (Exception ex) when (ex is IOException or SystemException) { return new(null, "Preview unavailable"); }
        finally { if (acquired) _gate.Release(); }
    }
    public void Dispose() { _stop.Cancel();lock (_sync) { _images.Clear();_previews.Clear();_order.Clear(); } }
}

using System.Windows.Media;
using System.Windows.Media.Imaging;
using FH6CarEditor;

namespace FH6LocalCryptoTool.GarageViewer;

/// <summary>Actual instance cache first; exact model ID/MediaName stock image second.</summary>
public sealed class GarageThumbnailResolver(GarageThumbnailCache? cache, CarThumbnailLibrary? stock)
{
    readonly object _sync = new();
    readonly SemaphoreSlim _gate = new(2);
    readonly Dictionary<string, Task<GarageThumbnailResult>> _small = new();
    readonly Dictionary<string, Task<GarageThumbnailResult>> _large = new();
    readonly Queue<string> _order = new();

    public async Task<GarageThumbnailResult> GetAsync(string? reference, long carId, string mediaName, bool preview = false)
    {
        var cached = cache == null ? new GarageThumbnailResult(null, "Cache folder unavailable")
            : await (preview ? cache.GetPreviewAsync(reference) : cache.GetAsync(reference)).ConfigureAwait(false);
        // Never replace a readable actual garage image with a default stock image.
        if (cached.Image != null || stock == null) return cached;
        string key = carId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + mediaName;
        Task<GarageThumbnailResult> task;
        lock (_sync) {
            var images = preview ? _large : _small;
            if (!images.TryGetValue(key, out task!)) {
                if (preview && images.Count >= 4) images.Clear();
                if (!preview) while (images.Count >= 128 && _order.TryDequeue(out var oldest)) images.Remove(oldest);
                task = LoadStockAsync(carId, mediaName, preview);
                images[key] = task;
                if (!preview) _order.Enqueue(key);
            }
        }
        return await task.ConfigureAwait(false);
    }

    async Task<GarageThumbnailResult> LoadStockAsync(long carId, string mediaName, bool preview)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            return await Task.Run(() => {
                var result = stock!.Load(carId, mediaName);
                if (result.Image == null) return new GarageThumbnailResult(null, "No cached or stock preview available");
                if (preview) return new GarageThumbnailResult(result.Image, "Stock model preview • not your modifications/design");
                var source = result.Image;
                double scale = Math.Min(1, Math.Min(256d / source.PixelWidth, 160d / source.PixelHeight));
                BitmapSource scaled = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
                int stride = checked(scaled.PixelWidth * 4);
                byte[] pixels = new byte[checked(stride * scaled.PixelHeight)];
                scaled.CopyPixels(pixels, stride, 0);
                // Rows retain only the small transparent snapshot, not a full texture.
                var image = BitmapSource.Create(scaled.PixelWidth, scaled.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
                image.Freeze();
                return new GarageThumbnailResult(image, "Stock model preview • not your modifications/design");
            }).ConfigureAwait(false);
        } finally { _gate.Release(); }
    }
}

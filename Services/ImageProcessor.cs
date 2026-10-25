using SkiaSharp;

namespace SevenBlog.Services;

public enum ImageKind { Attachment, PostBanner, Avatar, ProfileBanner, SiteIcon, SiteHeader }
public sealed record ProcessedImage(byte[] Bytes, int Width, int Height);
public sealed class ImageUploadException(string message) : Exception(message);

// Every upload is decoded, resized and re-encoded as WebP. Originals are never kept, which bounds
// disk use, strips metadata (including GPS location), and keeps pages light.
public static class ImageProcessor
{
    public const long MaxUploadBytes = 12 * 1024 * 1024;
    public const string Accept = "image/jpeg,image/png,image/webp,image/gif";
    private const long MaxSourcePixels = 50_000_000;
    // Decoding is CPU- and memory-heavy; at most two images are processed at once.
    private static readonly SemaphoreSlim Gate = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 2));
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private static (int Width, int Height, bool Crop, int Quality) Target(ImageKind kind) => kind switch {
        ImageKind.Avatar => (256, 256, true, 85),
        ImageKind.ProfileBanner => (1600, 400, true, 80),
        ImageKind.PostBanner => (1600, 600, true, 80),
        ImageKind.SiteIcon => (128, 128, true, 90),
        ImageKind.SiteHeader => (2000, 500, true, 78),
        _ => (1600, 1600, false, 80)
    };

    public static async Task<ProcessedImage> ProcessAsync(IFormFile file, ImageKind kind, CancellationToken cancellationToken)
    {
        if (file.Length == 0) throw new ImageUploadException("The selected image is empty.");
        if (file.Length > MaxUploadBytes) throw new ImageUploadException("Images can be at most 12 MB.");
        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, cancellationToken);
        await Gate.WaitAsync(cancellationToken);
        try { return Process(buffer.ToArray(), kind); }
        finally { Gate.Release(); }
    }

    public static ProcessedImage Process(byte[] bytes, ImageKind kind)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new ImageUploadException("Use a JPEG, PNG, WebP or GIF image.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxSourcePixels)
            throw new ImageUploadException("The image dimensions are too large.");
        var target = Target(kind);
        var rotated = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        double width = rotated ? info.Height : info.Width, height = rotated ? info.Width : info.Height;
        // The smallest scale that still covers the output, so large photos decode at reduced size.
        var scale = Math.Min(1, target.Crop
            ? Math.Max(target.Width / width, target.Height / height)
            : Math.Min(target.Width / width, target.Height / height));
        using var decoded = Decode(codec, info, scale);
        using var upright = Orient(decoded, codec.EncodedOrigin);

        SKRect source;
        int outWidth, outHeight;
        if (target.Crop)
        {
            var ratio = (double)target.Width / target.Height;
            var cropWidth = Math.Min(upright.Width, upright.Height * ratio);
            var cropHeight = cropWidth / ratio;
            source = SKRect.Create((float)((upright.Width - cropWidth) / 2), (float)((upright.Height - cropHeight) / 2), (float)cropWidth, (float)cropHeight);
            outWidth = (int)Math.Round(Math.Min(target.Width, cropWidth));
            outHeight = Math.Max(1, (int)Math.Round(outWidth / ratio));
        }
        else
        {
            source = SKRect.Create(upright.Width, upright.Height);
            var fit = Math.Min(1, Math.Min((double)target.Width / upright.Width, (double)target.Height / upright.Height));
            outWidth = Math.Max(1, (int)Math.Round(upright.Width * fit));
            outHeight = Math.Max(1, (int)Math.Round(upright.Height * fit));
        }

        using var output = new SKBitmap(new SKImageInfo(outWidth, outHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(output))
        using (var image = SKImage.FromBitmap(upright))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(image, source, SKRect.Create(outWidth, outHeight), Sampling, null);
        }
        using var pixels = output.PeekPixels();
        using var encoded = pixels.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, target.Quality))
            ?? throw new ImageUploadException("The image could not be compressed.");
        return new ProcessedImage(encoded.ToArray(), outWidth, outHeight);
    }

    private static SKBitmap Decode(SKCodec codec, SKImageInfo info, double scale)
    {
        // JPEG and WebP can decode directly at a reduced size; other formats return full size.
        var size = codec.GetScaledDimensions((float)scale);
        if (size.Width < info.Width * scale || size.Height < info.Height * scale) size = info.Size;
        var decodeInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        return SKBitmap.Decode(codec, decodeInfo)
            ?? SKBitmap.Decode(codec, decodeInfo.WithSize(info.Width, info.Height))
            ?? throw new ImageUploadException("The image could not be read. It may be damaged.");
    }

    // Applies the EXIF orientation so phone photos are stored the right way up.
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        float w = source.Width, h = source.Height;
        SKMatrix? matrix = origin switch {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => null
        };
        if (matrix is null) return source.Copy();
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(new SKImageInfo(swap ? source.Height : source.Width, swap ? source.Width : source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        canvas.SetMatrix(matrix.Value);
        canvas.DrawBitmap(source, 0, 0, Sampling, null);
        return result;
    }
}

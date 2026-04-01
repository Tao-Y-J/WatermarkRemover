using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace WatermarkRemover.App.Services;

public sealed class ImageFileService : IImageFileService
{
    public Task<BitmapSource> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var stream = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var image = decoder.Frames[0];
                image.Freeze();
                return (BitmapSource)image;
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException)
            {
                return LoadWithOpenCv(path);
            }
        }, cancellationToken);
    }

    public Task SaveAsync(BitmapSource image, string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = NormalizeOutputExtension(Path.GetExtension(path));
            if (extension == ".webp")
            {
                using var mat = BitmapSourceConverter.ToMat(EnsureBgra32(image));
                Cv2.ImWrite(path, mat);
                return;
            }

            BitmapEncoder encoder = extension switch
            {
                ".jpg" => new JpegBitmapEncoder
                {
                    QualityLevel = 92,
                },
                ".bmp" => new BmpBitmapEncoder(),
                ".png" => new PngBitmapEncoder(),
                _ => throw new NotSupportedException($"Unsupported output image extension: {extension}"),
            };

            encoder.Frames.Add(BitmapFrame.Create(image));

            using var stream = File.Create(path);
            encoder.Save(stream);
        }, cancellationToken);
    }

    private static BitmapSource EnsureBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = source;
        converted.DestinationFormat = PixelFormats.Bgra32;
        converted.EndInit();
        converted.Freeze();
        return converted;
    }

    private static BitmapSource LoadWithOpenCv(string path)
    {
        using var mat = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (mat.Empty())
        {
            throw new InvalidOperationException("Failed to decode the selected image file.");
        }

        var image = BitmapSourceConverter.ToBitmapSource(mat);
        image.Freeze();
        return image;
    }

    private static string NormalizeOutputExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".png";
        }

        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ".jpg",
            ".bmp" => ".bmp",
            ".png" => ".png",
            ".webp" => ".webp",
            _ => throw new NotSupportedException($"Unsupported output image extension: {extension}"),
        };
    }
}

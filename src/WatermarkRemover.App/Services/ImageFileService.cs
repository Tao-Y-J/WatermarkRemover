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

            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var image = decoder.Frames[0];
            image.Freeze();
            return (BitmapSource)image;
        }, cancellationToken);
    }

    public Task SaveAsync(BitmapSource image, string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".webp")
            {
                using var mat = BitmapSourceConverter.ToMat(EnsureBgra32(image));
                Cv2.ImWrite(path, mat);
                return;
            }

            BitmapEncoder encoder = extension switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder
                {
                    QualityLevel = 92,
                },
                ".bmp" => new BmpBitmapEncoder(),
                _ => new PngBitmapEncoder(),
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
}

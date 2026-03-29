using System.Windows.Media.Imaging;

namespace WatermarkRemover.App.Services;

public interface IImageFileService
{
    Task<BitmapSource> LoadAsync(string path, CancellationToken cancellationToken = default);

    Task SaveAsync(BitmapSource image, string path, CancellationToken cancellationToken = default);
}

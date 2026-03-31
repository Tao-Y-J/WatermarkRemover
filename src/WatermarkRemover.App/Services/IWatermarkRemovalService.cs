using System.Windows.Media.Imaging;
using WatermarkRemover.App.Models;

namespace WatermarkRemover.App.Services;

public interface IWatermarkRemovalService
{
    Task<BitmapSource> RemoveWatermarkAsync(
        InpaintingRequest request,
        IProgress<WatermarkRemovalProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

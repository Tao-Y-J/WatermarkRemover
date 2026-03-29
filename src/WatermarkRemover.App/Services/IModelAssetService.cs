using WatermarkRemover.App.Models;

namespace WatermarkRemover.App.Services;

public interface IModelAssetService
{
    Task<ModelAssetResolution> ResolveBundledModelAsync(CancellationToken cancellationToken = default);
}

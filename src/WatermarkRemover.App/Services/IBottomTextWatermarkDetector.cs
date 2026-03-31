namespace WatermarkRemover.App.Services;

public interface IBottomTextWatermarkDetector
{
    Task<BottomTextWatermarkDetectionResult> DetectAsync(string imagePath, CancellationToken cancellationToken = default);
}

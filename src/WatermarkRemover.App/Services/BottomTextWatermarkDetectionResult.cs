using System.Windows.Media.Imaging;

namespace WatermarkRemover.App.Services;

public sealed class BottomTextWatermarkDetectionResult
{
    public BottomTextWatermarkDetectionResult(BitmapSource? maskImage, BitmapSource? debugImage, double confidence)
    {
        MaskImage = maskImage;
        DebugImage = debugImage;
        Confidence = Math.Clamp(confidence, 0d, 1d);
    }

    public BitmapSource? MaskImage { get; }

    public BitmapSource? DebugImage { get; }

    public double Confidence { get; }

    public bool HasDetection => MaskImage is not null;

    public bool IsAutoApplicable => WatermarkDetectionPolicy.ShouldAutoApply(this);

    public bool NeedsManualReview => HasDetection && !IsAutoApplicable;
}

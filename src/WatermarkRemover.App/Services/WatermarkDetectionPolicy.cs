namespace WatermarkRemover.App.Services;

public static class WatermarkDetectionPolicy
{
    public const double MinimumAutoApplyConfidence = 0.25;

    public static bool ShouldAutoApply(BottomTextWatermarkDetectionResult? detection)
    {
        return detection is { HasDetection: true }
               && detection.Confidence >= MinimumAutoApplyConfidence;
    }
}

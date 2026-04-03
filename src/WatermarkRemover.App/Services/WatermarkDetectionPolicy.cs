namespace WatermarkRemover.App.Services;

public static class WatermarkDetectionPolicy
{
    public const double MinimumAutoApplyConfidence = 0.30;
    public const double MinimumReviewConfidence = 0.16;

    public static bool ShouldAutoApply(BottomTextWatermarkDetectionResult? detection)
    {
        return detection is { HasDetection: true }
               && detection.Confidence >= MinimumAutoApplyConfidence;
    }

    public static bool ShouldKeepCandidate(BottomTextWatermarkDetectionResult? detection)
    {
        return detection is { HasDetection: true }
               && detection.Confidence >= MinimumReviewConfidence;
    }
}

namespace WatermarkRemover.App.Models;

public sealed record WatermarkRepairOptions(
    int MinPaddingX,
    int MinPaddingY,
    double PaddingXScale,
    double PaddingYScale,
    int MaskDilateKernelSize,
    int MaskDilateIterations,
    double FeatherSigma)
{
    public string PresetName { get; init; } = "Custom";

    public static WatermarkRepairOptions Balanced { get; } = new(
        MinPaddingX: 56,
        MinPaddingY: 72,
        PaddingXScale: 1.2,
        PaddingYScale: 1.8,
        MaskDilateKernelSize: 3,
        MaskDilateIterations: 1,
        FeatherSigma: 2.4)
    {
        PresetName = "Balanced"
    };

    public static WatermarkRepairOptions CoverageBoost { get; } = new(
        MinPaddingX: 72,
        MinPaddingY: 96,
        PaddingXScale: 1.4,
        PaddingYScale: 2.0,
        MaskDilateKernelSize: 5,
        MaskDilateIterations: 1,
        FeatherSigma: 2.8)
    {
        PresetName = "CoverageBoost"
    };

    public static WatermarkRepairOptions Default { get; } = CoverageBoost;
}

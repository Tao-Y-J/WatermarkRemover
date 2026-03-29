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
    public static WatermarkRepairOptions Default { get; } = new(
        MinPaddingX: 56,
        MinPaddingY: 72,
        PaddingXScale: 1.2,
        PaddingYScale: 1.8,
        MaskDilateKernelSize: 3,
        MaskDilateIterations: 1,
        FeatherSigma: 2.4);
}

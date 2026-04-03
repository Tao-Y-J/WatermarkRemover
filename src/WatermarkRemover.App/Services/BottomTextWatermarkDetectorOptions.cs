namespace WatermarkRemover.App.Services;

public sealed record BottomTextWatermarkDetectorOptions(
    string PresetName,
    int MinExpandX,
    double ExpandWidthScale,
    int MinExpandY,
    double ExpandHeightScale,
    bool IncludeBrightTextUnion,
    int InitialCloseKernelWidth,
    int InitialCloseKernelHeight,
    int DilateKernelWidth,
    int DilateKernelHeight,
    int AdaptiveHorizontalCloseDivisor,
    int AdaptiveHorizontalCloseMaxWidth,
    double FallbackConfidenceCap)
{
    public static BottomTextWatermarkDetectorOptions Balanced { get; } = new(
        PresetName: "Balanced",
        MinExpandX: 18,
        ExpandWidthScale: 0.10,
        MinExpandY: 8,
        ExpandHeightScale: 0.45,
        IncludeBrightTextUnion: false,
        InitialCloseKernelWidth: 5,
        InitialCloseKernelHeight: 3,
        DilateKernelWidth: 5,
        DilateKernelHeight: 3,
        AdaptiveHorizontalCloseDivisor: 0,
        AdaptiveHorizontalCloseMaxWidth: 0,
        FallbackConfidenceCap: 0.24);

    public static BottomTextWatermarkDetectorOptions CoverageBoost { get; } = new(
        PresetName: "CoverageBoost",
        MinExpandX: 24,
        ExpandWidthScale: 0.14,
        MinExpandY: 10,
        ExpandHeightScale: 0.60,
        IncludeBrightTextUnion: true,
        InitialCloseKernelWidth: 7,
        InitialCloseKernelHeight: 3,
        DilateKernelWidth: 5,
        DilateKernelHeight: 3,
        AdaptiveHorizontalCloseDivisor: 6,
        AdaptiveHorizontalCloseMaxWidth: 61,
        FallbackConfidenceCap: 0.24);

    public static BottomTextWatermarkDetectorOptions Default { get; } = CoverageBoost;
}

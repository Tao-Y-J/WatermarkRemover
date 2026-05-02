using System.Windows.Media.Imaging;

namespace WatermarkRemover.App.Models;

public sealed class InpaintingRequest
{
    public required string ImagePath { get; init; }

    public required string ModelPath { get; init; }

    public required BitmapSource MaskImage { get; init; }

    /// <summary>
    /// 可选的每请求修复参数。如果为 null，则使用服务构造时注入的默认参数。
    /// </summary>
    public WatermarkRepairOptions? RepairOptions { get; init; }
}

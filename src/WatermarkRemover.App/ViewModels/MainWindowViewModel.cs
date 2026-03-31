using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WatermarkRemover.App.Models;
using WatermarkRemover.App.Services;

namespace WatermarkRemover.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IFileDialogService _fileDialogService;
    private readonly IImageFileService _imageFileService;
    private readonly IWatermarkRemovalService _watermarkRemovalService;
    private readonly IBottomTextWatermarkDetector _bottomTextWatermarkDetector;
    private readonly IModelAssetService _modelAssetService;

    [ObservableProperty]
    private string? imagePath;

    [ObservableProperty]
    private string? modelPath;

    [ObservableProperty]
    private BitmapSource? sourceImage;

    [ObservableProperty]
    private BitmapSource? editableMask;

    [ObservableProperty]
    private BitmapSource? resultImage;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = "先选择图片，然后直接框选水印区域开始处理。模型会由程序自动判断。";

    public MainWindowViewModel(
        IFileDialogService fileDialogService,
        IImageFileService imageFileService,
        IWatermarkRemovalService watermarkRemovalService,
        IBottomTextWatermarkDetector bottomTextWatermarkDetector,
        IModelAssetService modelAssetService)
    {
        _fileDialogService = fileDialogService;
        _imageFileService = imageFileService;
        _watermarkRemovalService = watermarkRemovalService;
        _bottomTextWatermarkDetector = bottomTextWatermarkDetector;
        _modelAssetService = modelAssetService;
    }

    public bool HasResult => ResultImage is not null;

    private bool CanInteract => !IsBusy;

    private bool CanProcess => !IsBusy
                               && SourceImage is not null
                               && EditableMask is not null
                               && !string.IsNullOrWhiteSpace(ImagePath);

    private bool CanSave => !IsBusy && ResultImage is not null;

    partial void OnImagePathChanged(string? value)
    {
        ProcessCommand.NotifyCanExecuteChanged();
    }

    partial void OnSourceImageChanged(BitmapSource? value)
    {
        ProcessCommand.NotifyCanExecuteChanged();
    }

    partial void OnEditableMaskChanged(BitmapSource? value)
    {
        ProcessCommand.NotifyCanExecuteChanged();
    }

    partial void OnResultImageChanged(BitmapSource? value)
    {
        OnPropertyChanged(nameof(HasResult));
        SaveResultCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OpenImageCommand.NotifyCanExecuteChanged();
        ProcessCommand.NotifyCanExecuteChanged();
        SaveResultCommand.NotifyCanExecuteChanged();
    }

    public async Task InitializeAsync()
    {
        await EnsureModelReadyAsync(isStartup: true);
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task OpenImageAsync()
    {
        var selectedPath = _fileDialogService.OpenImageFile();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "正在载入图片并检测底部文字水印...";

            var image = await _imageFileService.LoadAsync(selectedPath);
            SourceImage = image;
            ImagePath = selectedPath;
            EditableMask = null;
            ResultImage = null;

            BottomTextWatermarkDetectionResult? detection = null;
            string? detectionFailure = null;

            try
            {
                detection = await _bottomTextWatermarkDetector.DetectAsync(selectedPath);
            }
            catch (Exception ex)
            {
                detectionFailure = ex.Message;
            }

            EditableMask = detection?.MaskImage;

            StatusMessage = detection is { HasDetection: true }
                ? $"已载入 {Path.GetFileName(selectedPath)}，并自动检测到底部文字水印（置信度 {detection.Confidence:P0}）。可以直接处理，也可以继续补框调整。"
                : detectionFailure is null
                    ? $"已载入 {Path.GetFileName(selectedPath)}。未自动检测到明确的底部文字水印，请直接框选需要处理的区域。"
                    : $"已载入 {Path.GetFileName(selectedPath)}。自动检测失败：{detectionFailure}；请直接框选需要处理的区域。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"图片加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanProcess))]
    private async Task ProcessAsync()
    {
        if (ImagePath is null || EditableMask is null)
        {
            return;
        }

        if (!await EnsureModelReadyAsync())
        {
            return;
        }

        var resolvedModelPath = ModelPath;
        if (string.IsNullOrWhiteSpace(resolvedModelPath))
        {
            StatusMessage = "程序还没有选定可用模型，请稍后重试。";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "正在自动选择合适模型并执行去水印处理...";

            var result = await _watermarkRemovalService.RemoveWatermarkAsync(new InpaintingRequest
            {
                ImagePath = ImagePath,
                ModelPath = resolvedModelPath,
                MaskImage = EditableMask,
            });

            ResultImage = result;
            StatusMessage = "处理完成，可以直接保存结果图片。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"处理失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveResultAsync()
    {
        if (ResultImage is null)
        {
            return;
        }

        var sourceExtension = string.IsNullOrWhiteSpace(ImagePath)
            ? ".png"
            : Path.GetExtension(ImagePath);

        var suggestedFileName = string.IsNullOrWhiteSpace(ImagePath)
            ? $"watermark-clean{sourceExtension}"
            : $"{Path.GetFileNameWithoutExtension(ImagePath)}-clean{sourceExtension}";

        var savePath = _fileDialogService.SaveImageFile(suggestedFileName, sourceExtension);
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "正在保存处理后的图片...";

            await _imageFileService.SaveAsync(ResultImage, savePath);
            StatusMessage = $"结果已保存到 {savePath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> EnsureModelReadyAsync(bool isStartup = false)
    {
        if (!string.IsNullOrWhiteSpace(ModelPath) && File.Exists(ModelPath))
        {
            return true;
        }

        try
        {
            IsBusy = true;
            StatusMessage = isStartup
                ? "正在自动判断并加载内置模型..."
                : "正在自动判断当前应使用的模型...";

            var resolution = await _modelAssetService.ResolveBundledModelAsync();
            ModelPath = resolution.ModelPath;
            StatusMessage = $"已自动选择模型 {Path.GetFileName(resolution.ModelPath)}。";

            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"自动模型判断失败：{ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

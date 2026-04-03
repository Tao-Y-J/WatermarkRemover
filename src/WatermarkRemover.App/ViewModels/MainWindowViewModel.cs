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
    private bool isProcessing;

    [ObservableProperty]
    private string processingStageMessage = string.Empty;

    [ObservableProperty]
    private string statusMessage = "先选择图片，然后编辑选区再处理。";

    public MainWindowViewModel(
        IFileDialogService fileDialogService,
        IImageFileService imageFileService,
        IWatermarkRemovalService watermarkRemovalService,
        IModelAssetService modelAssetService)
    {
        _fileDialogService = fileDialogService;
        _imageFileService = imageFileService;
        _watermarkRemovalService = watermarkRemovalService;
        _modelAssetService = modelAssetService;
    }

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
            StatusMessage = "正在载入图片...";

            SourceImage = await _imageFileService.LoadAsync(selectedPath);
            ImagePath = selectedPath;
            EditableMask = null;
            ResultImage = null;

            StatusMessage = $"已载入 {Path.GetFileName(selectedPath)}。请编辑选区后再处理。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"图片载入失败：{ex.Message}";
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

        BeginProcessingUi("正在准备模型...");
        ResultImage = null;
        if (!await EnsureModelReadyAsync())
        {
            ResetProcessingUi();
            return;
        }

        var resolvedModelPath = ModelPath;
        if (string.IsNullOrWhiteSpace(resolvedModelPath))
        {
            StatusMessage = "程序还没有选定可用模型，请稍后重试。";
            ResetProcessingUi();
            return;
        }

        try
        {
            IsBusy = true;
            UpdateProcessingUi("模型已就绪，开始处理...");

            ResultImage = await _watermarkRemovalService.RemoveWatermarkAsync(new InpaintingRequest
            {
                ImagePath = ImagePath,
                ModelPath = resolvedModelPath,
                MaskImage = EditableMask,
            }, new Progress<WatermarkRemovalProgress>(progressUpdate =>
            {
                UpdateProcessingUi(progressUpdate.Message);
            }));

            UpdateProcessingUi("处理完成");
            StatusMessage = "处理完成，可以直接保存结果图片。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"处理失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ResetProcessingUi();
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

    private void BeginProcessingUi(string message)
    {
        IsProcessing = true;
        UpdateProcessingUi(message);
    }

    private void UpdateProcessingUi(string message)
    {
        ProcessingStageMessage = message;
        StatusMessage = message;
    }

    private void ResetProcessingUi()
    {
        IsProcessing = false;
        ProcessingStageMessage = string.Empty;
    }
}

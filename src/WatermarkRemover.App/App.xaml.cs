using System.Windows;
using WatermarkRemover.App.Models;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 使用 CoverageBoost 作为桌面端默认修复参数，提供更多上下文和覆盖
        var repairOptions = WatermarkRepairOptions.Default;

        var viewModel = new MainWindowViewModel(
            new FileDialogService(),
            new ImageFileService(),
            new WatermarkRemovalService(repairOptions),
            new ModelAssetService());

        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        window.Loaded += async (_, _) => await viewModel.InitializeAsync();

        MainWindow = window;
        window.Show();
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace WatermarkRemover.App.Controls;

public partial class MaskEditorControl : UserControl
{
    private readonly List<WpfRect> _selectedRegions = [];
    private BitmapSource? _importedMask;
    private bool _isDragging;
    private bool _isUpdatingMaskFromCanvas;
    private WpfPoint _dragStartPoint;

    public static readonly DependencyProperty SourceImageProperty =
        DependencyProperty.Register(
            nameof(SourceImage),
            typeof(BitmapSource),
            typeof(MaskEditorControl),
            new PropertyMetadata(null, OnSourceImageChanged));

    public static readonly DependencyProperty MaskImageProperty =
        DependencyProperty.Register(
            nameof(MaskImage),
            typeof(BitmapSource),
            typeof(MaskEditorControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnMaskImageChanged));

    public MaskEditorControl()
    {
        InitializeComponent();
        UpdateOverlayVisibility();
    }

    public BitmapSource? SourceImage
    {
        get => (BitmapSource?)GetValue(SourceImageProperty);
        set => SetValue(SourceImageProperty, value);
    }

    public BitmapSource? MaskImage
    {
        get => (BitmapSource?)GetValue(MaskImageProperty);
        set => SetValue(MaskImageProperty, value);
    }

    private static void OnSourceImageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MaskEditorControl)d;
        control.ApplySourceImage(e.NewValue as BitmapSource);
    }

    private static void OnMaskImageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MaskEditorControl)d;
        control.ApplyExternalMaskChange(e.NewValue as BitmapSource);
    }

    private void ApplySourceImage(BitmapSource? source)
    {
        SourceImagePresenter.Source = source;

        if (source is null)
        {
            SurfaceGrid.Width = 1280;
            SurfaceGrid.Height = 720;
            SelectionSurface.Width = 1280;
            SelectionSurface.Height = 720;
            ClearSelections();
            SetImportedMask(null);
            PublishMask(null);
        }
        else
        {
            SurfaceGrid.Width = source.PixelWidth;
            SurfaceGrid.Height = source.PixelHeight;
            SelectionSurface.Width = source.PixelWidth;
            SelectionSurface.Height = source.PixelHeight;
            ClearSelections();
            SetImportedMask(null);
            PublishMask(null);
        }

        UpdateOverlayVisibility();
    }

    private void ApplyExternalMaskChange(BitmapSource? newMask)
    {
        if (_isUpdatingMaskFromCanvas)
        {
            return;
        }

        ClearSelections();
        SetImportedMask(SourceImage is null ? null : newMask);
        UpdateOverlayVisibility();
    }

    private void SelectionSurface_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SourceImage is null)
        {
            return;
        }

        _dragStartPoint = ClampToSurface(e.GetPosition(SelectionSurface));
        _isDragging = true;
        SelectionSurface.CaptureMouse();

        UpdatePreviewRectangle(new WpfRect(_dragStartPoint, _dragStartPoint));
        PreviewRectangle.Visibility = Visibility.Visible;
    }

    private void SelectionSurface_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var currentPoint = ClampToSurface(e.GetPosition(SelectionSurface));
        UpdatePreviewRectangle(CreateNormalizedRect(_dragStartPoint, currentPoint));
    }

    private void SelectionSurface_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        SelectionSurface.ReleaseMouseCapture();

        var endPoint = ClampToSurface(e.GetPosition(SelectionSurface));
        var region = CreateNormalizedRect(_dragStartPoint, endPoint);

        PreviewRectangle.Visibility = Visibility.Collapsed;

        if (region.Width < 6 || region.Height < 6)
        {
            return;
        }

        _selectedRegions.Add(region);
        AddCommittedRectangle(region);
        ExportMaskFromSelections();
    }

    private void SelectionSurface_OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectedRegions.Count == 0 && _importedMask is null)
        {
            return;
        }

        ClearSelections();
        SetImportedMask(null);
        PublishMask(null);
    }

    private void AddCommittedRectangle(WpfRect region)
    {
        var rectangle = new Rectangle
        {
            Width = region.Width,
            Height = region.Height,
            RadiusX = 6,
            RadiusY = 6,
            Fill = new SolidColorBrush(Color.FromArgb(88, 249, 115, 22)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 249, 115, 22)),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(rectangle, region.Left);
        Canvas.SetTop(rectangle, region.Top);
        CommittedSelectionLayer.Children.Add(rectangle);
    }

    private void ClearSelections()
    {
        _selectedRegions.Clear();
        CommittedSelectionLayer.Children.Clear();
        PreviewRectangle.Visibility = Visibility.Collapsed;
        UpdateOverlayVisibility();
    }

    private void ExportMaskFromSelections()
    {
        UpdateOverlayVisibility();

        if (SourceImage is null)
        {
            PublishMask(null);
            return;
        }

        if (_selectedRegions.Count == 0 && _importedMask is null)
        {
            PublishMask(null);
            return;
        }

        var width = Math.Max(1, (int)Math.Round(SurfaceGrid.Width));
        var height = Math.Max(1, (int)Math.Round(SurfaceGrid.Height));

        var visual = new DrawingVisual();
        using (var drawingContext = visual.RenderOpen())
        {
            drawingContext.DrawRectangle(Brushes.Black, null, new WpfRect(0, 0, width, height));

            if (_importedMask is not null)
            {
                drawingContext.DrawImage(_importedMask, new WpfRect(0, 0, width, height));
            }

            foreach (var region in _selectedRegions)
            {
                drawingContext.DrawRectangle(Brushes.White, null, region);
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        PublishMask(bitmap);
    }

    private void PublishMask(BitmapSource? bitmap)
    {
        _isUpdatingMaskFromCanvas = true;
        SetCurrentValue(MaskImageProperty, bitmap);
        _isUpdatingMaskFromCanvas = false;
    }

    private void SetImportedMask(BitmapSource? mask)
    {
        _importedMask = mask;
        ImportedMaskPresenter.Source = mask;
    }

    private void UpdateOverlayVisibility()
    {
        var hasImage = SourceImage is not null;
        PlaceholderPanel.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdatePreviewRectangle(WpfRect region)
    {
        Canvas.SetLeft(PreviewRectangle, region.Left);
        Canvas.SetTop(PreviewRectangle, region.Top);
        PreviewRectangle.Width = region.Width;
        PreviewRectangle.Height = region.Height;
    }

    private WpfPoint ClampToSurface(WpfPoint point)
    {
        var maxX = Math.Max(0, SelectionSurface.ActualWidth);
        var maxY = Math.Max(0, SelectionSurface.ActualHeight);

        return new WpfPoint(
            Math.Clamp(point.X, 0, maxX),
            Math.Clamp(point.Y, 0, maxY));
    }

    private static WpfRect CreateNormalizedRect(WpfPoint start, WpfPoint end)
    {
        return new WpfRect(
            new WpfPoint(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y)),
            new WpfPoint(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)));
    }
}

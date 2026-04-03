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
    private const double MinRectangleSize = 6d;
    private const double MinBrushRadius = 4d;
    private const double MaxBrushRadius = 36d;
    private const double DefaultBrushRadius = 10d;

    private readonly List<WpfRect> _selectedRegions = [];
    private readonly List<BrushStroke> _brushStrokes = [];
    private readonly List<MaskEditAction> _editHistory = [];
    private BitmapSource? _importedMask;
    private bool _isDragging;
    private bool _isBrushEditing;
    private bool _isUpdatingMaskFromCanvas;
    private bool _isErasing;
    private WpfPoint _dragStartPoint;
    private WpfPoint _lastBrushPoint;
    private double _brushRadius = DefaultBrushRadius;

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

        Focus();
        Keyboard.Focus(this);

        _dragStartPoint = ClampToSurface(e.GetPosition(SelectionSurface));
        _lastBrushPoint = _dragStartPoint;
        _isErasing = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _isBrushEditing = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _isDragging = !_isBrushEditing;
        SelectionSurface.CaptureMouse();

        if (_isBrushEditing)
        {
            AddBrushStrokeDot(_dragStartPoint, _isErasing, addToHistory: true);
            ExportMaskFromSelections();
            return;
        }

        UpdatePreviewRectangle(new WpfRect(_dragStartPoint, _dragStartPoint));
        PreviewRectangle.Visibility = Visibility.Visible;
    }

    private void SelectionSurface_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isBrushEditing)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndInteraction();
                return;
            }

            var brushPoint = ClampToSurface(e.GetPosition(SelectionSurface));
            AddBrushStrokeSegment(_lastBrushPoint, brushPoint, _isErasing);
            _lastBrushPoint = brushPoint;
            ExportMaskFromSelections();
            return;
        }

        if (!_isDragging)
        {
            return;
        }

        var currentPoint = ClampToSurface(e.GetPosition(SelectionSurface));
        UpdatePreviewRectangle(CreateNormalizedRect(_dragStartPoint, currentPoint));
    }

    private void SelectionSurface_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isBrushEditing)
        {
            EndInteraction();
            return;
        }

        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        SelectionSurface.ReleaseMouseCapture();

        var endPoint = ClampToSurface(e.GetPosition(SelectionSurface));
        var region = CreateNormalizedRect(_dragStartPoint, endPoint);

        PreviewRectangle.Visibility = Visibility.Collapsed;

        if (region.Width < MinRectangleSize || region.Height < MinRectangleSize)
        {
            return;
        }

        _selectedRegions.Add(region);
        _editHistory.Add(new MaskEditAction(MaskEditKind.Rectangle, 1));
        AddCommittedRectangle(region);
        ExportMaskFromSelections();
    }

    private void SelectionSurface_OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectedRegions.Count == 0 && _brushStrokes.Count == 0 && _importedMask is null)
        {
            return;
        }

        ClearSelections();
        SetImportedMask(null);
        PublishMask(null);
    }

    private void MaskEditorControl_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Z)
        {
            UndoLastEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.OemOpenBrackets)
        {
            AdjustBrushRadius(-2d);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Oem6)
        {
            AdjustBrushRadius(2d);
            e.Handled = true;
        }
    }

    private void MaskEditorControl_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        AdjustBrushRadius(e.Delta > 0 ? 1d : -1d);
        e.Handled = true;
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

    private void AddBrushStrokeSegment(WpfPoint start, WpfPoint end, bool isErasing)
    {
        var distance = (end - start).Length;
        var steps = Math.Max(1, (int)Math.Ceiling(distance / (_brushRadius * 0.55)));
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            var point = new WpfPoint(
                start.X + ((end.X - start.X) * t),
                start.Y + ((end.Y - start.Y) * t));
            AddBrushStrokeDot(point, isErasing, addToHistory: false);
        }

        if (steps > 0)
        {
            _editHistory.Add(new MaskEditAction(MaskEditKind.BrushStroke, steps));
        }
    }

    private void AddBrushStrokeDot(WpfPoint point, bool isErasing, bool addToHistory)
    {
        _brushStrokes.Add(new BrushStroke(point, _brushRadius, isErasing));

        var ellipse = new Ellipse
        {
            Width = _brushRadius * 2,
            Height = _brushRadius * 2,
            IsHitTestVisible = false,
            Fill = isErasing
                ? new SolidColorBrush(Color.FromArgb(90, 239, 68, 68))
                : new SolidColorBrush(Color.FromArgb(92, 34, 197, 94)),
            Stroke = isErasing
                ? new SolidColorBrush(Color.FromArgb(220, 220, 38, 38))
                : new SolidColorBrush(Color.FromArgb(220, 22, 163, 74)),
            StrokeThickness = 1.5,
        };

        Canvas.SetLeft(ellipse, point.X - _brushRadius);
        Canvas.SetTop(ellipse, point.Y - _brushRadius);
        BrushStrokeLayer.Children.Add(ellipse);

        if (addToHistory)
        {
            _editHistory.Add(new MaskEditAction(MaskEditKind.BrushStroke, 1));
        }
    }

    private void ClearSelections()
    {
        _selectedRegions.Clear();
        _brushStrokes.Clear();
        _editHistory.Clear();
        CommittedSelectionLayer.Children.Clear();
        BrushStrokeLayer.Children.Clear();
        PreviewRectangle.Visibility = Visibility.Collapsed;
        EndInteraction();
        UpdateOverlayVisibility();
    }

    private void UndoLastEdit()
    {
        if (_editHistory.Count == 0)
        {
            return;
        }

        var lastEdit = _editHistory[^1];
        _editHistory.RemoveAt(_editHistory.Count - 1);

        switch (lastEdit.Kind)
        {
            case MaskEditKind.Rectangle:
                for (var i = 0; i < lastEdit.Count && _selectedRegions.Count > 0; i++)
                {
                    _selectedRegions.RemoveAt(_selectedRegions.Count - 1);
                }
                RebuildRectangleLayer();
                break;
            case MaskEditKind.BrushStroke:
                for (var i = 0; i < lastEdit.Count && _brushStrokes.Count > 0; i++)
                {
                    _brushStrokes.RemoveAt(_brushStrokes.Count - 1);
                }
                RebuildBrushLayer();
                break;
        }

        ExportMaskFromSelections();
    }

    private void RebuildRectangleLayer()
    {
        CommittedSelectionLayer.Children.Clear();
        foreach (var region in _selectedRegions)
        {
            AddCommittedRectangle(region);
        }
    }

    private void RebuildBrushLayer()
    {
        BrushStrokeLayer.Children.Clear();
        foreach (var stroke in _brushStrokes)
        {
            var ellipse = new Ellipse
            {
                Width = stroke.Radius * 2,
                Height = stroke.Radius * 2,
                IsHitTestVisible = false,
                Fill = stroke.IsErasing
                    ? new SolidColorBrush(Color.FromArgb(90, 239, 68, 68))
                    : new SolidColorBrush(Color.FromArgb(92, 34, 197, 94)),
                Stroke = stroke.IsErasing
                    ? new SolidColorBrush(Color.FromArgb(220, 220, 38, 38))
                    : new SolidColorBrush(Color.FromArgb(220, 22, 163, 74)),
                StrokeThickness = 1.5,
            };

            Canvas.SetLeft(ellipse, stroke.Center.X - stroke.Radius);
            Canvas.SetTop(ellipse, stroke.Center.Y - stroke.Radius);
            BrushStrokeLayer.Children.Add(ellipse);
        }
    }

    private void AdjustBrushRadius(double delta)
    {
        _brushRadius = Math.Clamp(_brushRadius + delta, MinBrushRadius, MaxBrushRadius);
    }

    private void ExportMaskFromSelections()
    {
        UpdateOverlayVisibility();

        if (SourceImage is null)
        {
            PublishMask(null);
            return;
        }

        if (_selectedRegions.Count == 0 && _brushStrokes.Count == 0 && _importedMask is null)
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

            foreach (var stroke in _brushStrokes)
            {
                drawingContext.DrawEllipse(
                    stroke.IsErasing ? Brushes.Black : Brushes.White,
                    null,
                    stroke.Center,
                    stroke.Radius,
                    stroke.Radius);
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

    private void EndInteraction()
    {
        _isDragging = false;
        _isBrushEditing = false;
        _isErasing = false;
        PreviewRectangle.Visibility = Visibility.Collapsed;

        if (SelectionSurface.IsMouseCaptured)
        {
            SelectionSurface.ReleaseMouseCapture();
        }
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

    private sealed record BrushStroke(WpfPoint Center, double Radius, bool IsErasing);

    private sealed record MaskEditAction(MaskEditKind Kind, int Count);

    private enum MaskEditKind
    {
        Rectangle,
        BrushStroke,
    }
}

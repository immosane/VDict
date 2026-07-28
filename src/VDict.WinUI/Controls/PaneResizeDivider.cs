using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace VDict.WinUI.Controls;

public sealed class PaneResizeDivider : UserControl
{
    private readonly Border _resizeLine;
    private bool _isDragging;
    private bool _isPointerOver;

    public PaneResizeDivider()
    {
        Grid hitTarget = new()
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        _resizeLine = new Border
        {
            Width = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(1)
        };
        hitTarget.Children.Add(_resizeLine);
        Content = hitTarget;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        ActualThemeChanged += OnActualThemeChanged;
        UpdateVisualState();
    }

    public bool IsDragging
    {
        get => _isDragging;
        set
        {
            if (_isDragging == value)
            {
                return;
            }

            _isDragging = value;
            UpdateVisualState();
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdateVisualState();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        UpdateVisualState();
    }

    private void OnGotFocus(object sender, RoutedEventArgs e) => UpdateVisualState();

    private void OnLostFocus(object sender, RoutedEventArgs e) => UpdateVisualState();

    private void OnActualThemeChanged(FrameworkElement sender, object args) =>
        UpdateVisualState();

    private void UpdateVisualState()
    {
        bool emphasized =
            _isDragging ||
            _isPointerOver ||
            FocusState != FocusState.Unfocused;
        string resourceKey = emphasized
            ? "AccentFillColorDefaultBrush"
            : "DividerStrokeColorDefaultBrush";
        _resizeLine.Background =
            Application.Current.Resources[resourceKey] as Brush ??
            new SolidColorBrush(emphasized
                ? Microsoft.UI.Colors.DodgerBlue
                : Microsoft.UI.Colors.Gray);
        _resizeLine.Opacity = emphasized ? 1 : 0.8;
        _resizeLine.Width = _isDragging ? 2 : 1;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using LitePdf.Core;

namespace LitePdf.App.Views;

public partial class RegionSelector : UserControl
{
    private Point _start;
    private bool _dragging;

    public event Action<RectD>? RegionSelected;

    public RegionSelector()
    {
        InitializeComponent();
        Canvas.MouseLeftButtonDown += OnMouseDown;
        Canvas.MouseMove += OnMouseMove;
        Canvas.MouseLeftButtonUp += OnMouseUp;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Canvas);
        _dragging = true;
        Canvas.CaptureMouse();
        Canvas.SetLeft(SelectionRect, _start.X);
        Canvas.SetTop(SelectionRect, _start.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        SelectionRect.Visibility = Visibility.Visible;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetPosition(Canvas);
        double x = Math.Min(_start.X, pos.X);
        double y = Math.Min(_start.Y, pos.Y);
        double w = Math.Abs(pos.X - _start.X);
        double h = Math.Abs(pos.Y - _start.Y);
        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Canvas.ReleaseMouseCapture();
        var pos = e.GetPosition(Canvas);
        double x = Math.Min(_start.X, pos.X);
        double y = Math.Min(_start.Y, pos.Y);
        double w = Math.Abs(pos.X - _start.X);
        double h = Math.Abs(pos.Y - _start.Y);
        if (w > 5 && h > 5)
        {
            // Convert canvas rect to page points? Caller will map.
            RegionSelected?.Invoke(new RectD(x, y, x + w, y + h));
        }
        SelectionRect.Visibility = Visibility.Collapsed;
    }
}

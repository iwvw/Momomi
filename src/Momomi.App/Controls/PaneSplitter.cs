using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Momomi.App.Controls;

public sealed class PaneSplitter : Grid
{
    public event EventHandler? DragStarted;
    public event EventHandler<double>? Dragged;
    public event EventHandler? DragCompleted;

    private bool _dragging;
    private double _startX;

    public PaneSplitter()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => EndDrag();
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _startX = e.GetCurrentPoint(null).Position.X;
        CapturePointer(e.Pointer);
        DragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var x = e.GetCurrentPoint(null).Position.X;
        Dragged?.Invoke(this, x - _startX);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }
}

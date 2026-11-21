using System;
using System.Windows;
using VPSnode.Core;

namespace VPSnode.Desktop;

// A review owns one pair; switching pairs retires both old views. Modeless children
// allow side-by-side comparison while the modal review keeps the main page in place.
internal sealed class NodeComparisonWindows : IDisposable
{
    private readonly Window _owner;
    private readonly Func<Node, string, Window> _create;
    private (string Left, string Right)? _pair;
    private Window? _left;
    private Window? _right;
    private bool _disposed;

    internal NodeComparisonWindows(Window owner, Func<Node, string, Window> create)
    {
        _owner = owner; _create = create; owner.Closed += OwnerClosed;
    }

    internal void Select(Node left, Node right)
    {
        if (_disposed) return;
        if (_pair == (left.Id, right.Id)) return;
        CloseViews(); _pair = (left.Id, right.Id);
    }

    internal void Open(Node left, Node right, bool rightSide)
    {
        if (_disposed) return;
        Select(left, right);
        var window = rightSide ? _right : _left;
        if (window != null)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate(); return;
        }
        window = _create(rightSide ? right : left, rightSide ? "右侧" : "左侧");
        window.Owner = _owner;
        var current = window;
        window.Closed += (_, _) => { if (_left == current) _left = null; if (_right == current) _right = null; };
        if (rightSide) _right = window; else _left = window;
        window.Show();
        // Start at opposite edges of the available desktop; both remain movable/resizable.
        var area = SystemParameters.WorkArea;
        window.Left = rightSide ? area.Right - window.ActualWidth : area.Left;
        window.Top = Math.Max(area.Top, _owner.Top);
    }

    private void CloseViews() { _left?.Close(); _right?.Close(); _left = _right = null; }
    private void OwnerClosed(object? sender, EventArgs args) => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _owner.Closed -= OwnerClosed; CloseViews(); _pair = null;
    }
}

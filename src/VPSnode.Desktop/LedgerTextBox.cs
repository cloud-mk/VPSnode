using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VPSnode.Desktop;

public sealed class LedgerTextBox : TextBox
{
    public LedgerTextBox()
    {
        IsReadOnly = true;
        IsTabStop = false;
        Cursor = Cursors.IBeam;
        Background = Brushes.Transparent;
        BorderThickness = Padding = new Thickness(0);
        SetResourceReference(ForegroundProperty, "Text");
        SetResourceReference(SelectionBrushProperty, "TextSelection");
        SetResourceReference(SelectionTextBrushProperty, "SelectedText");
        SelectionOpacity = 1;
        IsInactiveSelectionHighlightEnabled = true;
        ContextMenu = new ContextMenu();
        var copy = new MenuItem { Header = "复制", InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) => CopyValue(SelectionLength > 0 ? SelectedText : Text);
        var select = new MenuItem { Header = "全选", InputGestureText = "Ctrl+A" };
        select.Click += (_, _) => { Focus(); SelectAll(); };
        ContextMenu.Items.Add(copy);
        ContextMenu.Items.Add(select);
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, e) => { CopyValue(SelectionLength > 0 ? SelectedText : Text); e.Handled = true; },
            (_, e) => { e.CanExecute = Text.Length > 0; e.Handled = true; }));
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        e.Handled = true;
    }

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        ContextMenu.PlacementTarget = this;
        ContextMenu.Placement = PlacementMode.MousePoint;
        ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    internal static void CopyValue(string text)
    {
        if (text.Length == 0) return;
        try { Clipboard.SetDataObject(text, true); }
        catch (ExternalException) { MessageBox.Show("剪贴板暂不可用，请重试。"); }
    }
}

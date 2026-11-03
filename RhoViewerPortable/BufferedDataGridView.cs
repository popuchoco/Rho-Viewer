using System.Runtime.InteropServices;

namespace RhoViewerPortable;

/// <summary>
/// A double-buffered DataGridView with redraw suppression for bulk model updates.
/// </summary>
internal sealed class BufferedDataGridView : DataGridView
{
    private const int WmSetRedraw = 0x000B;
    private int _updateDepth;

    public BufferedDataGridView()
    {
        DoubleBuffered = true;
    }

    internal void BeginUpdate()
    {
        if (_updateDepth++ == 0 && IsHandleCreated)
            SetRedraw(false);
    }

    internal void EndUpdate()
    {
        if (_updateDepth == 0)
            throw new InvalidOperationException("EndUpdate must be paired with BeginUpdate.");

        if (--_updateDepth != 0)
            return;

        if (!IsHandleCreated || IsDisposed)
            return;

        SetRedraw(true);
        Invalidate(true);
        Update();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_updateDepth > 0)
            SetRedraw(false);
    }

    private void SetRedraw(bool enabled)
    {
        SendMessage(Handle, WmSetRedraw, enabled ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

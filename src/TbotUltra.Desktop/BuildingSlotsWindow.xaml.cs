using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TbotUltra.Desktop;

/// <summary>
/// Read-only reference window showing which building slot number sits where in a village.
/// Pure display: no bot state, no services, nothing to save. Resizing keeps the window's
/// original proportions so the map never ends up letterboxed inside the frame.
/// </summary>
public partial class BuildingSlotsWindow : Window
{
    private const int WmSizing = 0x0214;

    // wParam of WM_SIZING: which edge or corner the user is dragging.
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;

    private double _aspectRatio;

    public BuildingSlotsWindow()
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            // No HWND means no resize messages to correct; the window still works, just unlocked.
            return;
        }

        // Capture the ratio from the real window rect (physical pixels, borders included) so the
        // correction below matches what the user actually drags, at any DPI.
        if (GetWindowRect(source.Handle, out var rect))
        {
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width > 0 && height > 0)
            {
                _aspectRatio = (double)width / height;
            }
        }

        source.AddHook(OnWindowMessage);
    }

    // WPF has no built-in aspect lock, so the proposed size in WM_SIZING is corrected before
    // Windows applies it. Adjusting the edge the user is NOT dragging keeps the drag anchored.
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmSizing || _aspectRatio <= 0)
        {
            return IntPtr.Zero;
        }

        var rect = Marshal.PtrToStructure<Rect>(lParam);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return IntPtr.Zero;
        }

        switch ((int)wParam)
        {
            case WmszLeft:
            case WmszRight:
                // Dragging a side: width is authoritative, height follows.
                rect.Bottom = rect.Top + (int)Math.Round(width / _aspectRatio);
                break;

            case WmszTop:
            case WmszBottom:
                // Dragging top/bottom: height is authoritative, width follows.
                rect.Right = rect.Left + (int)Math.Round(height * _aspectRatio);
                break;

            default:
                // Corners: take the width and move the horizontal edge that is not anchored.
                var correctedHeight = (int)Math.Round(width / _aspectRatio);
                if ((int)wParam is WmszTopLeft or WmszTopRight)
                {
                    rect.Top = rect.Bottom - correctedHeight;
                }
                else
                {
                    rect.Bottom = rect.Top + correctedHeight;
                }

                break;
        }

        Marshal.StructureToPtr(rect, lParam, false);
        handled = true;
        return (IntPtr)1;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
}

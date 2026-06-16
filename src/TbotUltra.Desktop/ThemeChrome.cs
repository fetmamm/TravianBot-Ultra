using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TbotUltra.Desktop;

/// <summary>
/// Shared helper that switches a window's OS title bar (the WPF chrome we can't restyle in XAML)
/// to dark mode.
///
/// The important method is <see cref="EnableEarlyDarkTitleBar"/>: it applies the dark title bar at
/// <see cref="Window.SourceInitialized"/> — i.e. after the HWND exists but BEFORE the window is
/// shown — so the title bar is dark on the very first paint and never flashes light. Call it from a
/// window constructor (before Show/ShowDialog). Applying it later (e.g. on Loaded, after the window
/// is already on screen) is what caused the brief white-then-dark flash on every popup.
/// </summary>
internal static class ThemeChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Ensures the window gets a dark title bar the moment its HWND is created, before it is shown.
    /// Safe to call in a constructor: if the HWND already exists it applies immediately, otherwise it
    /// waits for <see cref="Window.SourceInitialized"/>.
    /// </summary>
    public static void EnableEarlyDarkTitleBar(Window window)
    {
        if (window is null)
        {
            return;
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            // HWND already created — apply right away.
            TryEnableDarkTitleBar(window);
            return;
        }

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            TryEnableDarkTitleBar(window);
        }

        window.SourceInitialized += OnSourceInitialized;
    }

    /// <summary>Switches a window's OS title bar to dark. Cosmetic only — failures are ignored.</summary>
    public static void TryEnableDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var useDark = 1;
            // Attribute 20 on Windows 10 1903+; fall back to 19 on older builds.
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
            }
        }
        catch
        {
            // Dark title bar is purely cosmetic; never let it break window creation.
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Threading;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace MaskOfKefka.Output;

/// <summary>
/// Native Win32 window on its own thread (dedicated message pump). This is the window OBS
/// captures. No D3D ever runs here, only messages; drawing is done by OutputRenderer on the
/// game's render thread.
/// </summary>
internal sealed unsafe class OutputWindowHost : IDisposable
{
    private const string WindowClassName = "MaskOfKefkaOutput";
    private const string WindowTitle = "Mask of Kefka";

    /// <summary>Custom message: reapplies the window style (bordered/borderless) on the owning thread.</summary>
    private const uint WmApplyStyle = WM.WM_APP + 1;

    private const uint BorderedStyle = WS.WS_OVERLAPPEDWINDOW;
    private const uint BorderlessStyle = WS.WS_POPUP | WS.WS_THICKFRAME | WS.WS_MINIMIZEBOX | WS.WS_MAXIMIZEBOX;

    // WndProc is static (native callback); since there is only one window per plugin,
    // the state it needs lives in statics.
    private static volatile bool borderless;

    private Thread? thread;
    private nint hwnd;
    private volatile bool ready;
    private volatile bool closed;

    public HWND Hwnd => (HWND)(void*)hwnd;

    /// <summary>Window created and ready to receive a swapchain.</summary>
    public bool Ready => ready;

    /// <summary>Window was closed (by the user or an error) and its thread ended.</summary>
    public bool Closed => closed;

    public void Start(int clientWidth, int clientHeight, bool startBorderless)
    {
        if (thread != null)
            throw new InvalidOperationException("Output window already started.");

        borderless = startBorderless;
        thread = new Thread(() => ThreadMain(clientWidth, clientHeight))
        {
            IsBackground = true,
            Name = "MaskOfKefka.OutputWindow",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void Dispose()
    {
        if (ready && hwnd != 0)
            PostMessageW((HWND)(void*)hwnd, WM.WM_CLOSE, 0u, 0);
        thread?.Join(3000);
        closed = true;
    }

    /// <summary>Toggles bordered/borderless at runtime. Safe to call from any thread.</summary>
    public void SetBorderless(bool value)
    {
        if (borderless == value)
            return;
        borderless = value;
        if (ready && hwnd != 0)
            PostMessageW((HWND)(void*)hwnd, WmApplyStyle, 0u, 0);
    }

    private void ThreadMain(int clientWidth, int clientHeight)
    {
        try
        {
            RunMessageLoop(clientWidth, clientHeight);
        }
        finally
        {
            ready = false;
            closed = true;
            hwnd = 0;
        }
    }

    private void RunMessageLoop(int clientWidth, int clientHeight)
    {
        fixed (char* clsName = WindowClassName)
        fixed (char* title = WindowTitle)
        {
            var hInstance = (HINSTANCE)(void*)GetModuleHandleW(null);

            // A registration may have survived a previous plugin reload; removal failing is fine.
            _ = UnregisterClassW(clsName, hInstance);

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = CS.CS_HREDRAW | CS.CS_VREDRAW,
                lpfnWndProc = &WndProc,
                hInstance = hInstance,
                hCursor = LoadCursorW(HINSTANCE.NULL, IDC.IDC_ARROW),
                lpszClassName = clsName,
            };
            if (RegisterClassExW(&wc) == 0)
                return;

            try
            {
                var style = borderless ? BorderlessStyle : BorderedStyle;
                var rect = new RECT { right = clientWidth, bottom = clientHeight };
                _ = AdjustWindowRectEx(&rect, style, BOOL.FALSE, 0);

                var window = CreateWindowExW(
                    0,
                    clsName,
                    title,
                    style,
                    CW_USEDEFAULT,
                    CW_USEDEFAULT,
                    rect.right - rect.left,
                    rect.bottom - rect.top,
                    HWND.NULL,
                    HMENU.NULL,
                    hInstance,
                    null);
                if (window == HWND.NULL)
                    return;

                hwnd = (nint)window.Value;
                ready = true;

                // Without stealing focus from the game.
                _ = ShowWindow(window, SW.SW_SHOWNOACTIVATE);

                MSG msg;
                while (true)
                {
                    int result = GetMessageW(&msg, HWND.NULL, 0, 0);
                    if (result == 0 || result == -1)
                        break;
                    _ = TranslateMessage(&msg);
                    _ = DispatchMessageW(&msg);
                }
            }
            finally
            {
                _ = UnregisterClassW(clsName, hInstance);
            }
        }
    }

    [UnmanagedCallersOnly]
    private static LRESULT WndProc(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case WM.WM_DESTROY:
                PostQuitMessage(0);
                return 0;

            // No background: the swapchain covers the whole client area; avoids resize flicker.
            case WM.WM_ERASEBKGND:
                return 1;

            case WmApplyStyle:
            {
                var style = (borderless ? BorderlessStyle : BorderedStyle) | WS.WS_VISIBLE;
                _ = SetWindowLongPtrW(window, GWL.GWL_STYLE, (nint)style);
                // SWP_FRAMECHANGED forces Windows to recompute the frame with the new style.
                _ = SetWindowPos(window, HWND.NULL, 0, 0, 0, 0,
                    SWP.SWP_NOMOVE | SWP.SWP_NOSIZE | SWP.SWP_NOZORDER | SWP.SWP_NOACTIVATE | SWP.SWP_FRAMECHANGED);
                return 0;
            }

            // Borderless has no title bar: the window body becomes the drag handle and an
            // 8px strip along the edges becomes the resize handle.
            case WM.WM_NCHITTEST when borderless:
            {
                var x = (int)(short)((nuint)lParam.Value & 0xFFFF);
                var y = (int)(short)(((nuint)lParam.Value >> 16) & 0xFFFF);
                RECT rect;
                if (!GetWindowRect(window, &rect))
                    break;

                const int grip = 8;
                var left = x < rect.left + grip;
                var right = x >= rect.right - grip;
                var top = y < rect.top + grip;
                var bottom = y >= rect.bottom - grip;

                if (top && left) return HTTOPLEFT;
                if (top && right) return HTTOPRIGHT;
                if (bottom && left) return HTBOTTOMLEFT;
                if (bottom && right) return HTBOTTOMRIGHT;
                if (left) return HTLEFT;
                if (right) return HTRIGHT;
                if (top) return HTTOP;
                if (bottom) return HTBOTTOM;
                return HTCAPTION;
            }
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }
}

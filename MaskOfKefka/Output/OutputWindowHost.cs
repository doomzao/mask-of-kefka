using System;
using System.Runtime.InteropServices;
using System.Threading;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace MaskOfKefka.Output;

/// <summary>
/// Janela Win32 nativa em thread própria (message pump dedicado). É essa janela que o OBS
/// captura. O D3D nunca roda aqui — só mensagens; quem desenha é o OutputRenderer, na
/// thread de render do jogo.
/// </summary>
internal sealed unsafe class OutputWindowHost : IDisposable
{
    private const string WindowClassName = "MaskOfKefkaOutput";
    private const string WindowTitle = "Mask of Kefka";

    /// <summary>Mensagem custom: reaplica o estilo da janela (com/sem borda) na thread dona dela.</summary>
    private const uint WmApplyStyle = WM.WM_APP + 1;

    private const uint BorderedStyle = WS.WS_OVERLAPPEDWINDOW;
    private const uint BorderlessStyle = WS.WS_POPUP | WS.WS_THICKFRAME | WS.WS_MINIMIZEBOX | WS.WS_MAXIMIZEBOX;

    // O WndProc é estático (callback nativo); como só existe uma janela por plugin,
    // o estado que ele precisa ler fica em estáticos.
    private static volatile bool borderless;

    private Thread? thread;
    private nint hwnd;
    private volatile bool ready;
    private volatile bool closed;

    public HWND Hwnd => (HWND)(void*)hwnd;

    /// <summary>Janela criada e pronta pra receber uma swapchain.</summary>
    public bool Ready => ready;

    /// <summary>Janela foi fechada (pelo usuário ou erro) e a thread terminou.</summary>
    public bool Closed => closed;

    public void Start(int clientWidth, int clientHeight, bool startBorderless)
    {
        if (thread != null)
            throw new InvalidOperationException("Janela de saída já iniciada.");

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

    /// <summary>Troca com/sem borda em runtime. Pode ser chamado de qualquer thread.</summary>
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

            // Pode ter sobrado registro de um reload anterior do plugin; remover falha sem problema.
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

                // Sem roubar o foco do jogo.
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

            // Sem fundo: o swapchain cobre a área cliente inteira; evita flicker no resize.
            case WM.WM_ERASEBKGND:
                return 1;

            case WmApplyStyle:
            {
                var style = (borderless ? BorderlessStyle : BorderedStyle) | WS.WS_VISIBLE;
                _ = SetWindowLongPtrW(window, GWL.GWL_STYLE, (nint)style);
                // SWP_FRAMECHANGED força o Windows a recalcular a moldura com o estilo novo.
                _ = SetWindowPos(window, HWND.NULL, 0, 0, 0, 0,
                    SWP.SWP_NOMOVE | SWP.SWP_NOSIZE | SWP.SWP_NOZORDER | SWP.SWP_NOACTIVATE | SWP.SWP_FRAMECHANGED);
                return 0;
            }

            // Sem borda não há barra de título: o corpo da janela vira "alça" de arrastar
            // e uma faixa de 8px nas bordas vira alça de redimensionar.
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

using AIWeather.Services;
using LibVLCSharp.Shared;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using MediaColor = System.Windows.Media.Color;

namespace AIWeather.Views
{
    public class VideoHwndHost : HwndHost
    {
        private IntPtr _hwnd;
        private IntPtr _themeHwnd;
        private IntPtr _videoHwnd;
        private IntPtr _parentHwnd;
        private IntPtr _backgroundBrush;
        private readonly uint _backgroundColorRef;
        private double _videoWidth;
        private double _videoHeight;

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_PAINT = 0x000F;
        private const int WM_CTLCOLORSTATIC = 0x0138;
        private const int SS_BLACKRECT = 0x0004;

        public IntPtr Hwnd => _hwnd;
        public IntPtr VideoHwnd => _videoHwnd;

        public MediaPlayer Player { get; }

        public VideoHwndHost(MediaPlayer player, MediaColor backgroundColor)
        {
            Player = player;
            _backgroundColorRef = (uint)(
                backgroundColor.R
                | (backgroundColor.G << 8)
                | (backgroundColor.B << 16));
        }

        internal bool TrySetVideoContentSize(
            double videoWidth,
            double videoHeight,
            out VideoHostLayout layout)
        {
            _videoWidth = videoWidth;
            _videoHeight = videoHeight;
            return TryLayoutVideoTarget(out layout);
        }

        internal void ShowVideoSurface()
        {
            // Remove the startup clip only after the caller has confirmed a decoded video
            // surface. Until this point WPF remains visible through the native airspace,
            // instead of exposing either the target Static control or LibVLC's white vout.
            if (_hwnd != IntPtr.Zero)
            {
                SetWindowRgn(_hwnd, IntPtr.Zero, true);
            }

            if (_videoHwnd != IntPtr.Zero)
            {
                ShowWindow(_videoHwnd, SW_SHOWNA);
                BringWindowToTop(_videoHwnd);
            }
        }

        internal void ShowStartupCover()
        {
            ApplyStartupClip();

            if (_themeHwnd != IntPtr.Zero)
            {
                ShowWindow(_themeHwnd, SW_SHOWNA);
                BringWindowToTop(_themeHwnd);
                InvalidateRect(_themeHwnd, IntPtr.Zero, true);
            }
        }

        private void ApplyStartupClip()
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            // Keep a single native pixel available so LibVLC/Direct3D still sees a visible,
            // normally sized on-screen target. The rest of the HwndHost is clipped out and
            // therefore shows the real WPF/N.I.N.A. theme beneath it. The 1.30 field log
            // proved that its blank preview was a TCP connection failure before Vout, not a
            // region-induced decoder failure; removing this clip merely exposed VLC's white
            // connection surface.
            var startupRegion = CreateRectRgn(0, 0, 1, 1);
            if (startupRegion == IntPtr.Zero)
            {
                return;
            }

            if (SetWindowRgn(_hwnd, startupRegion, true) == 0)
            {
                DeleteObject(startupRegion);
            }
        }

        internal bool TryGetRenderedVideoSize(out double width, out double height)
        {
            width = 0;
            height = 0;
            if (_videoHwnd == IntPtr.Zero)
            {
                return false;
            }

            var bestWidth = 0;
            var bestHeight = 0;
            var bestArea = 0;
            EnumChildWindows(
                _videoHwnd,
                (child, _) =>
                {
                    var className = new StringBuilder(128);
                    GetClassName(child, className, className.Capacity);
                    if (className.ToString().StartsWith("VLC video output", StringComparison.Ordinal)
                        && GetClientRect(child, out var bounds))
                    {
                        var candidateWidth = Math.Max(0, bounds.Right - bounds.Left);
                        var candidateHeight = Math.Max(0, bounds.Bottom - bounds.Top);
                        var area = candidateWidth * candidateHeight;
                        if (candidateWidth > 1 && candidateHeight > 1 && area > bestArea)
                        {
                            bestWidth = candidateWidth;
                            bestHeight = candidateHeight;
                            bestArea = area;
                        }
                    }

                    return true;
                },
                IntPtr.Zero);

            width = bestWidth;
            height = bestHeight;
            return bestArea > 0;
        }

        private bool TryLayoutVideoTarget(out VideoHostLayout layout)
        {
            layout = default;
            if (_hwnd == IntPtr.Zero
                || _videoHwnd == IntPtr.Zero
                || !GetClientRect(_hwnd, out var bounds))
            {
                return false;
            }

            var containerWidth = Math.Max(1, bounds.Right - bounds.Left);
            var containerHeight = Math.Max(1, bounds.Bottom - bounds.Top);
            PositionThemeSurface(containerWidth, containerHeight);
            if (_videoWidth <= 0 || _videoHeight <= 0)
            {
                // LibVLC's Direct3D output needs a normal, onscreen-sized child before it can
                // report decoded dimensions. Keep it full-sized under the native theme cover;
                // the user sees the cover, while VLC sees a valid output surface.
                PositionVideoTarget(0, 0, containerWidth, containerHeight, bringToFront: false);
                BringWindowToTop(_themeHwnd);
                InvalidateRect(_hwnd, IntPtr.Zero, true);
                return false;
            }

            var fitted = VideoFitCalculator.FitInside(
                containerWidth,
                containerHeight,
                _videoWidth,
                _videoHeight);
            var targetWidth = Math.Clamp((int)Math.Round(fitted.Width), 1, containerWidth);
            var targetHeight = Math.Clamp((int)Math.Round(fitted.Height), 1, containerHeight);
            var x = Math.Max(0, (containerWidth - targetWidth) / 2);
            var y = Math.Max(0, (containerHeight - targetHeight) / 2);

            PositionVideoTarget(x, y, targetWidth, targetHeight, bringToFront: true);
            InvalidateRect(_hwnd, IntPtr.Zero, true);

            layout = new VideoHostLayout(
                containerWidth,
                containerHeight,
                targetWidth,
                targetHeight,
                x,
                y);
            return true;
        }

        private void PositionThemeSurface(int width, int height)
        {
            SetWindowPos(
                _themeHwnd,
                IntPtr.Zero,
                0,
                0,
                Math.Max(1, width),
                Math.Max(1, height),
                SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private void PositionVideoTarget(int x, int y, int width, int height, bool bringToFront)
        {
            SetWindowPos(
                _videoHwnd,
                IntPtr.Zero,
                x,
                y,
                Math.Max(1, width),
                Math.Max(1, height),
                (bringToFront ? 0 : SWP_NOZORDER) | SWP_NOACTIVATE);
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _parentHwnd = hwndParent.Handle;

            // Create with a small initial size; WPF will call OnWindowPositionChanged
            // and we'll resize to the actual layout size.
            const int width = 1;
            const int height = 1;

            // HwndHost is a native airspace and cannot be transparently composed with the
            // WPF tree. Use a plugin-owned native window class whose class brush is the exact
            // active N.I.N.A. theme color; a system Static control otherwise repaints white.
            _backgroundBrush = CreateSolidBrush(_backgroundColorRef);
            _hwnd = CreateWindowEx(
                0,
                "Static",
                "",
                WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
                0, 0,
                width, height,
                _parentHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed for video host window");
            }

            // LibVLC creates and owns another native child hierarchy underneath the HWND it
            // receives. Never give it the full-size themed host: its "VLC video main" window
            // clears unused letterbox pixels to white at startup and on later repaints. Give it
            // a dedicated, visible child at a normal size. It must already be visible when
            // LibVLC creates its descendants; otherwise they inherit a hidden state.
            _videoHwnd = CreateWindowEx(
                0,
                "Static",
                "",
                // SS_BLACKRECT matters here, not only on a sibling cover. LibVLC does not
                // create its own vout children immediately; during that gap the target HWND
                // itself is what Windows paints. A plain Static control has a white client
                // area, which was the remaining white 16:9 rectangle seen after pressing
                // Start. Keeping the actual target black gives VLC a normal visible surface
                // without exposing that system-default white paint before its first frame.
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | SS_BLACKRECT,
                0, 0,
                width, height,
                _hwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_videoHwnd == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                throw new Win32Exception(error, "CreateWindowEx failed for dedicated VLC video target");
            }

            // A system-owned black rectangle is deterministic even when HwndHost/WPF replaces
            // paint handlers. It covers VLC only during startup; after the first decoded frame,
            // the fitted VLC target is brought above it.
            _themeHwnd = CreateWindowEx(
                0,
                "Static",
                "",
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | SS_BLACKRECT,
                0, 0,
                width, height,
                _hwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_themeHwnd == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                DestroyWindow(_hwnd);
                _videoHwnd = IntPtr.Zero;
                _hwnd = IntPtr.Zero;
                throw new Win32Exception(error, "CreateWindowEx failed for native theme cover");
            }

            ApplyStartupClip();
            InvalidateRect(_hwnd, IntPtr.Zero, true);

            return new HandleRef(this, _hwnd);
        }

        protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
        {
            base.OnWindowPositionChanged(rcBoundingBox);

            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            TryLayoutVideoTarget(out _);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (_hwnd != IntPtr.Zero)
            {
                // Destroying the themed host also destroys the dedicated VLC child hierarchy.
                _themeHwnd = IntPtr.Zero;
                _videoHwnd = IntPtr.Zero;
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (_backgroundBrush != IntPtr.Zero)
            {
                DeleteObject(_backgroundBrush);
                _backgroundBrush = IntPtr.Zero;
            }
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            var result = HandleBackgroundMessage(hwnd, msg, wParam, ref handled);
            if (handled)
            {
                return result;
            }

            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        private IntPtr HandleBackgroundMessage(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            ref bool handled)
        {
            if (msg == WM_ERASEBKGND && _backgroundBrush != IntPtr.Zero)
            {
                if (GetClientRect(hwnd, out var bounds))
                {
                    FillRect(wParam, ref bounds, _backgroundBrush);
                }

                handled = true;
                return new IntPtr(1);
            }

            if (msg == WM_PAINT && _backgroundBrush != IntPtr.Zero)
            {
                var paint = new PaintStruct { Reserved = new byte[32] };
                var dc = BeginPaint(hwnd, ref paint);
                if (dc != IntPtr.Zero && GetClientRect(hwnd, out var bounds))
                {
                    FillRect(dc, ref bounds, _backgroundBrush);
                }
                EndPaint(hwnd, ref paint);

                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_CTLCOLORSTATIC && _backgroundBrush != IntPtr.Zero)
            {
                SetBkColor(wParam, _backgroundColorRef);
                handled = true;
                return _backgroundBrush;
            }

            return IntPtr.Zero;
        }

        [DllImport("user32.dll", EntryPoint = "CreateWindowEx", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hwndParent,
            IntPtr hMenu,
            IntPtr hInst,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SW_SHOWNA = 8;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            int uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(
            IntPtr parent,
            EnumChildWindowsDelegate callback,
            IntPtr parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int FillRect(IntPtr hDC, ref NativeRect lprc, IntPtr hbr);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, ref PaintStruct lpPaint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EndPaint(IntPtr hWnd, ref PaintStruct lpPaint);

        [DllImport("gdi32.dll")]
        private static extern uint SetBkColor(IntPtr hdc, uint colorRef);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint colorRef);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PaintStruct
        {
            public IntPtr DeviceContext;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Erase;
            public NativeRect Paint;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Restore;
            [MarshalAs(UnmanagedType.Bool)]
            public bool IncrementalUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] Reserved;
        }

        private delegate bool EnumChildWindowsDelegate(IntPtr hwnd, IntPtr parameter);

    }

    internal readonly record struct VideoHostLayout(
        int ContainerWidth,
        int ContainerHeight,
        int VideoWidth,
        int VideoHeight,
        int X,
        int Y);
}

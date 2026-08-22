using LibVLCSharp.Shared;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using MediaColor = System.Windows.Media.Color;

namespace AIWeather.Views
{
    public class VideoHwndHost : HwndHost
    {
        private IntPtr _hwnd;
        private IntPtr _parentHwnd;
        private IntPtr _backgroundBrush;
        private readonly uint _backgroundColorRef;

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WM_ERASEBKGND = 0x0014;

        public IntPtr Hwnd => _hwnd;

        public MediaPlayer Player { get; }

        public VideoHwndHost(MediaPlayer player, MediaColor backgroundColor)
        {
            Player = player;
            _backgroundColorRef = (uint)(
                backgroundColor.R
                | (backgroundColor.G << 8)
                | (backgroundColor.B << 16));
        }

        public void ResizeTo(double width, double height)
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            var targetWidth = Math.Max(1, (int)Math.Round(width));
            var targetHeight = Math.Max(1, (int)Math.Round(height));
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, targetWidth, targetHeight, SWP_NOZORDER | SWP_NOACTIVATE);
            InvalidateRect(_hwnd, IntPtr.Zero, true);
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _parentHwnd = hwndParent.Handle;

            // Create with a small initial size; WPF will call OnWindowPositionChanged
            // and we'll resize to the actual layout size.
            const int width = 1;
            const int height = 1;

            // HwndHost is a native airspace and cannot be transparently composed with the
            // WPF tree. Give the native child the exact active N.I.N.A. theme color instead
            // of leaving the Win32 Static control's default white background visible.
            _backgroundBrush = CreateSolidBrush(_backgroundColorRef);
            _hwnd = CreateWindowEx(
                0,
                "Static",
                "",
                WS_CHILD | WS_VISIBLE,
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

            ResizeTo(rcBoundingBox.Width, rcBoundingBox.Height);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (_hwnd != IntPtr.Zero)
            {
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
            if (msg == WM_ERASEBKGND && _backgroundBrush != IntPtr.Zero)
            {
                if (GetClientRect(hwnd, out var bounds))
                {
                    FillRect(wParam, ref bounds, _backgroundBrush);
                }

                handled = true;
                return new IntPtr(1);
            }

            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int FillRect(IntPtr hDC, ref NativeRect lprc, IntPtr hbr);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint colorRef);

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

    }
}

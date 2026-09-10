using System;
using System.Runtime.InteropServices;

namespace MirrorAndPlay
{
    internal static class Win32Interops
    {
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        public struct ClientCropArea(int x, int y, int width, int height)
        {
            public int X = x;
            public int Y = y;
            public int Width = width;
            public int Height = height;
        }

        // クライアント領域（タイトルバー等を除いた中身）の矩形を取得
        public static ClientCropArea GetClientCropArea(IntPtr hwnd)
        {
            GetWindowRect(hwnd, out var winRect);
            GetClientRect(hwnd, out var clientRect);

            var pt = new POINT { X = 0, Y = 0 };
            ClientToScreen(hwnd, ref pt);

            // ウィンドウ全体から見たクライアント領域の開始位置（物理ピクセル）
            var offsetX = pt.X - winRect.Left; // 左枠の幅
            var offsetY = pt.Y - winRect.Top;  // タイトルバー ＋ 上枠の高さ
            var clientWidth = clientRect.Right - clientRect.Left;
            var clientHeight = clientRect.Bottom - clientRect.Top;

            // FFmpeg対策：偶数ピクセルに丸める
            clientWidth &= ~1;
            clientHeight &= ~1;

            return new ClientCropArea(offsetX, offsetY, clientWidth, clientHeight);
        }
    }
}

using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace MirrorAndPlay
{
    public static class CaptureHelper
    {
        // 1. HSTRING を手動で作成・破棄する API を追加
        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            int length,
            out IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        // 2. 第一引数を string ではなく生ポインタ (IntPtr) に変更
        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int RoGetActivationFactory(
            IntPtr activatableClassId,
            [In] ref Guid iid,
            out IntPtr factory);

        [ComImport]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            [PreserveSig]
            int CreateForWindow(
                IntPtr hWnd,
                [In] ref Guid riid,
                out IntPtr result);

            [PreserveSig]
            int CreateForMonitor(
                IntPtr hMonitor,
                [In] ref Guid riid,
                out IntPtr result);
        }

        public static GraphicsCaptureItem CreateItemForWindow(IntPtr hWnd)
        {
            var interopGuid = typeof(IGraphicsCaptureItemInterop).GUID;
            const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";

            // 手動で HSTRING を生成
            int hr = WindowsCreateString(className, className.Length, out IntPtr hstring);
            Marshal.ThrowExceptionForHR(hr);

            IntPtr factoryPtr = IntPtr.Zero;
            try
            {
                // 生ポインタとして RoGetActivationFactory に渡す
                hr = RoGetActivationFactory(hstring, ref interopGuid, out factoryPtr);
                Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                // HSTRING の解放
                WindowsDeleteString(hstring);
            }

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);

            // IGraphicsCaptureItem のインターフェース GUID
            var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            hr = interop.CreateForWindow(hWnd, ref itemGuid, out IntPtr itemPointer);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                return GraphicsCaptureItem.FromAbi(itemPointer);
            }
            finally
            {
                Marshal.Release(itemPointer);
            }
        }
    }
}
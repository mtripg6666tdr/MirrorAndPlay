using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace MirrorAndPlay
{
    public static class Direct3D11Helper
    {
        // DXGIデバイスからWinRTのIDirect3DDeviceポインタを生成するAPI
        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true, ExactSpelling = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        // WinRTのSurfaceからネイティブのDirectXテクスチャを取り出すためのインターフェース
        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [ComVisible(true)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            [PreserveSig]
            int GetInterface([In] ref Guid iid, out IntPtr p);
        }

        /// <summary>
        /// Vorticeの ID3D11Device から Windows.Graphics の IDirect3DDevice を生成
        /// </summary>
        public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3d11Device)
        {
            // 1. D3D11Device から IDXGIDevice を取得
            using var dxgiDevice = d3d11Device.QueryInterface<IDXGIDevice>();

            // 2. Win32 API で WinRT 互換のデバイスポインタ (IInspectable) を作成
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr pInspectable);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                // 3. C#/WinRT でインターフェース型へ投影復元
                return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pInspectable);
            }
            finally
            {
                Marshal.Release(pInspectable);
            }
        }

        /// <summary>
        /// キャプチャフレーム (IDirect3DSurface) から Vorticeの ID3D11Texture2D を取り出す
        /// </summary>
        public static ID3D11Texture2D CreateTexture2DFromSurface(IDirect3DSurface surface)
        {
            // WinRT拡張メソッド As<T>() でネイティブCOMインターフェースを照会
            var access = surface.As<IDirect3DDxgiInterfaceAccess>();

            var iid = typeof(ID3D11Texture2D).GUID;
            int hr = access.GetInterface(ref iid, out IntPtr pTexture);
            Marshal.ThrowExceptionForHR(hr);

            // 取り出したポインタを Vortice のテクスチャオブジェクトとしてラップ
            return new ID3D11Texture2D(pTexture);
        }
    }
}
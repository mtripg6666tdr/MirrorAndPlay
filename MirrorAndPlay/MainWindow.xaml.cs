using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Media3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace MirrorAndPlay
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, IDisposable
    {
        private readonly FFmpegUtils ffmpeg = new(AudioStreamer.PipeName);
        private readonly AudioStreamer audioStreamer = new();
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDirect3DDevice? _winrtDevice;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private GraphicsCaptureItem? _captureItem;
        private byte[]? _pixelBuffer;

        private ID3D11Texture2D? _stagingTexture;
        private ID3D11Texture2D StagingTexture
        {
            get
            {
                if (this._stagingTexture == null)
                {
                    this.InitializeStagingTexture();
                }

                return this._stagingTexture!;
            }
            set
            {
                this._stagingTexture = value;
            }
        }

        private CancellationTokenSource? _renderCts;
        private readonly object _textureLock = new();
        private bool _hasFirstFrame = false;

        public MainWindow()
        {
            this.InitializeComponent();
        }

        public async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;

            this.InitializeCapture(hwnd);

            this.audioStreamer.Start();

            this.ffmpeg.Spawn(
                width: this._captureItem!.Size.Width,
                height: this._captureItem!.Size.Height,
                cropArea: Win32Interops.GetClientCropArea(hwnd));

            this.StartCapture();

            this.StartStreamPushLoop();

            await Task.Delay(1000); // Wait for a second to ensure everything is set up

            await AdbUtils.LaunchVlcStreamAsync(this.ffmpeg.FFmpegPort);
        }

        public void Window_Closed(object sender, EventArgs e)
        {
            this.Dispose();
        }

        private void InitializeStagingTexture()
        {
            if (this._captureItem == null) throw new InvalidOperationException("Capture item is not initialized.");
            if (this._device == null) throw new InvalidOperationException("D3D11 device is not initialized.");

            var desc = new Texture2DDescription
            {
                Width = (uint)this._captureItem.Size.Width,
                Height = (uint)this._captureItem.Size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };

            this.StagingTexture = this._device.CreateTexture2D(desc);
        }

        private void InitializeCapture(IntPtr hwnd)
        {
            D3D11.D3D11CreateDevice(
                null,
                Vortice.Direct3D.DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [],
                out var device,
                out ID3D11DeviceContext? context);

            this._device = device;
            this._context = context;

            var winrtDevice = Direct3D11Helper.CreateDirect3DDevice(device);

            this._winrtDevice = winrtDevice;

            var item = CaptureHelper.CreateItemForWindow(hwnd);

            this._captureItem = item;

            var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: 2,
                item.Size);

            this._framePool = framePool;

            var session = framePool.CreateCaptureSession(item);

            this._session = session;

            session.IsBorderRequired = false;
            session.IsCursorCaptureEnabled = false;

            framePool.FrameArrived += this.OnFrameArrived;
        }

        private void StartCapture()
        {
            if (this._session == null) throw new InvalidOperationException("Capture session is not initialized.");

            this._session.StartCapture();
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (this._context == null || this._captureItem == null) return;

            using var frame = this._framePool?.TryGetNextFrame();

            if (frame == null) return;

            using var frameTexture = Direct3D11Helper.CreateTexture2DFromSurface(frame.Surface);

            lock (this._textureLock)
            {
                this._context.CopyResource(this.StagingTexture, frameTexture);
                this._hasFirstFrame = true;
            }
        }

        private void StartStreamPushLoop()
        {
            if (this._context == null || this._captureItem == null) throw new InvalidOperationException("Capture context or item is not initialized.");

            var width = this._captureItem.Size.Width;
            var height = this._captureItem.Size.Height;
            var totalBytes = width * height * 4; // Assuming 4 bytes per pixel (BGRA)

            this._pixelBuffer = new byte[totalBytes];

            this._renderCts = new CancellationTokenSource();
            var token = this._renderCts.Token;

            Task.Run(async () =>
            {
                var timespan = TimeSpan.FromMilliseconds(1000.0 / 30.0);
                using var timer = new PeriodicTimer(timespan);

                while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync())
                {
                    if (!this._hasFirstFrame)
                    {
                        continue;
                    }

                    this.PushFrameSync(width, height, totalBytes);
                }
            });
        }

        private void PushFrameSync(int width, int height, int totalBytes)
        {
            if (this._context == null || this._pixelBuffer == null) return;

            var expectedRowBytesSize = width * 4;
            var copied = false;

            lock (this._textureLock)
            {
                var mapped = this._context.Map(this.StagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                try
                {
                    if (mapped.RowPitch == expectedRowBytesSize)
                    {
                        var totalBytesSize = expectedRowBytesSize * height;

                        mapped.AsSpan<byte>(totalBytesSize).CopyTo(this._pixelBuffer);
                    }
                    else
                    {
                        var fullSpan = mapped.AsSpan((int)mapped.RowPitch * height);

                        for (var y = 0; y < height; y++)
                        {
                            // 各行の先頭のオフセットを計算する
                            // すべての行が一次元のバイト列になっているので
                            var rowStart = y * (int)mapped.RowPitch;

                            var src = fullSpan.Slice(rowStart, expectedRowBytesSize);
                            var dest = this._pixelBuffer.AsSpan(y * expectedRowBytesSize, expectedRowBytesSize);
                            src.CopyTo(dest);
                        }
                    }

                    copied = true;
                }
                catch
                {
                    // Handle exceptions if necessary

                    if (!this.ffmpeg.GetIsAlive())
                    {
                        this.audioStreamer.ResetPipeServer();
                        this.ffmpeg.Spawn();
                    }
                }
                finally
                {
                    this._context.Unmap(this.StagingTexture, 0);
                }
            }

            if (copied)
            {
                try
                {
                    this.ffmpeg.StandardInputStream.Write(this._pixelBuffer, 0, totalBytes);
                }
                catch
                {
                    if (!this.ffmpeg.GetIsAlive())
                    {
                        this.audioStreamer.ResetPipeServer();
                        this.ffmpeg.Spawn();
                    }
                }
            }
        }

        public void Dispose(bool disposing)
        {
            if (disposing)
            {
                this._renderCts?.Cancel();
                this.ffmpeg.Dispose();
                this.audioStreamer.Dispose();
                this._device?.Dispose();
                this._context?.Dispose();
                this._winrtDevice?.Dispose();
                this._framePool?.Dispose();
                this._session?.Dispose();
            }
        }

        public void Dispose()
        {
            this.Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
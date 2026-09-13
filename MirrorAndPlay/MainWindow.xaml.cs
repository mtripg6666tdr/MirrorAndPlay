using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
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
        private const int TargetWidth = 960;
        private const int TargetHeight = 540;
        private readonly int Port;
        private readonly FFmpegProcess ffmpeg = new(AudioStreamer.PipeName);
        private readonly AudioStreamer audioStreamer = new();
        private readonly HttpServer httpServer;

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDirect3DDevice? _winrtDevice;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private GraphicsCaptureItem? _captureItem;

        private GpuScaler? _gpuScaler;
        private ID3D11Texture2D? _latestFrameTexture;
        private Win32Interops.ClientCropArea _cropArea;
        private CancellationTokenSource? _cts;
        private readonly object _textureLock = new();
        private bool _hasFirstFrame = false;

        public MainWindow()
        {
            this.InitializeComponent();

            this.Port = 8912;
            this.httpServer = new(this.Port, this.ffmpeg);
        }

        public async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (this.webView.CoreWebView2 == null)
            {
                this.webView.CoreWebView2InitializationCompleted += (s, args) =>
                {
                    if (args.IsSuccess)
                    {
                        this.InitializeAutoSkipper();
                    }
                };
            }
            else
            {
                this.InitializeAutoSkipper();
            }

                var hwnd = new WindowInteropHelper(this).Handle;

            this.InitializeCapture(hwnd);

            this._gpuScaler = new(this._device!, TargetWidth, TargetHeight);

            this.audioStreamer.Start();

            this._cropArea = Win32Interops.GetClientCropArea(hwnd);

            this.ffmpeg.Spawn(width: this._gpuScaler.OutputWidth, height: this._gpuScaler.OutputHeight);

            this.httpServer.Start();

            this.StartCapture();

            await Task.Delay(1000);

            this.StartStreamPushLoop();

            await Task.Delay(1000);

            await AdbUtils.LaunchVlcStreamAsync(this.Port);
        }

        public void Window_Closed(object sender, EventArgs e) => this.Dispose();

        private async void InitializeAutoSkipper()
        {
#if DEBUG_WITH_DEVTOOLS
            this.webView.CoreWebView2.OpenDevToolsWindow();
#endif

            const int tickDelayMs = 200;

            var token = this._cts?.Token;

            var wasAdPlaying = false;
            var skipAdRestCount = -1;

            while (true)
            {
                if (token?.IsCancellationRequested == true)
                {
                    break;
                }

                await Task.Delay(tickDelayMs);

                if (skipAdRestCount >= 0)
                {
                    skipAdRestCount--;
                    if (skipAdRestCount <= 0)
                    {
                        System.Diagnostics.Debug.WriteLine("[AutoSkipper] Skipping ad...");

                        var rawResult = (await this.webView.CoreWebView2.ExecuteScriptAsync(@"
(function(){
    const button = document.querySelector(
        "".videoAdUiSkipButton, "" +
        "".ytp-ad-skip-button.ytp-button, "" +
        "".ytp-ad-skip-button-modern.ytp-button, "" +
        "".ytp-skip-ad-button""
    );

    const rect = button.getBoundingClientRect();

    const posX = Math.round(rect.left + rect.width / 2);
    const posY = Math.round(rect.top + rect.height / 2);

    return `${posX}:${posY}`;
})()
"));
                        var result = rawResult.Substring(1, rawResult.Length - 2).Split(':');
                        var posX = result[0];
                        var posY = result[1];

                        System.Diagnostics.Debug.WriteLine("[AutoSkipper] Clicking skip button at position: " + posX + ", " + posY);

                        await this.webView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                            "Input.dispatchMouseEvent",
                            $@"{{""type"":""mousePressed"",""x"":{posX},""y"":{posY},""button"":""left"",""clickCount"":1}}");

                        await this.webView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                            "Input.dispatchMouseEvent",
                            $@"{{""type"":""mouseReleased"",""x"":{posX},""y"":{posY},""button"":""left""}}");

                        skipAdRestCount = -1;
                    }
                }

                var isAdPlaying = await this.webView.CoreWebView2.ExecuteScriptAsync(@"
(function(){
    const ad =
            document.querySelector("".ytp-ad-visit-advertiser-button"") ||
            document.querySelector("".ytp-visit-advertiser-link"") ||
            document.querySelector("".ytp-ad-badge"");

    const isPlaying = !!ad;

    return isPlaying ? ""1"" : ""0"";
})()
") == @"""1""";

                if (isAdPlaying && !wasAdPlaying)
                {
                    System.Diagnostics.Debug.WriteLine("[AutoSkipper] Ad detected, waiting 5.0 seconds before skipping...");
                    skipAdRestCount = 5000 / tickDelayMs; // 5秒待機
                }

                if (!isAdPlaying && wasAdPlaying)
                {
                    System.Diagnostics.Debug.WriteLine("[AutoSkipper] Ad finished.");
                    skipAdRestCount = -1;
                }

                wasAdPlaying = isAdPlaying;
            }
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

            var desc = new Texture2DDescription
            {
                Width = (uint)item.Size.Width,
                Height = (uint)item.Size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            };
            this._latestFrameTexture = device.CreateTexture2D(desc);

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
                this._context.CopyResource(this._latestFrameTexture, frameTexture);
                this._hasFirstFrame = true;
            }
        }

        private void StartStreamPushLoop()
        {
            this._cts = new CancellationTokenSource();
            var token = this._cts.Token;

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

                    this.PushFrameSync();
                }
            });
        }

        private void PushFrameSync()
        {
            if (this._gpuScaler == null || this._latestFrameTexture == null) return;

            var cropRect = new Rectangle(this._cropArea.X, this._cropArea.Y, this._cropArea.Width, this._cropArea.Height);

            try
            {
                lock (this._textureLock)
                {
                    this._gpuScaler.ProcessFrame(
                        this._latestFrameTexture,
                        cropRect,
                        this.ffmpeg.StandardInputStream);
                }
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

        public void Dispose(bool disposing)
        {
            if (disposing)
            {
                this._cts?.Cancel();
                this.ffmpeg.Dispose();
                this.audioStreamer.Dispose();
                this.httpServer.Dispose();
                this._gpuScaler?.Dispose();
                this._latestFrameTexture?.Dispose();
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
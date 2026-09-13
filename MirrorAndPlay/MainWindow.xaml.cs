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
        private readonly FFmpegProcess ffmpeg = new(AudioStreamer.PipeName);
        private readonly AudioStreamer audioStreamer = new();

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDirect3DDevice? _winrtDevice;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private GraphicsCaptureItem? _captureItem;

        private GpuScaler? _gpuScaler;
        private ID3D11Texture2D? _latestFrameTexture;
        private Win32Interops.ClientCropArea _cropArea;
        private CancellationTokenSource? _renderCts;
        private readonly object _textureLock = new();
        private bool _hasFirstFrame = false;

        public MainWindow()
        {
            this.InitializeComponent();
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

            this.StartCapture();
            this.StartStreamPushLoop();

            await Task.Delay(1000); // Wait for a second to ensure everything is set up

            await AdbUtils.LaunchVlcStreamAsync(this.ffmpeg.FFmpegPort);
        }

        public void Window_Closed(object sender, EventArgs e) => this.Dispose();

        private void InitializeAutoSkipper()
        {
            const string AdSkipperScript = @"
(function() {
    if (window.top !== window) {
        return;
    }

    console.log(""[AutoSkipper] injected"", location.href);

    const SKIP_AFTER = 5000;

    let timer = null;
    let wasAdPlaying = false;

    setInterval(() => {
      const ad =
        document.querySelector("".ytp-ad-visit-advertiser-button"") ||
        document.querySelector("".ytp-visit-advertiser-link"") ||
        document.querySelector("".ytp-ad-badge"");

      const isAdPlaying = !!ad;

      if (isAdPlaying && !wasAdPlaying) {
        // 広告開始
        console.log(""[AutoSkipper] ad started"", location.href);
        timer = setTimeout(() => {
          console.log(""[AutoSkipper] skipping ad"", location.href);
          const buttons = document.querySelectorAll(
            "".videoAdUiSkipButton, "" +
            "".ytp-ad-skip-button.ytp-button, "" +
            "".ytp-ad-skip-button-modern.ytp-button, "" +
            "".ytp-skip-ad-button""
          );

          console.log(""[AutoSkipper] found skip buttons"", buttons);

          buttons.forEach((button) => {
            const evObj = document.createEvent(""Events"");
            evObj.initEvent(""click"", true, false);
            button.dispatchEvent(evObj);
          });
        }, SKIP_AFTER + 200);
      }

      if (!isAdPlaying && wasAdPlaying) {
        // 広告終了
        console.log(""[AutoSkipper] ad ended"", location.href);
        clearTimeout(timer);
        timer = null;
      }

      wasAdPlaying = isAdPlaying;
    }, 200);
})();
";

            _ = this.webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(AdSkipperScript);
            _ = this.webView.CoreWebView2.ExecuteScriptAsync(AdSkipperScript);

            this.webView.CoreWebView2.OpenDevToolsWindow();
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
                this._renderCts?.Cancel();
                this.ffmpeg.Dispose();
                this.audioStreamer.Dispose();
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
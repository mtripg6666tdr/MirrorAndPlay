using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MirrorAndPlay
{
    internal class FFmpegUtils(string _audioStreamerPipename, int port = 8912) : IDisposable
    {
        public readonly int FFmpegPort = port;
        private readonly string _audioStreamerPipeName = _audioStreamerPipename;
        public Process? FFmpegProcess { get; private set; }

        public Stream StandardInputStream => this.FFmpegProcess?.StandardInput.BaseStream ?? throw new InvalidOperationException("FFmpeg process is not started.");

        private int? _width;
        private int? _height;
        private Win32Interops.ClientCropArea? _cropArea;
        private int? _sampleRate;
        private int? _channels;

        public FFmpegUtils Spawn()
        {
            if (!this._width.HasValue || !this._height.HasValue || !this._cropArea.HasValue)
            {
                throw new InvalidOperationException("Width, height, and crop area must be set before spawning FFmpeg.");
            }

            return this.Spawn(this._width.Value, this._height.Value, this._cropArea.Value, this._sampleRate, this._channels);
        }

        public FFmpegUtils Spawn(int width, int height, Win32Interops.ClientCropArea cropArea, int? _sampleRate = null, int? _channels = null)
        {
            this._width = width;
            this._height = height;
            this._cropArea = cropArea;
            var sampleRate = this._sampleRate ??= _sampleRate ?? 48000;
            var channels = this._channels ??= _channels ?? 2;

            this.FFmpegProcess?.Dispose();

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg.exe",
                Arguments = $"-hide_banner " +
                            $"-thread_queue_size 1024 " +
                            $"-f rawvideo -pixel_format bgra -video_size {width}x{height} -framerate 30 -i pipe:0 " +
                            $"-thread_queue_size 1024 " +
                            $"-f f32le -ar {sampleRate} -ac {channels} -i \"\\\\.\\pipe\\{this._audioStreamerPipeName}\" " +
                            "-c:v h264_qsv -preset veryfast -profile:v baseline -level 3.1 " +
                            "-async_depth 1 -look_ahead 0 -flags +low_delay " +
                            $"-vf \"crop={cropArea.Width}:{cropArea.Height}:{cropArea.X}:{cropArea.Y},scale=960:540,format=nv12\" " +
                            $"-b:v 1500k -maxrate 1800k -bufsize 3000k -r 30 -g 30 " +
                            $"-c:a aac -b:a 128k -ar 48000 -ac 2 " +
                            $"-max_muxing_queue_size 1024 " +
                            $"-muxdelay 0.1 -f mpegts -listen 1 http://0.0.0.0:{FFmpegPort}/live",
                UseShellExecute = false,
                RedirectStandardInput = true,
#if DEBUG
                RedirectStandardError = true,
#endif
                CreateNoWindow = true
            };

            var proc = Process.Start(psi)!;

            this.FFmpegProcess = proc;

#if DEBUG
            Task.Run(() =>
            {
                string? line;
                while ((line = this.FFmpegProcess.StandardError.ReadLine()) != null)
                {
                    Debug.WriteLine(line);
                }
            });
#endif

            proc.Exited += this.OnProcessExited;

            return this;
        }

        public bool GetIsAlive()
        {
            return this.FFmpegProcess != null && !this.FFmpegProcess.HasExited;
        }

        public void Kill()
        {
            this.FFmpegProcess?.Kill();
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            this.FFmpegProcess?.Dispose();
            this.FFmpegProcess = null;
        }

        public void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.Kill();
                this.FFmpegProcess?.Dispose();
            }
        }

        public void Dispose()
        {
            this.Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}

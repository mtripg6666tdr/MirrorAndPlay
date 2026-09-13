using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MirrorAndPlay
{
    internal class FFmpegProcess(string _audioStreamerPipename, int port = 8912) : IDisposable
    {
        public readonly int FFmpegPort = port;
        private readonly string _audioStreamerPipeName = _audioStreamerPipename;
        public Process? FFmpegInternalProcess { get; private set; }

        public Stream StandardInputStream => this.FFmpegInternalProcess?.StandardInput.BaseStream ?? throw new InvalidOperationException("FFmpeg process is not started.");

        private int? _width;
        private int? _height;
        private int? _sampleRate;
        private int? _channels;

        public FFmpegProcess Spawn()
        {
            if (!this._width.HasValue || !this._height.HasValue)
            {
                throw new InvalidOperationException("Width, height, and crop area must be set before spawning FFmpeg.");
            }

            return this.Spawn(this._width.Value, this._height.Value, this._sampleRate, this._channels);
        }

        public FFmpegProcess Spawn(int width, int height, int? _sampleRate = null, int? _channels = null)
        {
            this._width = width;
            this._height = height;
            var sampleRate = this._sampleRate ??= _sampleRate ?? 48000;
            var channels = this._channels ??= _channels ?? 2;

            this.FFmpegInternalProcess?.Dispose();

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
                            $"-vf \"format=nv12\" " +
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

            this.FFmpegInternalProcess = proc;

#if DEBUG
            Task.Run(() =>
            {
                string? line;
                while ((line = this.FFmpegInternalProcess.StandardError.ReadLine()) != null)
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
            return this.FFmpegInternalProcess != null && !this.FFmpegInternalProcess.HasExited;
        }

        public void Kill()
        {
            this.FFmpegInternalProcess?.Kill();
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            this.FFmpegInternalProcess?.Dispose();
            this.FFmpegInternalProcess = null;
        }

        public void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.Kill();
                this.FFmpegInternalProcess?.Dispose();
            }
        }

        public void Dispose()
        {
            this.Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}

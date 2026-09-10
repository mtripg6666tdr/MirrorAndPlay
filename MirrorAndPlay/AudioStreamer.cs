using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MirrorAndPlay
{
    internal class AudioStreamer : IDisposable
    {
        public const string PipeName = "mirror_audio_pipe";
        private NamedPipeServerStream? _pipeServer;
        private WasapiLoopbackCapture? _capture;
        private WasapiOut? _silenceOut; // 無音時フリーズ防止用
        private CancellationTokenSource? _cts;

        public int SampleRate { get; private set; } = 48000;
        public int Channels { get; private set; } = 2;

        public string AudioFormat { get; private set; } = "f32le";

        public void Start()
        {
            // 1. Windowsに「音が出ている」と錯覚させ、無音時でもパケットを途切れさせない
            this._capture = new WasapiLoopbackCapture();
            var waveFormat = this._capture.WaveFormat;
            this.SampleRate = waveFormat.SampleRate;
            this.Channels = waveFormat.Channels;

            if (waveFormat.BitsPerSample == 16)
            {
                this.AudioFormat = "s16le";
            }
            else
            {
                // WASAPI標準の IEEE Float 32bit
                this.AudioFormat = "f32le";
            }

            Debug.WriteLine($"[Audio] Format: {this.AudioFormat}, SampleRate: {this.SampleRate}Hz, Channels: {this.Channels}ch, Bits: {waveFormat.BitsPerSample}bit");

            var silenceProvider = new SilenceProvider(this._capture.WaveFormat);
            this._silenceOut = new WasapiOut(AudioClientShareMode.Shared, 100);
            this._silenceOut.Init(silenceProvider);
            this._silenceOut.Play();

            // 2. FFmpegと繋ぐ名前付きパイプを作成
            this.ResetPipeServer();
        }

        private void StartFFmpegPipeServer()
        {
            this._cts = new CancellationTokenSource();

            // 3. 非同期でFFmpegの接続を待ち受け
            Task.Run(async () =>
            {
                try
                {
                    Debug.WriteLine("[Audio] Waiting for connection from FFmpeg...");
                    await this._pipeServer!.WaitForConnectionAsync(this._cts.Token);
                    Debug.WriteLine("[Audio] ★FFmpeg connected！");

                    this._capture!.DataAvailable += this.CaptureDataAvailable;

                    if (this._capture.CaptureState == CaptureState.Stopped)
                    {
                        this._capture.StartRecording();
                        Debug.WriteLine("[Audio] WASAPI recording started!");
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Audio] Exception occurred: {ex.Message}");
                }
            });
        }

        private void CaptureDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (this._pipeServer?.IsConnected == true && e.BytesRecorded > 0)
            {
                try
                {
                    this._pipeServer.Write(e.Buffer, 0, e.BytesRecorded);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Audio] Error while writing chunk to the pipe: {ex.Message}");
                }
            }
        }

        public void ResetPipeServer()
        {
            this._cts?.Cancel();

            if (this._capture != null)
            {
                this._capture.DataAvailable -= this.CaptureDataAvailable;
            }

            this._pipeServer?.Dispose();
            this._pipeServer = new NamedPipeServerStream(
                PipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                65536,
                65536);
            this.StartFFmpegPipeServer();
        }

        public void Dispose()
        {
            this._cts?.Cancel();
            this._capture?.StopRecording();
            this._capture?.Dispose();
            this._silenceOut?.Stop();
            this._silenceOut?.Dispose();
            this._pipeServer?.Dispose();
        }
    }
}
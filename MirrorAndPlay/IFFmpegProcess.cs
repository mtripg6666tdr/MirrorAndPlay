using System.IO;

namespace MirrorAndPlay
{
    public interface IFFmpegProcess
    {
        Stream StandardInputStream { get; }
        Stream StandardOutputStream { get; }
    }
}

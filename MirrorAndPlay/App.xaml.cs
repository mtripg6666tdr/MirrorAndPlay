using System.Diagnostics;
using System.Windows;
using Meziantou.Framework.Win32;

namespace MirrorAndPlay
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        static readonly JobObject job;

        static App()
        {
            job = new JobObject();

            job.SetLimits(new()
            {
                Flags = JobObjectLimitFlags.KillOnJobClose,
            });

            job.AssignProcess(Process.GetCurrentProcess());
        }
    }

}

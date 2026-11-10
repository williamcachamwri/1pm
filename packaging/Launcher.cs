using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace OnePMLauncher
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string targetExe = Path.Combine(baseDir, "bin", "OnePM.exe");
                if (!File.Exists(targetExe)) targetExe = Path.Combine(baseDir, "bin", "1pm.exe");

                if (!File.Exists(targetExe))
                {
                    MessageBox.Show(
                        "Cannot find 1pm binary in 'bin\\OnePM.exe'.\nPlease ensure all extracted files remain in the package.",
                        "1pm - Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = targetExe,
                    Arguments = string.Join(" ", args),
                    WorkingDirectory = Path.GetDirectoryName(targetExe),
                    UseShellExecute = true
                };

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message, "1pm", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;

namespace OnePMSingleFile
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string installDir = Path.Combine(localApp, "1pm");
                string targetExe = Path.Combine(installDir, "bin", "OnePM.exe");
                string markerFile = Path.Combine(installDir, "build.marker");

                Assembly assembly = Assembly.GetExecutingAssembly();
                long currentExeLength = new FileInfo(assembly.Location).Length;
                string currentSig = currentExeLength.ToString();

                bool needExtract = !File.Exists(targetExe) || !File.Exists(markerFile);
                if (!needExtract)
                {
                    try
                    {
                        if (File.ReadAllText(markerFile).Trim() != currentSig)
                        {
                            needExtract = true;
                        }
                    }
                    catch
                    {
                        needExtract = true;
                    }
                }

                if (needExtract)
                {
                    if (Directory.Exists(installDir))
                    {
                        try { Directory.Delete(installDir, true); } catch { }
                    }
                    Directory.CreateDirectory(installDir);

                    using (Stream stream = assembly.GetManifestResourceStream("payload.zip"))
                    {
                        if (stream == null)
                        {
                            MessageBox.Show("Internal error: embedded payload resource not found.", "1pm - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        using (ZipArchive archive = new ZipArchive(stream))
                        {
                            archive.ExtractToDirectory(installDir);
                        }
                    }

                    try { File.WriteAllText(markerFile, currentSig); } catch { }
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
                MessageBox.Show("Failed to launch 1pm: " + ex.Message, "1pm - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}

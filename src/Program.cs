using System;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("PC OnSong Bridge")]
[assembly: AssemblyProduct("PC OnSong Bridge")]
[assembly: AssemblyDescription("Play OnSong tablet cues through a Windows audio output over Wi-Fi or USB")]
[assembly: AssemblyCompany("PC OnSong Bridge contributors")]

namespace PcOnSongBridge
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length >= 1 && args[0] == "--setup-features")
            {
                bool open = args.Length == 2 && args[1] == "--open";
                using (var chooser = new FeatureSetupForm(open))
                    if (chooser.ShowDialog() != DialogResult.OK || !open) return;
            }
            if (args.Length == 1 && args[0] == "--test-microphone")
            {
                using (var test = new MicrophoneTest(null, Preferences.Load().InputName))
                {
                    test.Text += " · v" + BridgeRelease.Version;
                    Application.Run(test);
                }
                return;
            }
            Application.Run(new MainForm());
        }
    }
}

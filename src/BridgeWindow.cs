using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PcOnSongBridge
{
    internal abstract class BridgeWindow : Form
    {
        // Reuse one multi-resolution icon across windows for the lifetime of the app.
        private static readonly Icon ProductIcon = LoadIcon();

        protected BridgeWindow()
        {
            if (ProductIcon != null) Icon = ProductIcon;
        }

        private static Icon LoadIcon()
        {
            var assembly = typeof(BridgeWindow).Assembly;
            using (Stream resource = assembly.GetManifestResourceStream("Bridge.ico"))
            {
                if (resource != null) return new Icon(resource);
            }
            try { return Icon.ExtractAssociatedIcon(assembly.Location); }
            catch (ArgumentException) { return null; }
        }
    }
}

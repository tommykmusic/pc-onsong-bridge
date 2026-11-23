using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace PcOnSongBridge
{
    internal sealed class AboutForm : BridgeWindow
    {
        internal AboutForm(Color background, Color foreground, Color surface)
        {
            Text = "About PC OnSong Bridge";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(18);
            BackColor = background;
            ForeColor = foreground;
            Font = new Font("Segoe UI", 10);

            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
            Controls.Add(panel);
            panel.Controls.Add(new Label {
                Text = "PC OnSong Bridge", AutoSize = true, Font = new Font(Font.FontFamily, 16, FontStyle.Bold)
            });
            panel.Controls.Add(new Label {
                Text = "Version " + BridgeRelease.Version + "  ·  MIT License", AutoSize = true,
                Margin = new Padding(3, 3, 3, 15)
            });
            panel.Controls.Add(new Label { Text = "Running executable", AutoSize = true });
            var path = new TextBox {
                ReadOnly = true, AccessibleName = "Running executable", Dock = DockStyle.Fill,
                Text = Assembly.GetExecutingAssembly().Location, BorderStyle = BorderStyle.FixedSingle,
                BackColor = surface, ForeColor = foreground, TabIndex = 1
            };
            panel.Controls.Add(path);
            panel.Controls.Add(new Label {
                Text = "NDI® is a registered trademark of Vizrt NDI AB.",
                AutoSize = true, Margin = new Padding(3, 12, 3, 0)
            });
            var ndi = new LinkLabel { Text = "NDI technology", AutoSize = true,
                LinkColor = Color.DeepSkyBlue, VisitedLinkColor = Color.DeepSkyBlue };
            ndi.LinkClicked += delegate { System.Diagnostics.Process.Start("https://ndi.video/"); };
            panel.Controls.Add(ndi);
            var close = new Button {
                Text = "&Close", DialogResult = DialogResult.OK, AutoSize = true, TabIndex = 0,
                Anchor = AnchorStyles.Right, Margin = new Padding(3, 12, 3, 3),
                BackColor = surface, ForeColor = foreground, FlatStyle = FlatStyle.Flat
            };
            panel.Controls.Add(close);
            AcceptButton = CancelButton = close;
            ActiveControl = close;
        }
    }
}

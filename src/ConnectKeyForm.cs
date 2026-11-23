using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PcOnSongBridge
{
    internal sealed class ConnectKeyForm : Form
    {
        internal ConnectKeyForm(bool configured, ConnectKeyStore keyStore)
        {
            Text = "OnSong developer key";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = MinimizeBox = MaximizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, Padding = new Padding(16) };
            var description = new Label { AutoSize = true, MaximumSize = new Size(390, 0),
                Text = configured ? "A key is configured. Paste a replacement to update it for this Windows account." :
                    "Paste the application key from OnSong’s developer portal. Windows protects the saved key for this account." };
            var field = new TextBox { Width = 390, UseSystemPasswordChar = true, AccessibleName = "OnSong developer key" };
            var portal = new LinkLabel { Text = "Open OnSong’s developer portal", AutoSize = true };
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            var save = new Button { Text = "Save key", Enabled = false, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var feedback = new Label { AutoSize = true, MaximumSize = new Size(390, 0), AccessibleRole = AccessibleRole.StatusBar };
            field.TextChanged += delegate
            {
                save.Enabled = ConnectKeyStore.IsValid(field.Text.Trim());
                feedback.Text = field.TextLength > 0 && !save.Enabled ? "Paste the key without spaces or line breaks." : "";
            };
            portal.LinkClicked += delegate
            {
                try { Process.Start(new ProcessStartInfo("https://onsongapp.com/developers/licensing/") { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception) { feedback.Text = "Open onsongapp.com/developers/licensing in your browser."; }
            };
            save.Click += delegate
            {
                try { keyStore.Save(field.Text.Trim()); }
                catch (Exception) { feedback.Text = "Windows could not save this key. Check access to your Windows profile."; return; }
                field.Clear();
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.AddRange(new Control[] { save, cancel });
            panel.Controls.AddRange(new Control[] { description, field, portal, feedback, buttons });
            Controls.Add(panel);
            CancelButton = cancel;
            AcceptButton = save;
            ActiveControl = field;
        }
    }
}

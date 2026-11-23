using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace PcOnSongBridge
{
    internal sealed class SongViewer
    {
        private const string Package = "com.onsongapp.app";
        private const string ViewerHint = "Open a song full screen in OnSong, dismiss menus and overlays, then try again.";
        internal sealed class LayoutUnavailableException : InvalidOperationException
        {
            public LayoutUnavailableException() : base(ViewerHint) { }
        }
        private static readonly Regex Bounds = new Regex(@"^\[([0-9]{1,5}),([0-9]{1,5})\]\[([0-9]{1,5}),([0-9]{1,5})\]$");
        private readonly Point start, end;
        private readonly int rotation, sheetIndex;
        private readonly bool vertical;
        private readonly Rectangle rootBounds;
        internal string Identity { get; private set; }

        private SongViewer(XmlElement root, XmlElement sheet, XmlElement chart, int rotation, bool vertical)
        {
            this.rotation = rotation;
            this.vertical = vertical;
            rootBounds = ReadBounds(root);
            Rectangle area = ReadBounds(sheet);
            if (!Int32.TryParse(sheet.GetAttribute("resource-id").Substring("_sheet_".Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out sheetIndex)) throw LayoutUnavailable();
            Identity = sheetIndex.ToString(CultureInfo.InvariantCulture) + ":" + chart.GetAttribute("resource-id").Substring(1);
            start = new Point(area.Left + Offset(area.Width, vertical ? 50 : 28), area.Top + Offset(area.Height, vertical ? 28 : 50));
            end = new Point(area.Left + Offset(area.Width, vertical ? 50 : 72), area.Top + Offset(area.Height, vertical ? 72 : 50));
            if ((vertical ? end.Y - start.Y : end.X - start.X) < 48) throw LayoutUnavailable();

            // Ancestors clip the page. A visible sibling intersecting this path may be a menu or overlay.
            for (XmlElement node = chart; node != null && node.Name == "node"; node = node.ParentNode as XmlElement)
            {
                Rectangle clip = ReadBounds(node);
                if (node.GetAttribute("package") != Package || node.GetAttribute("visible-to-user") == "false"
                    || !clip.Contains(start) || !clip.Contains(end)) throw LayoutUnavailable();
                foreach (XmlElement sibling in node.ParentNode.SelectNodes("node"))
                    if (sibling != node && IntersectsSwipe(sibling)) throw LayoutUnavailable();
            }
            foreach (XmlElement control in chart.SelectNodes(".//node[@clickable='true' or @long-clickable='true' or @scrollable='true' or @class='android.widget.EditText']"))
                if (IntersectsSwipe(control)) throw LayoutUnavailable();
        }

        private static XmlElement ReadHierarchy(string dump)
        {
            int start = dump.IndexOf("<hierarchy", StringComparison.Ordinal);
            int end = dump.IndexOf("</hierarchy>", StringComparison.Ordinal);
            if (start < 0 || end < start || dump.IndexOf("<hierarchy", start + 1, StringComparison.Ordinal) >= 0)
                throw LayoutUnavailable();
            var doc = new XmlDocument { XmlResolver = null };
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 };
                using (var text = new StringReader(dump.Substring(start, end + "</hierarchy>".Length - start)))
                using (var reader = XmlReader.Create(text, settings)) doc.Load(reader);
            }
            catch (XmlException) { throw LayoutUnavailable(); } // Raw layout can include song text.
            return doc.DocumentElement;
        }

        internal static bool IsShortcutEditor(string dump)
        {
            try
            {
                XmlElement root = Only(ReadHierarchy(dump), "node");
                if (root.GetAttribute("package") != Package || root.SelectSingleNode(
                    ".//node[@class='android.webkit.WebView' or @class='android.widget.EditText' or @text='Choose Action' or @package!='" + Package + "']") != null) return false;
                // This native modal contains a title, shortcut instructions and an Actions Editor tab bar.
                // The song, other tabs and the action picker each have a different accessibility tree.
                foreach (string label in new[] { "Shortcuts", "SYSTEM DEFINED", "Press and hold keys to create new shortcuts", "Pedals", "MIDI", "Screen", "Voice" })
                {
                    int visible = 0;
                    foreach (XmlElement item in root.SelectNodes(".//node[@class='android.widget.TextView' and @text='" + label + "']"))
                    {
                        Rectangle box = ReadBounds(item);
                        if (box.Width > 0 && box.Height > 0 && item.GetAttribute("visible-to-user") != "false")
                            visible++;
                    }
                    if (visible < (label == "Shortcuts" ? 2 : 1)) return false;
                }
                return true;
            }
            catch (LayoutUnavailableException) { return false; }
        }

        internal static SongViewer Read(string dump, bool vertical = false)
        {
            XmlElement hierarchy = ReadHierarchy(dump);
            int rotation;
            if (hierarchy.Name != "hierarchy" || !Int32.TryParse(hierarchy.GetAttribute("rotation"), out rotation)
                || rotation < 0 || rotation > 3) throw LayoutUnavailable();
            XmlElement root = Only(hierarchy, "node");
            XmlElement viewer = Only(root, "descendant-or-self::node[@resource-id='viewer']");
            XmlElement scroller = Only(viewer, ".//node[@resource-id='R2-Scroller']");
            XmlElement sheet = null;
            foreach (XmlElement page in scroller.SelectNodes(".//node[starts-with(@resource-id,'_sheet_')]"))
            {
                Rectangle bounds = ReadBounds(page);
                if (bounds.Width == 0 || bounds.Height == 0) continue;
                if (sheet != null || !Regex.IsMatch(page.GetAttribute("resource-id"), @"^_sheet_[0-9]+$"))
                    throw LayoutUnavailable();
                sheet = page;
            }
            if (sheet == null) throw LayoutUnavailable();
            XmlElement chart = null;
            foreach (XmlElement child in sheet.SelectNodes("node"))
            {
                string id = child.GetAttribute("resource-id");
                Guid guid;
                if (!id.StartsWith("_", StringComparison.Ordinal) || !Guid.TryParseExact(id.Substring(1), "D", out guid)) continue;
                if (chart != null) throw LayoutUnavailable();
                chart = child;
            }
            if (chart == null) throw LayoutUnavailable();
            return new SongViewer(root, sheet, chart, rotation, vertical);
        }

        internal Size CheckCurrentDisplay(string windows)
        {
            // Android's accessibility bounds and input swipe are both in logical, rotated display coordinates.
            // Compare with the current display immediately before dispatch. Applying another transform is incorrect.
            MatchCollection displays = Regex.Matches(windows, @"(?m)^[ \t]*Display: mDisplayId=([0-9]+)\b");
            if (!Regex.IsMatch(windows, @"(?m)^[ \t]*mTopFocusedDisplayId=0\s*$")) throw DisplayChanged();
            for (int i = 0; i < displays.Count; i++)
            {
                if (displays[i].Groups[1].Value != "0") continue;
                int start = displays[i].Index;
                int end = i + 1 < displays.Count ? displays[i + 1].Index : windows.Length;
                string display = windows.Substring(start, end - start);
                Match size = Regex.Match(display, @"(?m)^[ \t]*init=[0-9]+x[0-9]+[^\r\n]*?\bcur=([0-9]{1,5})x([0-9]{1,5})\b");
                MatchCollection angles = Regex.Matches(display, @"(?m)^[ \t]*mRotation=([0-3])\b");
                if (!size.Success || angles.Count != 1 || Number(angles[0], 1) != rotation
                    || rootBounds.Right > Number(size, 1) || rootBounds.Bottom > Number(size, 2)) throw DisplayChanged();
                return new Size(Number(size, 1), Number(size, 2));
            }
            throw DisplayChanged();
        }

        internal string Gesture(string action)
        {
            if (vertical ? action != "scroll-down" && action != "scroll-up" : action != "next" && action != "previous")
                throw new ArgumentException("This gesture was not verified for the requested direction.");
            bool forward = action == "next" || action == "scroll-down";
            Point from = forward ? end : start, to = forward ? start : end;
            return String.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} 300", from.X, from.Y, to.X, to.Y);
        }

        internal bool IsAdjacentTo(string before, string direction)
        {
            string[] identity = (before ?? String.Empty).Split(':');
            int previous;
            Guid chart;
            if (identity.Length != 2 || !Int32.TryParse(identity[0], NumberStyles.None, CultureInfo.InvariantCulture, out previous)
                || !Guid.TryParseExact(identity[1], "D", out chart)) throw new ArgumentException("A valid starting chart is required.");
            if (direction != "next" && direction != "previous") throw new ArgumentException("Choose next or previous.");
            // A set may contain the same chart twice. Its position distinguishes those two entries.
            return (long)sheetIndex == (long)previous + (direction == "next" ? 1 : -1);
        }

        private static int Offset(int length, int percent) { return (length * percent + 50) / 100; }
        private bool IntersectsSwipe(XmlElement node)
        {
            Rectangle bounds = ReadBounds(node);
            return bounds.Width > 0 && bounds.Height > 0 && bounds.Left <= end.X && bounds.Right > start.X
                && bounds.Top <= end.Y && bounds.Bottom > start.Y;
        }
        private static XmlElement Only(XmlElement parent, string path)
        {
            XmlNodeList items = parent.SelectNodes(path);
            if (items.Count != 1) throw LayoutUnavailable();
            return (XmlElement)items[0];
        }
        private static int Number(Match match, int group) { return Int32.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture); }
        private static Rectangle ReadBounds(XmlElement node)
        {
            Match bounds = Bounds.Match(node.GetAttribute("bounds"));
            if (!bounds.Success) throw LayoutUnavailable();
            var rect = Rectangle.FromLTRB(Number(bounds, 1), Number(bounds, 2), Number(bounds, 3), Number(bounds, 4));
            if (rect.Width < 0 || rect.Height < 0) throw LayoutUnavailable();
            return rect;
        }
        private static LayoutUnavailableException LayoutUnavailable() { return new LayoutUnavailableException(); }
        private static InvalidOperationException DisplayChanged()
        {
            return new InvalidOperationException("Could not verify the song viewer against the current tablet display. Hold its orientation steady and try again.");
        }
    }
}

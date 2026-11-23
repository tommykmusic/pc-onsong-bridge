using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PcOnSongBridge
{
    internal sealed class OnSongAction
    {
        public readonly string Id, Category, Title, SourceUrl, DefaultShortcut;

        internal OnSongAction(string id, string category, string title, string url, string shortcut = null)
        {
            Id = id;
            Category = category;
            Title = title;
            SourceUrl = url;
            DefaultShortcut = shortcut;
        }
    }

    internal static class OnSongActions
    {
        internal const string MenuSourceUrl = "https://onsongapp.com/docs/2026/interface/menubar/utilities-menu/editors/actions/menu/";
        internal const string KeyboardSourceUrl = "https://onsongapp.com/docs/2026/interface/menubar/utilities-menu/editors/actions/shortcuts/";
        internal const string AndroidSourceUrl = "https://onsongapp.com/docs/platforms/android/";
        internal static readonly OnSongAction[] All = Build();

        // Group and action labels in the official 2026 Actions Menu. Some actions require variable input,
        // configured targets, a subscription, or compatible hardware. Keyboard examples are for Apple devices.
        private static OnSongAction[] Build()
        {
            var rows = new List<OnSongAction>();
            Group(rows, "Top", "top", "", "Default", "None", "Multiple");
            Group(rows, "Navigation", "navigation", "Page", "Adjust", "Up", "Down", "Switch");
            Group(rows, "Navigation", "navigation", "Scroll", "Adjust", "Up", "Down", "Top", "Bottom");
            Group(rows, "Navigation", "navigation", "Section", "Adjust", "Previous", "Next", "Switch", "Goto");
            Group(rows, "Navigation", "navigation", "Set", "Previous", "Next", "Switch");
            Group(rows, "Navigation", "navigation", "Song", "Adjust", "Previous", "Next", "Switch", "Random", "Reset");
            Group(rows, "Navigation", "navigation", "Viewport", "Default", "Previous", "Current", "Next", "Switch", "Primary");
            Group(rows, "Navigation", "navigation", "Zoom", "Adjust", "Toggle", "Actual Size", "In", "Out");
            Group(rows, "Autoscroll", "autoscroll", "Control", "Start", "Stop", "Toggle");
            Group(rows, "Autoscroll", "autoscroll", "Speed", "Adjust", "Increase", "Decrease");
            Group(rows, "Autoscroll", "autoscroll", "Timeline", "Previous", "Next");
            Group(rows, "Backing Track", "audio", "Control", "Play", "Pause", "Stop", "Scrub", "Toggle");
            Group(rows, "Backing Track", "audio", "Pan", "Adjust", "Left", "Center", "Right");
            Group(rows, "Backing Track", "audio", "Volume", "Adjust", "Increase", "Decrease");
            Group(rows, "Device", "device", "Brightness", "Adjust", "Increase", "Decrease");
            Group(rows, "Device", "device", "Volume", "Adjust", "Up", "Down");
            Group(rows, "External Video", "external-video", "Mode", "Lyrics Projection", "Mirroring", "Multiple Outputs", "Stage Monitor");
            Group(rows, "Gears", "gears", "Shift", "First", "Up", "Down");
            Group(rows, "Live Video", "live-video", "Camera", "Toggle", "Front", "Rear");
            Group(rows, "Live Video", "live-video", "Control", "Show", "Hide", "Toggle");
            Group(rows, "Live Video", "live-video", "Effects", "On", "Off", "Toggle");
            Group(rows, "Live Video", "live-video", "Opacity", "Increase", "Decrease");
            Group(rows, "Live Video", "live-video", "Placement", "Left", "Center", "Right", "Top", "Bottom", "Middle");
            Group(rows, "Live Video", "live-video", "Size", "25%", "33%", "50%", "67%", "75%", "100%", "Increase", "Decrease");
            Group(rows, "Lyrics Projection", "lyrics", "Background", "Show", "Hide", "Toggle");
            Group(rows, "Lyrics Projection", "lyrics", "Blackout", "On", "Off", "Toggle");
            Group(rows, "Lyrics Projection", "lyrics", "Lyrics", "Show", "Hide", "Toggle", "Larger", "Smaller");
            Group(rows, "Lyrics Projection", "lyrics", "Media", "Show", "Hide", "Toggle");
            Group(rows, "Lyrics Projection", "lyrics", "Overlay", "Previous", "Next", "Show", "Hide", "Toggle");
            Group(rows, "Lyrics Projection", "lyrics", "Slide", "Previous", "Next");
            Group(rows, "MIDI", "midi", "Chord", "Off", "Previous", "Next", "Repeat", "Vary");
            Group(rows, "MIDI", "midi", "Clock", "Start", "Stop", "Toggle");
            Group(rows, "MIDI", "midi", "Events", "Panic", "Send");
            Group(rows, "MIDI", "midi", "Sync", "Start", "Stop", "Toggle");
            Group(rows, "MIDI", "midi", "Transpose", "Up", "Down");
            Group(rows, "MIDI", "midi", "Velocity", "Adjust");
            Group(rows, "Media", "media", "Control", "Play", "Pause", "Stop", "Toggle");
            Group(rows, "Media", "media", "Fade", "Adjust", "In", "Out");
            Group(rows, "Media", "media", "Opacity", "Adjust", "In", "Out");
            Group(rows, "Media", "media", "Volume", "Adjust", "Up", "Down");
            Group(rows, "Messaging", "messaging", "Message", "Chat", "Send");
            Group(rows, "Messaging", "messaging", "Song", "Previous", "Current", "Next");
            Group(rows, "Metronome", "metronome", "Control", "Start", "Stop", "Toggle");
            Group(rows, "Metronome", "metronome", "Pan", "Adjust", "Left", "Center", "Right");
            Group(rows, "Metronome", "metronome", "Tempo", "Adjust", "Change", "Increase", "Decrease", "Tap", "Tap Downbeat");
            Group(rows, "Metronome", "metronome", "Volume", "Adjust", "Increase", "Decrease", "Mute");
            Group(rows, "Pitch", "pitch", "Control", "Play");
            Group(rows, "Playback", "playback", "Control", "Play", "Pause", "Stop", "Toggle");
            Group(rows, "Scenes", "scenes", "Control", "Play", "Stop", "Continue", "Toggle", "Switch");
            Group(rows, "Scenes", "scenes", "Master Dimmer", "Adjust", "Blackout");
            Group(rows, "Song Viewer", "song-viewer", "Font Size", "Increase", "Decrease");
            Group(rows, "Timer", "timer", "Control", "Start", "Stop", "Reset", "Toggle");
            Group(rows, "Transpose", "transpose", "Capo", "Adjust", "Change", "Up", "Down", "Toggle");
            Group(rows, "Transpose", "transpose", "Key", "Adjust", "Change", "Up", "Down", "Toggle");
            Group(rows, "User Interface", "ui", "Editors", "Actions", "Audio Media", "Chord Diagrams", "MIDI", "Scenes", "Song", "Visual Media");
            Group(rows, "User Interface", "ui", "Live bar", "Show", "Hide", "Toggle");
            Group(rows, "User Interface", "ui", "Low Light", "On", "Off", "Toggle");
            Group(rows, "User Interface", "ui", "Menus", "Open", "Close", "Actions", "Additions", "Attachments", "Messages", "Organizer", "Playback", "Share", "Songs", "Style Preferences", "Utilities", "Video");
            Group(rows, "User Interface", "ui", "Menubar", "Show", "Hide", "Toggle");
            Group(rows, "User Interface", "ui", "Navigation", "Previous", "Next", "Top", "Bottom", "Back", "Search", "Select");
            Group(rows, "User Interface", "ui", "Quick Pick", "Show", "Hide", "Toggle");
            Group(rows, "User Interface", "ui", "Screenshots", "Show", "Hide", "Toggle", "Capture");
            Group(rows, "User Interface", "ui", "Snapshots", "Load");
            Group(rows, "User Interface", "ui", "Songs Menu", "Show", "Hide", "Toggle");
            Group(rows, "User Interface", "ui", "Web Action", "Open", "Close");
            Group(rows, "User Interface", "ui", "Widgets", "Show", "Hide", "Toggle");
            Group(rows, "Voice", "voice", "Control", "Start", "Stop", "Toggle");
            AppleShortcut(rows, "close-window", "Close Window", "Cmd+W");
            AppleShortcut(rows, "new-song", "New Song", "Cmd+N");
            AppleShortcut(rows, "pedal-1", "Pedal 1", "Up");
            AppleShortcut(rows, "pedal-2", "Pedal 2", "Left");
            AppleShortcut(rows, "pedal-3", "Pedal 3", "Down");
            AppleShortcut(rows, "pedal-4", "Pedal 4", "Right");
            AppleShortcut(rows, "pedal-5", "Pedal 5", "Enter");
            AppleShortcut(rows, "pedal-6", "Pedal 6", "B");
            return rows.ToArray();
        }

        private static void AppleShortcut(List<OnSongAction> rows, string id, string name, string key)
        {
            rows.Add(new OnSongAction("keyboard-" + id, "Keyboard shortcuts", name, KeyboardSourceUrl, key + " (Apple)"));
        }

        private static void Group(List<OnSongAction> rows, string category, string page, string group, params string[] names)
        {
            foreach (string name in names)
            {
                string title = group.Length == 0 ? name : group + " / " + name;
                string id = Regex.Replace((page + "-" + title).ToLowerInvariant(), "[^a-z0-9]+", "-").TrimEnd('-');
                string shortcut = null;
                switch (id)
                {
                    case "navigation-song-next": id = "next"; title = "Next song"; break;
                    case "navigation-song-previous": id = "previous"; title = "Previous song"; break;
                    case "navigation-scroll-up": id = "scroll-up"; title = "Scroll up"; break;
                    case "navigation-scroll-down": id = "scroll-down"; title = "Scroll down"; break;
                    case "navigation-zoom-actual-size": shortcut = "Cmd+0 (Apple)"; break;
                    case "ui-editors-song": shortcut = "Cmd+E (Apple)"; break;
                    case "navigation-page-up": shortcut = "Page Up (Apple)"; break;
                    case "navigation-page-down": shortcut = "Page Down (Apple)"; break;
                }
                rows.Add(new OnSongAction(id, category, title, MenuSourceUrl + page + "/", shortcut));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PcOnSongBridge
{
    // Saved on the PC. The user assigns the corresponding shortcut in OnSong on the tablet.
    public sealed class VoiceBinding
    {
        public string Id { get; set; }
        public string ActionId { get; set; }
        public string Phrase { get; set; }
        public string Shortcut { get; set; }
    }

    internal sealed class VoiceCommand
    {
        public readonly string Id, Title, Phrase, Category, Shortcut;
        public VoiceCommand(string id, string title, string phrase, string category, string shortcut = null)
        {
            Id = id;
            Title = title;
            Phrase = phrase;
            Category = category;
            Shortcut = shortcut;
        }
    }

    internal sealed class VoiceShortcut
    {
        private readonly string[] keys;
        public string Label { get; private set; }
        public string[] AndroidKeys { get { return (string[])keys.Clone(); } }

        private VoiceShortcut(List<string> labels, List<string> codes)
        {
            Label = String.Join("+", labels);
            keys = codes.Select(code => "KEYCODE_" + code).ToArray();
        }

        private static readonly Dictionary<string, string> NamedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Up", "DPAD_UP" }, { "Down", "DPAD_DOWN" }, { "Left", "DPAD_LEFT" }, { "Right", "DPAD_RIGHT" },
            { "Page Up", "PAGE_UP" }, { "Page Down", "PAGE_DOWN" }, { "Home", "MOVE_HOME" }, { "End", "MOVE_END" },
            { "Space", "SPACE" }, { "Enter", "ENTER" }, { "Tab", "TAB" }
        };

        public static VoiceShortcut Parse(string text)
        {
            var parts = (text ?? "").Split('+').Select(part => part.Trim()).ToArray();
            if (parts.Length > 4 || parts.Any(String.IsNullOrEmpty))
                throw new ArgumentException("Enter one key, optionally preceded by Ctrl, Alt or Shift.");
            var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts.Take(parts.Length - 1))
                if (!new[] { "Ctrl", "Alt", "Shift" }.Contains(part, StringComparer.OrdinalIgnoreCase) || !modifiers.Add(part))
                    throw new ArgumentException("Use Ctrl, Alt or Shift once each, followed by one key.");
            string key = parts.Last().ToUpperInvariant();
            string code;
            if (!NamedKeys.TryGetValue(key, out code))
            {
                if (!Regex.IsMatch(key, @"\A(?:[A-Z0-9]|F(?:[1-9]|1[0-2]))\z"))
                    throw new ArgumentException("Choose a letter, digit, F1–F12, arrow, Page Up, Page Down, Home, End, Space, Enter or Tab.");
                code = key;
            }
            else key = NamedKeys.Keys.First(name => String.Equals(name, key, StringComparison.OrdinalIgnoreCase));
            if (modifiers.Contains("Alt") && (key == "Tab" || key == "F4" || key == "Space")
                || modifiers.Contains("Ctrl") && key == "Space")
                throw new ArgumentException("Choose a shortcut that is not reserved for app or input switching.");
            var labels = new List<string>();
            var codes = new List<string>();
            foreach (string mod in new[] { "Ctrl", "Alt", "Shift" })
                if (modifiers.Contains(mod)) { labels.Add(mod); codes.Add(mod.ToUpperInvariant() + "_LEFT"); }
            labels.Add(key);
            codes.Add(code);
            return new VoiceShortcut(labels, codes);
        }
    }

    internal static class VoiceCommands
    {
        internal static string SuggestedPhrase(OnSongAction action)
        {
            // The manual's "Goto" takes a section index. Spell it as spoken words in an English grammar.
            if (action.Id == "navigation-section-goto") return "go to section number";
            if (action.Id == "transpose-key-up") return "raise the song key";
            if (action.Id == "transpose-key-down") return "lower the song key";
            string[] parts = action.Title.ToLowerInvariant().Split('/');
            if (parts.Length == 1) return parts[0].Trim();
            string group = parts[0].Trim(), verb = parts[1].Trim();
            string categoryName = action.Category.ToLowerInvariant();
            if (categoryName == "backing tracks") categoryName = "backing track";
            string subject = group == "control" ? categoryName : group;
            if (group != "control" && !new[] { "navigation", "user interface", "transpose", "song viewer" }.Contains(categoryName))
                subject = categoryName + " " + group;
            string text;
            if (group == "editors") text = "open " + verb + " editor";
            else if (group == "menus" && !new[] { "open", "close" }.Contains(verb)) text = "open " + verb + " menu";
            else if (verb == "on" || verb == "off") text = "turn " + subject + " " + verb;
            else if (new[] { "up", "down", "in", "out", "left", "right" }.Contains(verb)) text = subject + " " + verb;
            else text = verb + " " + subject;
            return Regex.Replace(text.Replace("%", " percent "), @"[^\p{L}\p{M}\p{Nd}'-]+", " ").Trim();
        }

        internal static readonly VoiceCommand[] SongCommands =
        {
            new VoiceCommand("next", "Next song", "next song", "Navigation"),
            new VoiceCommand("previous", "Previous song", "previous song", "Navigation")
        };
        private static readonly VoiceCommand[] Native = SongCommands.Concat(new[]
        {
            new VoiceCommand("scroll-up", "Scroll up", "scroll up", "Navigation"),
            new VoiceCommand("scroll-down", "Scroll down", "scroll down", "Navigation")
        }).ToArray();

        internal const string NextSongShortcut = "Ctrl+Shift+N";
        internal const string PreviousSongShortcut = "Ctrl+Shift+P";

        internal static string FastSongShortcutFor(string action)
        {
            if (action == "next") return NextSongShortcut;
            if (action == "previous") return PreviousSongShortcut;
            return null;
        }

        public static VoiceBinding CreateBinding(string actionId, string phrase, string shortcut,
            IEnumerable<VoiceBinding> bindings, string existingId = null)
        {
            string normalized = Regex.Replace((phrase ?? "").Trim().Replace('’', '\''), @"\s+", " ");
            string[] words = normalized.Split(' ');
            const string spokenWord = @"\A[\p{L}\p{Nd}][\p{L}\p{M}\p{Nd}]*(?:['-][\p{L}\p{Nd}][\p{L}\p{M}\p{Nd}]*)*'?\z";
            if (normalized.Length > 80 || words.Length > 8 || words.Any(word => !Regex.IsMatch(word, spokenWord)))
                throw new ArgumentException("Enter a spoken phrase of one to eight words using letters, numbers, apostrophes or hyphens.");
            if (actionId != "custom" && !OnSongActions.All.Any(a => a.Id == actionId))
                throw new ArgumentException("Choose a catalog action or a custom target.");
            if (existingId != null && !Regex.IsMatch(existingId, @"\Amapping-[0-9a-f]{32}\z"))
                throw new ArgumentException("The mapping identifier is invalid. Create the mapping again.");
            if (Native.Any(command => String.Equals(command.Phrase, normalized, StringComparison.OrdinalIgnoreCase)) ||
                (bindings ?? Enumerable.Empty<VoiceBinding>()).Any(b => b != null && b.Id != existingId &&
                    String.Equals(b.Phrase, normalized, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("That phrase already has a command. Choose a distinct phrase.");
            return new VoiceBinding { Id = existingId ?? "mapping-" + Guid.NewGuid().ToString("N"),
                ActionId = actionId, Phrase = normalized, Shortcut = VoiceShortcut.Parse(shortcut).Label };
        }

        public static List<VoiceCommand> ForMode(TabletControlMode mode, IEnumerable<VoiceBinding> bindings)
        {
            // All supported modes can navigate songs, use guarded vertical swipes, and send explicit shortcuts.
            if (!Enum.IsDefined(typeof(TabletControlMode), mode)) return new List<VoiceCommand>();
            var result = new List<VoiceCommand>(Native.Length);
            foreach (VoiceCommand native in Native)
            {
                string shortcut = mode == TabletControlMode.FastSongShortcuts ? FastSongShortcutFor(native.Id) : null;
                result.Add(shortcut == null ? native : new VoiceCommand(native.Id, native.Title, native.Phrase, native.Category, shortcut));
            }
            var mapped = new List<VoiceCommand>();
            foreach (var binding in bindings ?? Enumerable.Empty<VoiceBinding>())
            {
                if (binding == null || binding.Id == null) continue;
                VoiceBinding valid;
                try { valid = CreateBinding(binding.ActionId, binding.Phrase, binding.Shortcut, null, binding.Id); }
                catch (ArgumentException) { continue; }
                var action = OnSongActions.All.FirstOrDefault(a => a.Id == valid.ActionId);
                mapped.Add(new VoiceCommand(valid.Id, action == null ? valid.Phrase : action.Title,
                    valid.Phrase, action == null ? "Custom" : action.Category, valid.Shortcut));
            }
            var duplicateIds = new HashSet<string>(mapped.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key));
            foreach (var group in mapped.GroupBy(c => c.Phrase, StringComparer.OrdinalIgnoreCase))
                if (group.Count() == 1 && !duplicateIds.Contains(group.First().Id)) result.Add(group.First());
            return result;
        }
    }
}

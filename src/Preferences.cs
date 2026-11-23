using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace PcOnSongBridge
{
    public enum TabletControlMode { Automatic, ArrowShortcuts, SongSwipes, FastSongShortcuts }

    public sealed class Preferences
    {
        public string ScrcpyPath { get; set; }
        public string Serial { get; set; }
        public string InputName { get; set; }
        public string OutputName { get; set; }
        public string OutputEndpointId { get; set; }
        public string WifiCueSource { get; set; }
        public string WifiCueSourceAddress { get; set; }
        public string WifiCueOutputEndpointId { get; set; }
        public int BufferMs { get; set; }
        public bool KeyboardMode { get; set; }
        public TabletControlMode? ControlMode { get; set; }
        public List<VoiceBinding> VoiceBindings { get; set; }

        public Preferences()
        {
            BufferMs = 75;
            VoiceBindings = new List<VoiceBinding>();
        }

        private static readonly string DefaultFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PC OnSong Bridge", "settings.xml");
        private string filePath = DefaultFilePath;
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Preferences));

        public static Preferences Load()
        {
            return Load(DefaultFilePath);
        }

        internal static Preferences Load(string path)
        {
            Preferences result;
            try
            {
                using (FileStream stream = File.OpenRead(path))
                    result = (Preferences)Serializer.Deserialize(stream) ?? new Preferences();
            }
            catch (IOException) { result = new Preferences(); }
            catch (UnauthorizedAccessException) { result = new Preferences(); }
            catch (InvalidOperationException) { result = new Preferences(); }
            result.filePath = path;
            return result;
        }

        public void Save()
        {
            string folder = Path.GetDirectoryName(filePath);
            Directory.CreateDirectory(folder);
            string staged = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = File.Create(staged))
                    Serializer.Serialize(stream, this);
                if (File.Exists(filePath))
                    File.Replace(staged, filePath, null);
                else
                    File.Move(staged, filePath);
            }
            finally
            {
                if (File.Exists(staged))
                    File.Delete(staged);
            }
        }
    }
}

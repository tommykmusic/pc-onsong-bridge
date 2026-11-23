using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PcOnSongBridge
{
    internal sealed class ConnectTokenStore
    {
        private readonly string folder;

        internal ConnectTokenStore(string folder = null)
        {
            this.folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PC OnSong Bridge", "Connect");
        }

        internal string GetOrCreate(Uri endpoint, string tabletName)
        {
            byte[] binding;
            using (var hash = SHA256.Create())
                binding = hash.ComputeHash(Encoding.UTF8.GetBytes(endpoint.Authority + "\n" + tabletName));
            string path = Path.Combine(folder, BitConverter.ToString(binding).Replace("-", "") + ".bin");
            Directory.CreateDirectory(folder);
            if (!File.Exists(path))
            {
                var random = new byte[32];
                using (var generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
                var encrypted = ProtectedData.Protect(random, binding, DataProtectionScope.CurrentUser);
                Array.Clear(random, 0, random.Length);
                string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(temporary, encrypted);
                    try { File.Move(temporary, path); }
                    catch (IOException) { if (!File.Exists(path)) throw; }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            var saved = ProtectedData.Unprotect(File.ReadAllBytes(path), binding, DataProtectionScope.CurrentUser);
            try
            {
                if (saved.Length != 32) throw new InvalidDataException("The saved pairing is invalid.");
                return BitConverter.ToString(saved).Replace("-", "").ToLowerInvariant();
            }
            finally { Array.Clear(saved, 0, saved.Length); }
        }
    }
}

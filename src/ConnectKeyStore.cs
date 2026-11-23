using Microsoft.Win32;
using System;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace PcOnSongBridge
{
    internal sealed class ConnectKeyStore
    {
        private const string ValueName = "DeveloperKey";
        private readonly string registryPath;

        internal ConnectKeyStore(string registryPath = @"Software\PC OnSong Bridge\Connect")
        {
            this.registryPath = registryPath;
        }

        internal static bool IsValid(string value)
        {
            return !String.IsNullOrEmpty(value) && value.Length <= 512 && value.All(c => c >= 33 && c <= 126);
        }

        internal string Read()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(registryPath))
                {
                    byte[] encrypted = key == null ? null : key.GetValue(ValueName) as byte[];
                    if (encrypted == null || encrypted.Length > 8192) return null;
                    byte[] clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    try
                    {
                        string value = Encoding.UTF8.GetString(clear);
                        return IsValid(value) ? value : null;
                    }
                    finally { Array.Clear(clear, 0, clear.Length); }
                }
            }
            catch (CryptographicException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (SecurityException) { return null; }
            catch (System.IO.IOException) { return null; }
        }

        internal void Save(string value)
        {
            if (!IsValid(value)) throw new ArgumentException("Enter a valid OnSong developer key.");
            byte[] clear = Encoding.UTF8.GetBytes(value);
            try
            {
                byte[] encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
                using (var key = Registry.CurrentUser.CreateSubKey(registryPath))
                    key.SetValue(ValueName, encrypted, RegistryValueKind.Binary);
            }
            finally { Array.Clear(clear, 0, clear.Length); }
        }
    }
}

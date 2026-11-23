using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PcOnSongBridge
{
    internal static class WindowsToolTrust
    {
        // Path.IsPathRooted also accepts UNC paths, \relative and C:relative on .NET Framework.
        internal static bool IsLocalDrivePath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length < 3 || !Char.IsLetter(path[0]) || path[1] != ':' ||
                (path[2] != '\\' && path[2] != '/')) return false;
            try
            {
                var type = new DriveInfo(path.Substring(0, 3)).DriveType;
                return type == DriveType.Fixed || type == DriveType.Removable || type == DriveType.Ram;
            }
            catch (ArgumentException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // Validate the file Windows is about to load. The caller holds a read-only share through LoadLibrary.
        internal static FileStream OpenTrustedNdiLibrary(string path)
        {
            if (!IsLocalDrivePath(path)) return null;
            FileStream file = null;
            try
            {
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!HasNdiSignature(file)) return null;
                var version = FileVersionInfo.GetVersionInfo(path);
                if (version.FileMajorPart != 6 || !String.Equals(version.OriginalFilename, "Processing.NDI.Lib.dll",
                    StringComparison.OrdinalIgnoreCase)) return null;
                FileStream verified = file;
                file = null;
                return verified;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (CryptographicException) { return null; }
            finally { if (file != null) file.Dispose(); }
        }

        private static bool HasNdiSignature(FileStream file)
        {
            // The primary signature of both architectures of the official NDI 6 runtime identifies Vizrt AG.
            var subject = new TrustFile { Size = (uint)Marshal.SizeOf(typeof(TrustFile)), Path = file.Name,
                Handle = file.SafeFileHandle.DangerousGetHandle() };
            var path = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustFile)));
            Marshal.StructureToPtr(subject, path, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf(typeof(TrustData)), Ui = 2, Choice = 1,
                File = path, StateAction = 1, Flags = 0x1000 }; // WTD_CACHE_ONLY_URL_RETRIEVAL for offline shows.
            var policy = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            try
            {
                if (WinVerifyTrust(new IntPtr(-1), ref policy, ref data) != 0) return false;
                IntPtr provider = WTHelperProvDataFromStateData(data.State);
                if (provider == IntPtr.Zero) return false;
                IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                if (signer == IntPtr.Zero) return false;
                IntPtr first = WTHelperGetProvCertFromChain(signer, 0);
                if (first == IntPtr.Zero) return false;
                var verified = (ProviderCert)Marshal.PtrToStructure(first, typeof(ProviderCert));
                if (verified.Context == IntPtr.Zero) return false;
                using (var cert = new X509Certificate2(verified.Context))
                    return String.Equals(cert.Subject, "CN=Vizrt AG, O=Vizrt AG, L=Zürich, C=CH", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                data.StateAction = 2; // WTD_STATEACTION_CLOSE
                WinVerifyTrust(new IntPtr(-1), ref policy, ref data);
                Marshal.DestroyStructure(path, typeof(TrustFile));
                Marshal.FreeHGlobal(path);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustFile { internal uint Size; [MarshalAs(UnmanagedType.LPWStr)] internal string Path; internal IntPtr Handle, KnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            internal uint Size;
            internal IntPtr PolicyCallback, SipClient;
            internal uint Ui, Revocation, Choice;
            internal IntPtr File;
            internal uint StateAction;
            internal IntPtr State, Url;
            internal uint Flags, UiContext;
            internal IntPtr SignatureSettings;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProviderCert { internal uint Size; internal IntPtr Context; }
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint index, bool counterSigner, uint counterIndex);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
    }
}

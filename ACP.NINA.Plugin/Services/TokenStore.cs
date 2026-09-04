using NINA.Core.Utility;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ACP.NINA.Plugin.Services {

    /// The ACP bearer token, held in Windows Credential Manager rather than in
    /// settings.json. Settings travel with the plugin folder and get copied
    /// around when people move a NINA install between machines, so a token in
    /// there is a token in a text file on a NUC at a remote site.
    ///
    /// This is a direct P/Invoke to advapi32 rather than the CredentialManagement
    /// NuGet package. The package targets the .NET Framework era, has not been
    /// updated in years, and would add another DLL the plugin's PostBuild step
    /// has to xcopy into NINA's plugin folder. Three entry points and one struct
    /// are cheaper than a dependency, and they carry no restore risk on
    /// net8.0-windows.
    ///
    /// Everything here fails soft. If Credential Manager is unavailable the
    /// plugin behaves as if no token is set, which is exactly the pre-v3
    /// behaviour, and the dock reports the resulting 401 instead of crashing.
    public static class TokenStore {

        /// One credential per plugin install. The target name is the key users
        /// would see in Windows' own Credential Manager UI, so it names ACP.
        public const string TargetName = "ACP.NINA.Plugin:AcpApiToken";

        private const uint CRED_TYPE_GENERIC = 1;
        private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite([In] ref CREDENTIAL userCredential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint reservedFlag);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
        private static extern void CredFree(IntPtr credentialPtr);

        /// The stored token, or null when none is set or the store is unreadable.
        public static string Read() {
            var handle = IntPtr.Zero;
            try {
                if (!CredRead(TargetName, CRED_TYPE_GENERIC, 0, out handle) || handle == IntPtr.Zero) {
                    return null;
                }
                var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
                if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) {
                    return null;
                }
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                var token = Encoding.Unicode.GetString(bytes);
                return string.IsNullOrWhiteSpace(token) ? null : token;
            } catch (Exception ex) {
                Logger.Warning($"ACP: could not read the API token from Credential Manager: {ex.Message}");
                return null;
            } finally {
                if (handle != IntPtr.Zero) {
                    CredFree(handle);
                }
            }
        }

        /// True when a non-empty token is stored. Used by the Options page so it
        /// can say a token is set without ever putting it back on screen.
        public static bool HasToken() {
            return Read() != null;
        }

        /// Store the token, or clear it when the value is null or blank.
        /// Returns false when Credential Manager refused the write.
        public static bool Write(string token) {
            if (string.IsNullOrWhiteSpace(token)) {
                return Delete();
            }

            var blob = Encoding.Unicode.GetBytes(token);
            var blobPtr = IntPtr.Zero;
            var targetPtr = IntPtr.Zero;
            var userPtr = IntPtr.Zero;
            try {
                blobPtr = Marshal.AllocCoTaskMem(blob.Length);
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
                targetPtr = Marshal.StringToCoTaskMemUni(TargetName);
                // Credential Manager rejects a generic credential with no user
                // name, so give it a fixed one. Nothing reads it back.
                userPtr = Marshal.StringToCoTaskMemUni("ACP");

                var cred = new CREDENTIAL {
                    Flags = 0,
                    Type = CRED_TYPE_GENERIC,
                    TargetName = targetPtr,
                    Comment = IntPtr.Zero,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = blobPtr,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    AttributeCount = 0,
                    Attributes = IntPtr.Zero,
                    TargetAlias = IntPtr.Zero,
                    UserName = userPtr,
                };

                if (!CredWrite(ref cred, 0)) {
                    var err = Marshal.GetLastWin32Error();
                    Logger.Error($"ACP: Credential Manager refused the token write, Win32 error {err}.");
                    return false;
                }
                Logger.Info("ACP: API token saved to Windows Credential Manager.");
                return true;
            } catch (Exception ex) {
                Logger.Error($"ACP: could not save the API token: {ex.Message}");
                return false;
            } finally {
                Array.Clear(blob, 0, blob.Length);
                if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
                if (targetPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPtr);
                if (userPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(userPtr);
            }
        }

        /// Remove the stored token. Returns true when nothing is left stored,
        /// including the case where there was nothing there to begin with.
        public static bool Delete() {
            try {
                if (CredDelete(TargetName, CRED_TYPE_GENERIC, 0)) {
                    Logger.Info("ACP: API token cleared from Windows Credential Manager.");
                    return true;
                }
                // 1168 is ERROR_NOT_FOUND, which is the state the caller wanted.
                var err = Marshal.GetLastWin32Error();
                if (err == 1168) return true;
                Logger.Warning($"ACP: could not clear the API token, Win32 error {err}.");
                return false;
            } catch (Exception ex) {
                Logger.Warning($"ACP: could not clear the API token: {ex.Message}");
                return false;
            }
        }
    }
}

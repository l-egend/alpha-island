using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using alpha_island.Conversations;


namespace alpha_island.Services
{
    /// <summary>
    /// AI 密钥的安全存放（B 组①）。
    ///
    /// 用 Windows DPAPI（<c>CryptProtectData</c>，当前用户范围）加密后写到
    /// <c>%LOCALAPPDATA%\alpha-island\config\ai\secrets.dat</c>：
    ///  • **不写进 options.json**（那里会被同步/分享/诊断导出带走）；
    ///  • **绝不进日志**；
    ///  • 通常只有原 Windows 用户环境可以解密，换机器导入应重新填写密钥。
    /// DPAPI 是用户级保护，不隔离同一用户运行的其他程序。
    /// </summary>
    public static class AiSecretStore
    {
        /// <summary>应用格式的固定附加熵，不是额外的秘密或应用间隔离。</summary>
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("alpha-island.ai.v1");

        public static string SecretsFile => ConversationFiles.Secret;

        /// <summary>是否已保存过密钥（只看文件在不在，不解密）。</summary>
        public static bool Exists
        {
            get
            {
                try { return File.Exists(SecretsFile); }
                catch { return false; }
            }
        }

        /// <summary>加密保存密钥；传空字符串等于删除。返回是否成功。</summary>
        public static bool TrySave(string? apiKey)
        {
            return TrySave(SecretsFile, apiKey);
        }

        internal static bool TrySave(string path, string? apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return Clear(path);

            byte[]? plain = null;
            try
            {
                plain = Encoding.UTF8.GetBytes(apiKey);
                byte[]? blob = Protect(plain);
                if (blob == null) return false;

                AtomicBinaryFile.Write(path, blob);

                return true;
            }
            catch (Exception)
            {

                return false;
            }
            finally { if (plain != null) CryptographicOperations.ZeroMemory(plain); }
        }

        /// <summary>读回密钥；没有/解不开返回空串（**不要把这个值写进日志**）。</summary>
        public static string Load()
        {
            return Load(File.Exists(SecretsFile) ? SecretsFile : Path.Combine(ConversationFiles.Root, "ai", "secrets.dat"));
        }

        internal static string Load(string path)
        {
            byte[]? plain = null;
            try
            {
                if (!File.Exists(path)) return "";
                byte[] blob = File.ReadAllBytes(path);
                if (blob.Length == 0) return "";

                plain = Unprotect(blob);
                if (plain == null) return "";
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception)
            {

                return "";
            }
            finally { if (plain != null) CryptographicOperations.ZeroMemory(plain); }
        }

        /// <summary>删除已保存的密钥。</summary>
        public static bool Clear() => Clear(SecretsFile);

        internal static bool Clear(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);

                }
                return true;
            }
            catch (Exception)
            {

                return false;
            }
        }

        // ─────────────────────────── DPAPI 包装 ───────────────────────────

        private static byte[]? Protect(byte[] plain)
        {
            IntPtr pIn = IntPtr.Zero, pEntropyBytes = IntPtr.Zero, pEntropyBlob = IntPtr.Zero;
            SecretNative.DATA_BLOB outBlob = default;
            try
            {
                pIn = Marshal.AllocHGlobal(plain.Length);
                Marshal.Copy(plain, 0, pIn, plain.Length);
                pEntropyBytes = Marshal.AllocHGlobal(Entropy.Length);
                Marshal.Copy(Entropy, 0, pEntropyBytes, Entropy.Length);

                var inBlob = new SecretNative.DATA_BLOB { cbData = plain.Length, pbData = pIn };
                var entropyBlob = new SecretNative.DATA_BLOB { cbData = Entropy.Length, pbData = pEntropyBytes };
                pEntropyBlob = Marshal.AllocHGlobal(Marshal.SizeOf<SecretNative.DATA_BLOB>());
                Marshal.StructureToPtr(entropyBlob, pEntropyBlob, false);

                // CRYPTPROTECT_UI_FORBIDDEN = 0x1：绝不弹系统对话框（本来也不会弹）
                if (!SecretNative.CryptProtectData(ref inBlob, "alpha-island AI key", pEntropyBlob,
                        IntPtr.Zero, IntPtr.Zero, 0x1, out outBlob))
                {

                    return null;
                }

                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            catch (Exception)
            {

                return null;
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero) SecretNative.LocalFree(outBlob.pbData);
                if (pEntropyBlob != IntPtr.Zero) Marshal.FreeHGlobal(pEntropyBlob);
                if (pEntropyBytes != IntPtr.Zero) Marshal.FreeHGlobal(pEntropyBytes);
                if (pIn != IntPtr.Zero)
                {
                    Marshal.Copy(new byte[plain.Length], 0, pIn, plain.Length);
                    Marshal.FreeHGlobal(pIn);
                }
            }
        }

        private static byte[]? Unprotect(byte[] blob)
        {
            IntPtr pIn = IntPtr.Zero, pEntropyBytes = IntPtr.Zero, pEntropyBlob = IntPtr.Zero;
            SecretNative.DATA_BLOB outBlob = default;
            try
            {
                pIn = Marshal.AllocHGlobal(blob.Length);
                Marshal.Copy(blob, 0, pIn, blob.Length);
                pEntropyBytes = Marshal.AllocHGlobal(Entropy.Length);
                Marshal.Copy(Entropy, 0, pEntropyBytes, Entropy.Length);

                var inBlob = new SecretNative.DATA_BLOB { cbData = blob.Length, pbData = pIn };
                var entropyBlob = new SecretNative.DATA_BLOB { cbData = Entropy.Length, pbData = pEntropyBytes };
                pEntropyBlob = Marshal.AllocHGlobal(Marshal.SizeOf<SecretNative.DATA_BLOB>());
                Marshal.StructureToPtr(entropyBlob, pEntropyBlob, false);

                if (!SecretNative.CryptUnprotectData(ref inBlob, IntPtr.Zero, pEntropyBlob,
                        IntPtr.Zero, IntPtr.Zero, 0x1, out outBlob))
                {

                    return null;
                }

                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            catch (Exception)
            {

                return null;
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero)
                {
                    Marshal.Copy(new byte[outBlob.cbData], 0, outBlob.pbData, outBlob.cbData);
                    SecretNative.LocalFree(outBlob.pbData);
                }
                if (pEntropyBlob != IntPtr.Zero) Marshal.FreeHGlobal(pEntropyBlob);
                if (pEntropyBytes != IntPtr.Zero) Marshal.FreeHGlobal(pEntropyBytes);
                if (pIn != IntPtr.Zero) Marshal.FreeHGlobal(pIn);
            }
        }
    }
}

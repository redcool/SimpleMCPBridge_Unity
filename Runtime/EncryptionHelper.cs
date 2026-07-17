using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SimpleMCPBridge
{
    /// <summary>
    /// Shared-key AES-256-CBC payload encryption.
    /// Both Bridge and Server must use the same encryptionKey.
    /// Empty key = no encryption (passthrough).
    ///
    /// Encrypted format: {"encrypted":"<base64(IV + ciphertext)>"}
    /// Key derivation: SHA-256(encryptionKey)
    /// </summary>
    public static class EncryptionHelper
    {
        /// <summary>
        /// Encrypt plaintext. Returns JSON-wrapped base64 ciphertext.
        /// If key is empty, returns plaintext unchanged.
        /// </summary>
        public static string Encrypt(string plainText, string key)
        {
            if (string.IsNullOrEmpty(key)) return plainText;

            var keyBytes = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(key));

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.BlockSize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = keyBytes;
            aes.GenerateIV();

            var iv = aes.IV;
            byte[] cipherBytes;
            using (var encryptor = aes.CreateEncryptor())
            {
                var plainBytes = Encoding.UTF8.GetBytes(plainText);
                cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
            }

            // Prepend IV to ciphertext
            var combined = new byte[iv.Length + cipherBytes.Length];
            Buffer.BlockCopy(iv, 0, combined, 0, iv.Length);
            Buffer.BlockCopy(cipherBytes, 0, combined, iv.Length, cipherBytes.Length);

            return "{\"encrypted\":\"" + Convert.ToBase64String(combined) + "\"}";
        }

        /// <summary>
        /// Decrypt an incoming message. If key is empty, returns data unchanged.
        /// If data is not encrypted (no {"encrypted":"..."} wrapper), returns data unchanged.
        /// Returns null on decryption failure (wrong key or corrupt data).
        /// </summary>
        public static string Decrypt(string data, string key)
        {
            if (string.IsNullOrEmpty(key)) return data;

            // Check for encrypted wrapper
            var enc = ExtractEncryptedValue(data);
            if (enc == null) return data; // not encrypted

            try
            {
                var combined = Convert.FromBase64String(enc);
                if (combined.Length <= 16) return null;

                var iv = new byte[16];
                var cipherBytes = new byte[combined.Length - 16];
                Buffer.BlockCopy(combined, 0, iv, 0, 16);
                Buffer.BlockCopy(combined, 16, cipherBytes, 0, cipherBytes.Length);

                var keyBytes = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(key));

                using var aes = Aes.Create();
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = keyBytes;
                aes.IV = iv;

                using var decryptor = aes.CreateDecryptor();
                var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Minimal JSON parser — extracts the string value of "encrypted" key.
        /// Returns null if the JSON doesn't start with {"encrypted":"...
        /// This avoids parsing full JSON for every incoming message.
        /// </summary>
        private static string ExtractEncryptedValue(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            json = json.TrimStart();
            if (!json.StartsWith("{\"encrypted\":")) return null;

            // Find the opening quote of the value
            var colonIdx = json.IndexOf(':', 14); // after {"encrypted":
            if (colonIdx < 0) return null;

            var quoteStart = colonIdx + 1;
            while (quoteStart < json.Length && json[quoteStart] == ' ') quoteStart++;
            if (quoteStart >= json.Length || json[quoteStart] != '"') return null;

            quoteStart++; // skip opening quote
            var quoteEnd = json.LastIndexOf('"');
            if (quoteEnd <= quoteStart) return null;

            return json.Substring(quoteStart, quoteEnd - quoteStart);
        }
    }
}

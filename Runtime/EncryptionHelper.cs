using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace SimpleMCPBridge
{
    /// <summary>
    /// Shared-key AES-256-CBC payload encryption.
    /// Both Bridge and Server must use the same encryptionKey.
    /// Empty key = no encryption (passthrough).
    ///
    /// Encrypted format: #ENC#<base64(IV + ciphertext)>
    /// Key derivation: SHA-256(encryptionKey)
    /// NOTE: The server-side decryption must also use the #ENC# prefix.
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

            using var sha = SHA256.Create();
            var keyBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key));

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

            return "#ENC#" + Convert.ToBase64String(combined);
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

                using var sha = SHA256.Create();
                var keyBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key));

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
        /// Extracts the base64 payload after the "#ENC#" prefix.
        /// Returns null if the string doesn't start with "#ENC#".
        /// NOTE: Server must also use the #ENC# prefix.
        /// </summary>
        private static string ExtractEncryptedValue(string data)
        {
            if (string.IsNullOrEmpty(data)) return null;
            if (!data.TrimStart().StartsWith("#ENC#")) return null;

            return data.Substring(data.IndexOf("#ENC#", StringComparison.Ordinal) + 5).Trim();
        }
    }
}

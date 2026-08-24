using System;
using System.Security.Cryptography;

namespace MetroTelegram.Crypto
{
    public static class MtprotoKdf
    {
        public static void ComputeKeys(byte[] authKey, byte[] msgKey, bool isClient, out byte[] aesKey, out byte[] aesIv)
        {
            int x = isClient ? 0 : 8;

            using (SHA256Managed sha256 = new SHA256Managed())
            {
                byte[] bufA = new byte[16 + 36];
                Buffer.BlockCopy(msgKey, 0, bufA, 0, 16);
                Buffer.BlockCopy(authKey, x, bufA, 16, 36);
                byte[] sha256_a = sha256.ComputeHash(bufA);

                byte[] bufB = new byte[36 + 16];
                Buffer.BlockCopy(authKey, 40 + x, bufB, 0, 36);
                Buffer.BlockCopy(msgKey, 0, bufB, 36, 16);
                byte[] sha256_b = sha256.ComputeHash(bufB);

                aesKey = new byte[32];
                Buffer.BlockCopy(sha256_a, 0, aesKey, 0, 8);
                Buffer.BlockCopy(sha256_b, 8, aesKey, 8, 16);
                Buffer.BlockCopy(sha256_a, 24, aesKey, 24, 8);

                aesIv = new byte[32];
                Buffer.BlockCopy(sha256_b, 0, aesIv, 0, 8);
                Buffer.BlockCopy(sha256_a, 8, aesIv, 8, 16);
                Buffer.BlockCopy(sha256_b, 24, aesIv, 24, 8);
            }
        }

        public static byte[] ComputeMsgKey(byte[] authKey, byte[] plaintext, bool isClient)
        {
            int x = isClient ? 0 : 8;

            using (SHA256Managed sha256 = new SHA256Managed())
            {
                byte[] toHash = new byte[32 + plaintext.Length];
                Buffer.BlockCopy(authKey, 88 + x, toHash, 0, 32);
                Buffer.BlockCopy(plaintext, 0, toHash, 32, plaintext.Length);
                byte[] hash = sha256.ComputeHash(toHash);

                byte[] msgKey = new byte[16];
                Buffer.BlockCopy(hash, 8, msgKey, 0, 16);
                return msgKey;
            }
        }
    }
}
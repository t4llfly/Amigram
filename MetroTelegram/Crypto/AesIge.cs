using System;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace MetroTelegram.Crypto
{
    public static class AesIge
    {
        public static byte[] Decrypt(byte[] cipherText, byte[] key, byte[] iv)
        {
            if (cipherText.Length % 16 != 0)
                throw new ArgumentException("Ciphertext length must be multiple of 16");

            byte[] plainText = new byte[cipherText.Length];
            byte[] iv1 = new byte[16];
            byte[] iv2 = new byte[16];
            Buffer.BlockCopy(iv, 0, iv1, 0, 16);
            Buffer.BlockCopy(iv, 16, iv2, 0, 16);

            AesEngine engine = new AesEngine();
            engine.Init(false, new KeyParameter(key));

            byte[] block = new byte[16];
            byte[] decryptedBlock = new byte[16];

            for (int i = 0; i < cipherText.Length; i += 16)
            {
                for (int j = 0; j < 16; j++)
                    block[j] = (byte)(cipherText[i + j] ^ iv2[j]);

                engine.ProcessBlock(block, 0, decryptedBlock, 0);

                for (int j = 0; j < 16; j++)
                    plainText[i + j] = (byte)(decryptedBlock[j] ^ iv1[j]);

                Buffer.BlockCopy(cipherText, i, iv1, 0, 16);
                Buffer.BlockCopy(plainText, i, iv2, 0, 16);
            }

            return plainText;
        }

        public static byte[] Encrypt(byte[] plainText, byte[] key, byte[] iv)
        {
            if (plainText.Length % 16 != 0)
                throw new ArgumentException("Plaintext length must be multiple of 16");

            byte[] cipherText = new byte[plainText.Length];
            byte[] iv1 = new byte[16];
            byte[] iv2 = new byte[16];
            Buffer.BlockCopy(iv, 0, iv1, 0, 16);
            Buffer.BlockCopy(iv, 16, iv2, 0, 16);

            AesEngine engine = new AesEngine();
            engine.Init(true, new KeyParameter(key));

            byte[] block = new byte[16];
            byte[] encryptedBlock = new byte[16];

            for (int i = 0; i < plainText.Length; i += 16)
            {
                for (int j = 0; j < 16; j++)
                    block[j] = (byte)(plainText[i + j] ^ iv1[j]);

                engine.ProcessBlock(block, 0, encryptedBlock, 0);

                for (int j = 0; j < 16; j++)
                    cipherText[i + j] = (byte)(encryptedBlock[j] ^ iv2[j]);

                Buffer.BlockCopy(cipherText, i, iv1, 0, 16);
                Buffer.BlockCopy(plainText, i, iv2, 0, 16);
            }

            return cipherText;
        }
    }
}
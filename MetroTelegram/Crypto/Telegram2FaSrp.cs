using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;

namespace MetroTelegram.Crypto
{
    public class PasswordKdfParams
    {
        public long SrpId { get; set; }
        public byte[] SrpB { get; set; }
        public byte[] Salt1 { get; set; }
        public byte[] Salt2 { get; set; }
        public int G { get; set; }
        public byte[] P { get; set; }
        public string Hint { get; set; }
    }

    public static class Telegram2FaSrp
    {
        private static readonly RNGCryptoServiceProvider _rng = new RNGCryptoServiceProvider();

        private static byte[] SH(byte[] data, byte[] salt)
        {
            using (SHA256Managed sha256 = new SHA256Managed())
            {
                byte[] buf = new byte[salt.Length + data.Length + salt.Length];
                Buffer.BlockCopy(salt, 0, buf, 0, salt.Length);
                Buffer.BlockCopy(data, 0, buf, salt.Length, data.Length);
                Buffer.BlockCopy(salt, 0, buf, salt.Length + data.Length, salt.Length);
                return sha256.ComputeHash(buf);
            }
        }

        public static void ComputeSrpProof(string password, PasswordKdfParams kdf, out byte[] A_bytes, out byte[] M1_bytes)
        {
            byte[] passBytes = Encoding.UTF8.GetBytes(password);

            byte[] sh1 = SH(passBytes, kdf.Salt1);
            byte[] ph1 = SH(sh1, kdf.Salt2);

            Pkcs5S2ParametersGenerator generator = new Pkcs5S2ParametersGenerator(new Sha512Digest());
            generator.Init(ph1, kdf.Salt1, 100000);
            KeyParameter keyParam = (KeyParameter)generator.GenerateDerivedParameters("AES", 64 * 8);
            byte[] pbkdf2_res = keyParam.GetKey();

            byte[] ph2 = SH(pbkdf2_res, kdf.Salt2);

            BigInteger x = new BigInteger(1, ph2);
            BigInteger p = new BigInteger(1, kdf.P);
            BigInteger g = BigInteger.ValueOf(kdf.G);
            BigInteger B = new BigInteger(1, kdf.SrpB);

            byte[] aBytes = new byte[256];
            _rng.GetBytes(aBytes);
            BigInteger a = new BigInteger(1, aBytes);
            BigInteger A = g.ModPow(a, p);

            byte[] pPadded = Pad256(p.ToByteArrayUnsigned());
            byte[] gPadded = Pad256(g.ToByteArrayUnsigned());
            byte[] aPadded = Pad256(A.ToByteArrayUnsigned());
            byte[] bPadded = Pad256(B.ToByteArrayUnsigned());

            BigInteger k;
            using (SHA256Managed sha256 = new SHA256Managed())
            {
                byte[] kInput = new byte[512];
                Buffer.BlockCopy(pPadded, 0, kInput, 0, 256);
                Buffer.BlockCopy(gPadded, 0, kInput, 256, 256);
                k = new BigInteger(1, sha256.ComputeHash(kInput));
            }

            BigInteger u;
            using (SHA256Managed sha256 = new SHA256Managed())
            {
                byte[] uInput = new byte[512];
                Buffer.BlockCopy(aPadded, 0, uInput, 0, 256);
                Buffer.BlockCopy(bPadded, 0, uInput, 256, 256);
                u = new BigInteger(1, sha256.ComputeHash(uInput));
            }

            BigInteger v = g.ModPow(x, p);
            BigInteger kv = k.Multiply(v).Mod(p);

            BigInteger sBase = B.Subtract(kv).Mod(p);
            if (sBase.SignValue < 0) sBase = sBase.Add(p);

            BigInteger sExp = a.Add(u.Multiply(x));
            BigInteger S = sBase.ModPow(sExp, p);
            byte[] sPadded = Pad256(S.ToByteArrayUnsigned());

            byte[] K;
            using (SHA256Managed sha256 = new SHA256Managed())
            {
                K = sha256.ComputeHash(sPadded);
            }

            using (SHA256Managed sha256 = new SHA256Managed())
            {
                byte[] pHash = sha256.ComputeHash(pPadded);
                byte[] gHash = sha256.ComputeHash(gPadded);
                byte[] pXorG = new byte[32];
                for (int i = 0; i < 32; i++) pXorG[i] = (byte)(pHash[i] ^ gHash[i]);

                byte[] s1Hash = sha256.ComputeHash(kdf.Salt1);
                byte[] s2Hash = sha256.ComputeHash(kdf.Salt2);

                byte[] m1Input = new byte[32 + 32 + 32 + 256 + 256 + 32];
                Buffer.BlockCopy(pXorG, 0, m1Input, 0, 32);
                Buffer.BlockCopy(s1Hash, 0, m1Input, 32, 32);
                Buffer.BlockCopy(s2Hash, 0, m1Input, 64, 32);
                Buffer.BlockCopy(aPadded, 0, m1Input, 96, 256);
                Buffer.BlockCopy(bPadded, 0, m1Input, 352, 256);
                Buffer.BlockCopy(K, 0, m1Input, 608, 32);

                M1_bytes = sha256.ComputeHash(m1Input);
            }

            A_bytes = aPadded;
            Debug.WriteLine(string.Format("[Telegram2FaSrp] M1 вычислен: {0}, A_len: {1}",
                BitConverter.ToString(M1_bytes).Replace("-", "").Substring(0, 16), A_bytes.Length));
        }

        public static byte[] Pad256(byte[] data)
        {
            if (data == null) return new byte[256];
            if (data.Length == 256) return data;
            if (data.Length > 256)
            {
                byte[] trimmed = new byte[256];
                Buffer.BlockCopy(data, data.Length - 256, trimmed, 0, 256);
                return trimmed;
            }
            byte[] padded = new byte[256];
            Buffer.BlockCopy(data, 0, padded, 256 - data.Length, data.Length);
            return padded;
        }
    }
}
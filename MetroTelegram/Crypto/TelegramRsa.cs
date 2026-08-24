using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Org.BouncyCastle.Math;

namespace MetroTelegram.Crypto
{
    public class TelegramPublicKey
    {
        public long Fingerprint { get; set; }
        public BigInteger Modulus { get; set; }
        public BigInteger Exponent { get; set; }
    }

    public static class TelegramRsa
    {
        private static Dictionary<long, TelegramPublicKey> _keys;
        private static readonly object _initLock = new object();

        private static readonly string[] TelegramPemKeysBase64 = new string[]
        {
            "MIIBCgKCAQEAwVACPiGmYPrK6W7h01XNeJHpP540FejiNZFi94HNRh9Hc/0kW2Yn" +
            "PXBlQIdfGh5YTUsIuvx+0C+HN5eo3jMThQWOm526/T8hsBQIj2UdzTdYTioYVEU4" +
            "VhikuAXDJOkpPJNx5sySirhhvcRIrLIaC8xCUYgzb0+/uChoGoC3hOntdukF7F8P" +
            "fxDUASYT30tqYkKAooNqUlYootyGlLw9N0EB280EWUMIQAqgFqMW3g8IlAZtAkMe" +
            "Sad5GGD3mTVPaBaW4GdT6n+/I56TaYTLuhmymoYgOiNMzWWnP288gmJWjY8Goo47" +
            "EnhUlGQSoVvlSfGVYzlIiRRioadQKwidaQIDAQAB",

            "MIIBCgKCAQEAxKLo+/cg0SITFYMGMkqevgnzkThbtjEIuvXphzkKzkRI8qqlap+G" +
            "CTVRuhGGSahOxLkMmTjAjVlR5Y696FcQOt9pHgkAnb9F5GLbJD9qlq1P6FI405x1" +
            "P1508iQkW+FLm56VyQQKnN6Ksgn65AjHgRmGnTIqB009Vo1glZh8O788LrpmlFbP" +
            "7h703AX0NirEjoUT68BcxvWSp00MB/WnuPzET0yR5LJFgtkw3nn80P2Y2biVgq/8" +
            "EKmmQHIW10ARFki9JFNTz+/7Cc58o/Etv54YbD+OICq/+/BwaYpuHgO6kjGsc6VL" +
            "3ddJbfHyqlVKVIpTvUz5NCRMtUhasterdQIDAQAB",

            "MIIBCgKCAQEA6LszBcC1LGzyr992NzE0ieY+BSaOW622Aa9Bd4ZHLl+TuFQ4lo4g" +
            "5nKaMBwK/BIb9xUfg0Q29/2mgIR6Zr9krM7HjuIcCzFvDtr+L0GQjae9H0pRB2OO" +
            "62cECs5HKhT5DZ98K33vmWiLowc621dQuwKWSQKjWf50XYFw42h21P2KXUGyp2y/" +
            "+aEyZ+uVgLLQbRA1dEjSDZ2iGRy12Mk5gpYc397aYp438fsJoHIgJ2lgMv5h7WY9" +
            "t6N/byY9Nw9p21Og3AoXSL2q/2IJ1WRUhebgAdGVMlV1fkuOQoEzR7EdpqtQD9Cs" +
            "5+bfo3Nhmcyvk5ftB0WkJ9z6bNZ7yxrP8wIDAQAB",

            "MIIBCgKCAQEA750qTOdnJJ2xYeLKnVUIGSTx386BSOT9sm4SqCwWDZTRi8pSZrah" +
            "19WQ28YBzGXx4PasdhxqeAAsSxJVuEQWglJBGeSFCPenHqmlEzFYqH1ehMzUL6MG" +
            "ifuFt16qGL4j5c84R/80rrCouJW5Knq265jK0T11OxGoohve1NPVMbSi60W6vDFl" +
            "dvlapZPTaGOnevZOLhmdevaFQf6IfOWtTxWSo6Y5nvGF/n0ITqJZGt1edEBnToh2" +
            "T4KWbYoc77PXVbDa0BhEbgy9nhr/+/t5PN0I610q98qBlsmYPnJvODL/mYOWQ1AM" +
            "0Rt+58++1Million/W1b73/s+693F5vTIDAQAB"
        };

        private static void EnsureInitialized()
        {
            if (_keys != null) return;

            lock (_initLock)
            {
                if (_keys != null) return;

                _keys = new Dictionary<long, TelegramPublicKey>();

                foreach (string base64 in TelegramPemKeysBase64)
                {
                    try
                    {
                        byte[] der = Convert.FromBase64String(base64);
                        ParsePkcs1Der(der);
                    }
                    catch { }
                }
            }
        }

        private static void ParsePkcs1Der(byte[] der)
        {
            using (MemoryStream ms = new MemoryStream(der))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                byte seqTag = reader.ReadByte();
                int seqLen = ReadAsnLength(reader);

                byte modTag = reader.ReadByte();
                int modLen = ReadAsnLength(reader);
                byte[] modBytes = reader.ReadBytes(modLen);

                if (modBytes.Length > 256 && modBytes[0] == 0)
                {
                    byte[] trimmed = new byte[256];
                    Buffer.BlockCopy(modBytes, modBytes.Length - 256, trimmed, 0, 256);
                    modBytes = trimmed;
                }

                byte expTag = reader.ReadByte();
                int expLen = ReadAsnLength(reader);
                byte[] expBytes = reader.ReadBytes(expLen);

                BigInteger modulus = new BigInteger(1, modBytes);
                BigInteger exponent = new BigInteger(1, expBytes);

                long fingerprint = ComputeFingerprint(modBytes, expBytes);

                _keys[fingerprint] = new TelegramPublicKey
                {
                    Fingerprint = fingerprint,
                    Modulus = modulus,
                    Exponent = exponent
                };
            }
        }

        private static int ReadAsnLength(BinaryReader reader)
        {
            byte b = reader.ReadByte();
            if ((b & 0x80) == 0)
            {
                return b;
            }

            int count = b & 0x7F;
            int length = 0;
            for (int i = 0; i < count; i++)
            {
                length = (length << 8) | reader.ReadByte();
            }
            return length;
        }

        private static long ComputeFingerprint(byte[] modulus, byte[] exponent)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                WriteTlBytes(writer, modulus);
                WriteTlBytes(writer, exponent);

                byte[] rsaBytes = ms.ToArray();
                using (SHA1Managed sha1 = new SHA1Managed())
                {
                    byte[] hash = sha1.ComputeHash(rsaBytes);
                    return BitConverter.ToInt64(hash, 12);
                }
            }
        }

        public static void WriteTlBytes(BinaryWriter writer, byte[] bytes)
        {
            if (bytes.Length < 254)
            {
                writer.Write((byte)bytes.Length);
                writer.Write(bytes);
                int pad = (4 - ((bytes.Length + 1) % 4)) % 4;
                for (int i = 0; i < pad; i++) writer.Write((byte)0);
            }
            else
            {
                writer.Write((byte)254);
                writer.Write((byte)(bytes.Length & 0xFF));
                writer.Write((byte)((bytes.Length >> 8) & 0xFF));
                writer.Write((byte)((bytes.Length >> 16) & 0xFF));
                writer.Write(bytes);
                int pad = (4 - (bytes.Length % 4)) % 4;
                for (int i = 0; i < pad; i++) writer.Write((byte)0);
            }
        }

        public static bool FindMatchingKey(IList<long> fingerprints, out long matchedFingerprint, out TelegramPublicKey keyParams)
        {
            EnsureInitialized();

            if (fingerprints != null)
            {
                foreach (long fp in fingerprints)
                {
                    if (_keys.ContainsKey(fp))
                    {
                        matchedFingerprint = fp;
                        keyParams = _keys[fp];
                        return true;
                    }
                }
            }

            foreach (var kvp in _keys)
            {
                matchedFingerprint = kvp.Key;
                keyParams = kvp.Value;
                return true;
            }

            throw new InvalidOperationException("В клиенте не найдено ни одного валидного RSA-ключа.");
        }

        public static byte[] EncryptWithRsa(TelegramPublicKey key, byte[] data256)
        {
            BigInteger message = new BigInteger(1, data256);
            BigInteger encrypted = message.ModPow(key.Exponent, key.Modulus);

            byte[] result = encrypted.ToByteArrayUnsigned();
            if (result.Length < 256)
            {
                byte[] padded = new byte[256];
                Buffer.BlockCopy(result, 0, padded, 256 - result.Length, result.Length);
                return padded;
            }
            return result;
        }
    }
}
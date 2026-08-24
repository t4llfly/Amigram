using System;

namespace MetroTelegram.Crypto
{
    public static class Factorizer
    {
        private static ulong Gcd(ulong a, ulong b)
        {
            while (b != 0)
            {
                ulong temp = b;
                b = a % b;
                a = temp;
            }
            return a;
        }

        public static void Factorize(ulong pq, out ulong p, out ulong q)
        {
            if (pq % 2 == 0)
            {
                p = 2;
                q = pq / 2;
                return;
            }

            ulong x = 2;
            ulong y = 2;
            ulong d = 1;
            ulong c = 1;

            Random rnd = new Random();

            while (d == 1 || d == pq)
            {
                if (d == pq)
                {
                    x = (ulong)rnd.Next(2, 50);
                    y = x;
                    c = (ulong)rnd.Next(1, 20);
                }

                x = (MultiplyMod(x, x, pq) + c) % pq;
                y = (MultiplyMod(y, y, pq) + c) % pq;
                y = (MultiplyMod(y, y, pq) + c) % pq;

                ulong diff = x > y ? x - y : y - x;
                d = Gcd(diff, pq);
            }

            p = d;
            q = pq / d;

            if (p > q)
            {
                ulong temp = p;
                p = q;
                q = temp;
            }
        }

        private static ulong MultiplyMod(ulong a, ulong b, ulong m)
        {
            ulong res = 0;
            a %= m;
            while (b > 0)
            {
                if ((b & 1) > 0) res = (res + a) % m;
                a = (2 * a) % m;
                b >>= 1;
            }
            return res;
        }
    }
}
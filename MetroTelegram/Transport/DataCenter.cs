using System.Collections.Generic;

namespace MetroTelegram.Transport
{
    public class DataCenter
    {
        public int Id { get; set; }
        public string Host { get; set; }
        public List<string> FallbackHosts { get; set; }
        public int Port { get; set; }

        public static readonly int[] FallbackPorts = new int[] { 443, 80, 5222, 8443 };

        public DataCenter(int id, string host, int port = 443, string[] fallbacks = null)
        {
            Id = id;
            Host = host;
            Port = port;
            FallbackHosts = new List<string> { host };
            if (fallbacks != null)
            {
                FallbackHosts.AddRange(fallbacks);
            }
        }

        public static readonly DataCenter DC1 = new DataCenter(1, "149.154.175.53", 443, new[] { "149.154.175.50" });
        public static readonly DataCenter DC2 = new DataCenter(2, "149.154.167.51", 443, new[] { "91.108.56.165", "149.154.167.50" });
        public static readonly DataCenter DC3 = new DataCenter(3, "149.154.175.100", 443);
        public static readonly DataCenter DC4 = new DataCenter(4, "149.154.167.91", 443, new[] { "91.108.56.164", "149.154.167.90" });
        public static readonly DataCenter DC5 = new DataCenter(5, "91.108.56.130", 443, new[] { "91.108.56.165" });

        public static readonly DataCenter Default = DC2;

        public static DataCenter GetDc(int id)
        {
            switch (id)
            {
                case 1: return DC1;
                case 2: return DC2;
                case 3: return DC3;
                case 4: return DC4;
                case 5: return DC5;
                default: return DC2;
            }
        }
    }
}
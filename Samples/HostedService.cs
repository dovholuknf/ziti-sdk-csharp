using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace OpenZiti.Samples {
    public class HostedService : SampleBase {
        public static async Task Run(string hostedJwt) {
            string outputPath = "";
            if (hostedJwt.EndsWith(".jwt")) {
                outputPath = hostedJwt.Replace(".jwt", ".json");
            } else {
                Console.WriteLine("Please provide a file that ends with .jwt");
                return;
            }

            try {
                Enroll(hostedJwt, outputPath);
            } catch (Exception e) {
                Console.WriteLine($"WARN: the jwt was not enrolled properly: {e.Message}");
            }

            ZitiSocket socket = new ZitiSocket(SocketType.Stream);
            ZitiContext ctx = new ZitiContext(outputPath);
            string svc = "hosted-svc";
            string terminator = "";
            
            API.Bind(socket, ctx, svc, terminator);
            API.Listen(socket, 100);

            while (true) {
                ZitiSocket client = API.Accept(socket, out var caller);
                using (var s = client.ToNetworkStream())
                using (var r = new StreamReader(s))
                using (var w = new StreamWriter(s)) {
                    w.AutoFlush = true;
                    Console.WriteLine($"receiving connection from {caller}");
                    string read = await r.ReadLineAsync();
                    while (read != "EOL") {
                        Console.WriteLine($"{caller} sent {read}");
                        string resp = $"Hi {caller}. Thanks for sending me: {read}";
                        await w.WriteLineAsync(resp);
                        Console.WriteLine($"replied to {caller}");
                        read = r.ReadLine();
                    }
                    await w.WriteLineAsync("disconnecting...");
                    Console.WriteLine($"{caller} disconnected");
                }
            }
        }
    }
}

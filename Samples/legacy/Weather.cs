/*
Copyright NetFoundry Inc.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

https://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using OpenZiti.legacy;

namespace OpenZiti.Samples.legacy {
    public class Weather {
        private static readonly MemoryStream ms = new MemoryStream(2 << 16); //a big bucket to hold bytes to display contiguously at the end of the program

        public static void Run(string identityFile) {
            var opts = new ZitiIdentity.InitOptions() {
                EventFlags = ZitiEventFlags.ZitiContextEvent | ZitiEventFlags.ZitiServiceEvent,
                IdentityFile = identityFile,
                ApplicationContext = "weather-svc",
                ConfigurationTypes = new[] { "weather-config-type" },
            };
            opts.OnZitiContextEvent += Opts_OnZitiContextEvent;
            opts.OnZitiServiceEvent += Opts_OnZitiServiceEvent;

            var zid = new ZitiIdentity(opts);
            zid.Run();
            Console.WriteLine("=================LOOP IS COMPLETE=================");
        }

        private static void Opts_OnZitiContextEvent(object sender, ZitiContextEvent e) {
            if (e.Status.Ok()) {
                //good. carry on.
            } else {
                //something went wrong. inspect the erorr here...
                Console.WriteLine("An error occurred.");
                Console.WriteLine("    ZitiStatus : " + e.Status);
                Console.WriteLine("               : " + e.Status.GetDescription());
            }
        }

        private static void Opts_OnZitiServiceEvent(object sender, ZitiServiceEvent e) {
            var expected = (string)e.Context;
            try {
                var service = e.Added().First(s => {
                    return s.Name == expected;
                });
                service.Dial(onConnected, onData);
            } catch (Exception ex) {
                Console.WriteLine("ERROR: Could not find the service we want [" + expected + "]? " + ex.Message);
            }
        }

        private static void onConnected(ZitiConnection connection, OpenZiti.legacy.ZitiStatus status) {
            OpenZiti.legacy.ZitiUtil.CheckStatus(status);

            var cfg = connection.Service.GetConfiguration("weather-config-type");
            string where = null;
            if (cfg == null) {
                where = "London";
                Console.WriteLine("The service does not have a configuration of type 'weather-config-type' - using default: " + where);
            } else {
                where = JsonDocument.Parse(cfg).RootElement.GetProperty("where").ToString();
            }
            var bytes = Encoding.UTF8.GetBytes($"GET /{where} HTTP/1.0\r\n"
                                                + "Accept: *-/*\r\n"
                                                + "Connection: close\r\n"
                                                + "User-Agent: curl/7.59.0\r\n"
                                                + "Host: wttr.in\r\n"
                                                + "\r\n");

            connection.Write(bytes, afterDataWritten, "write context");
        }

        private static void afterDataWritten(ZitiConnection connection, OpenZiti.legacy.ZitiStatus status, object context) {
            OpenZiti.legacy.ZitiUtil.CheckStatus(status);
        }

        private static void onData(ZitiConnection connection, OpenZiti.legacy.ZitiStatus status, byte[] data) {
            if (status == OpenZiti.legacy.ZitiStatus.OK) {
                ms.Write(data); //collect all the bytes to display contiguously at the end of the program
            } else {
                if (status == OpenZiti.legacy.ZitiStatus.EOF) {
                    ConsoleHelper.OutputResponseToConsole(ms.ToArray());
                    Console.WriteLine("request completed: " + status.GetDescription());
                    connection.Close();
                    Environment.Exit(0);
                } else {
                    Console.WriteLine("unexpected error: " + status.GetDescription());
                }
                ConsoleHelper.OutputResponseToConsole(ms.ToArray());
            }
        }
    }
}

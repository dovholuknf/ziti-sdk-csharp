using Microsoft.VisualStudio.TestTools.UnitTesting;

using OpenZiti;
using OpenZiti.Management;
using System;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace Ziti.NET.Standard.Tests {
    [TestClass]
    public class UnitTest1 {
        
        [TestMethod]
        public void TestMethod1() {
            string beginMarker = "-----BEGIN CERTIFICATE-----";
            string endMarker = "-----END CERTIFICATE-----";

            string certsAsString = File.ReadAllText(@"c:\temp\certs.txt");

            string[] certs = certsAsString.Split(beginMarker);

            X509Certificate2Collection cas = new X509Certificate2Collection();
            X509Certificate2Collection intermediates = new X509Certificate2Collection();
            foreach (string cert in certs)
            {
                int end = cert.IndexOf(endMarker);
                if (end > 0)
                {
                    string c = beginMarker + cert.Substring(0, end) + endMarker;
                    var caCertificate = new X509Certificate2(System.Text.UTF8Encoding.UTF8.GetBytes(c));
                    if (caCertificate.Issuer == caCertificate.Subject)
                    {
                        if (!cas.Contains(caCertificate))
                        {
                            cas.Add(caCertificate);
                        }
                    } else
                    {
                        if (!intermediates.Contains(caCertificate))
                        {
                            intermediates.Add(caCertificate);
                        }
                    }
                } else
                {
                }
            }

            HttpClientHandler handler = new HttpClientHandler();
            
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, _) => {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(cas/* your custom root, add as many roots as you need */);
                chain.ChainPolicy.ExtraStore.AddRange(intermediates/* add any additional intermediate certs */);
                // any additional chain building settings you want

                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;



                return chain.Build(cert);
            };

            var logHandler = new LoggingHandler(handler);

            HttpClient client = new(logHandler);

            ManagementClient mc = new ManagementClient(client);
            mc.BaseUrl = "https://localhost:1280/edge/management/v1";
            

            Authenticate auth = new Authenticate();
            auth.Username = "admin";
            auth.Password = "admin";
            
            var env = mc.AuthenticateAsync(auth, Method.Password).Result;

            client.DefaultRequestHeaders.Add("zt-session", env.Data.Token);

            IdentityCreate idc = new();
            idc.Name = "csharp";
            idc.IsAdmin = false;
            idc.Type = IdentityType.User;
            try
            {
                var cr = mc.CreateIdentityAsync(idc).Result;

                var id = mc.DetailIdentityAsync(cr.Data.Id).Result;
                var jwt = id.Data.Enrollment.Ott.Jwt;
                mc.DeleteIdentityAsync(cr.Data.Id).Wait();
            }
            catch (Exception e)
            {
                if (e.InnerException != null && e.InnerException.GetType() == typeof(ApiException)){
                    ApiException ae = e.InnerException as ApiException;
                    Console.WriteLine(ae.Response);
                } else
                {
                    Console.WriteLine(e);
                    throw;
                }
            }
        }

        private void afterEnroll(ZitiStatus status) {
            throw new NotImplementedException();
        }
    }

    public class LoggingHandler : DelegatingHandler
    {
        public LoggingHandler(HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Console.WriteLine("Request:");
            Console.WriteLine(request.ToString());
            if (request.Content != null)
            {
                Console.WriteLine(await request.Content.ReadAsStringAsync());
            }
            Console.WriteLine();

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

            Console.WriteLine("Response:");
            Console.WriteLine(response.ToString());
            if (response.Content != null)
            {
                Console.WriteLine(await response.Content.ReadAsStringAsync());
            }
            Console.WriteLine();

            return response;
        }
    }
}

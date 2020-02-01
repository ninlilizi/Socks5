﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;
using WatsonDedupe;

namespace NKLI.DeDupeProxy
{
    public class TitaniumController
    {
        private readonly SemaphoreSlim @lock = new SemaphoreSlim(1);
        private ProxyServer proxyServer;
        private ExplicitProxyEndPoint explicitEndPoint;

        public static int RemotePort_Socks;


        //Watson DeDupe
        DedupeLibrary deDupe;
        static List<Chunk> Chunks;
        static string Key = "kjv";
        static List<string> Keys;
        static byte[] Data;

        static bool DebugDedupe = false;
        static bool DebugSql = false;
        static int NumObjects;
        static int NumChunks;
        static long LogicalBytes;
        static long PhysicalBytes;
        static decimal DedupeRatioX;
        static decimal DedupeRatioPercent;
        public static int maxObjectSizeHTTP;
        ///END Watson DeDupe
        ///

        public TitaniumController()
        {
            proxyServer = new ProxyServer();

            // Setup Root certificates for machine
            proxyServer.CertificateManager.RootCertificateIssuerName = "NKLI DeDupe Engine";
            proxyServer.CertificateManager.RootCertificateName = "NKLI Certificate Authority";
            proxyServer.CertificateManager.SaveFakeCertificates = true;
            proxyServer.CertificateManager.EnsureRootCertificate();

            //proxyServer.EnableHttp2 = true;

            // generate root certificate without storing it in file system
            //proxyServer.CertificateManager.CreateRootCertificate(false);

            //proxyServer.CertificateManager.TrustRootCertificate();
            //proxyServer.CertificateManager.TrustRootCertificateAsAdmin();

            proxyServer.ExceptionFunc = async exception =>
            {
                if (exception is ProxyHttpException phex)
                {
                    await writeToConsole(exception.Message + ": " + phex.InnerException?.Message, ConsoleColor.Red);
                }
                else
                {
                    await writeToConsole(exception.Message, ConsoleColor.Red);
                }
            };

            proxyServer.TcpTimeWaitSeconds = 10;
            proxyServer.ConnectionTimeOutSeconds = 15;
            proxyServer.ReuseSocket = false;
            proxyServer.EnableConnectionPool = false;
            proxyServer.ForwardToUpstreamGateway = true;
            proxyServer.CertificateManager.SaveFakeCertificates = true;
            //proxyServer.ProxyBasicAuthenticateFunc = async (args, userName, password) =>
            //{
            //    return true;
            //};

            // this is just to show the functionality, provided implementations use junk value
            //proxyServer.GetCustomUpStreamProxyFunc = onGetCustomUpStreamProxyFunc;
            //proxyServer.CustomUpStreamProxyFailureFunc = onCustomUpStreamProxyFailureFunc;

            // optionally set the Certificate Engine
            // Under Mono or Non-Windows runtimes only BouncyCastle will be supported
            //proxyServer.CertificateManager.CertificateEngine = Network.CertificateEngine.BouncyCastle;

            // optionally set the Root Certificate
            //proxyServer.CertificateManager.RootCertificate = new X509Certificate2("myCert.pfx", string.Empty, X509KeyStorageFlags.Exportable);

            //Watson DeDupe
            if (!Directory.Exists("Chunks")) Directory.CreateDirectory("Chunks");
            if (File.Exists("Test.db"))
            {
                deDupe = new DedupeLibrary("Test.db", WriteChunk, ReadChunk, DeleteChunk, DebugDedupe, DebugSql);
            }
            else
            {
                deDupe = new DedupeLibrary("Test.db", 32768, 262144, 2048, 2, WriteChunk, ReadChunk, DeleteChunk, DebugDedupe, DebugSql);
            }

            Console.Write(Environment.NewLine + "-------------------------" + Environment.NewLine + "DeDupe Engine Initialized" + Environment.NewLine + "-------------------------" + Environment.NewLine);

            //int maxSizeMB = 50;
            //maxObjectSizeHTTP = maxSizeMB * 1024 * 1024;
            //Console.Write("<DeDupe> Maximum system supported HTTP object size:" + (Int32.MaxValue / 1024 / 1024) + "Mb / Maximum HTTP object size:" + (maxObjectSizeHTTP / 1024 / 1024) + "Mb" + Environment.NewLine);

            // Gather index and dedupe stats
            if (deDupe.IndexStats(out NumObjects, out NumChunks, out LogicalBytes, out PhysicalBytes, out DedupeRatioX, out DedupeRatioPercent))
            {
                Console.WriteLine("  [Objects:" + NumObjects + "]/[Chunks:" + NumChunks + "] - [Logical:" + String.Format("{0:n0}", (LogicalBytes / 1024)) + "Kb]/[Physical:" + String.Format("{0:n0}", (PhysicalBytes / 1024)) +"Kb] + [Ratio:" + Math.Round(DedupeRatioPercent, 4) + "%]");
                //Console.WriteLine("  Dedupe ratio     : " + DedupeRatioX + "X, " + DedupeRatioPercent + "%");
                Console.WriteLine("-------------------------");
            }
            ///END Watson DeDupe
        }

        public void StartProxy(int listenPort, bool useSocksRelay, int socksProxyPort)
        {
            RemotePort_Socks = socksProxyPort;

            proxyServer.BeforeRequest += onRequest;
            proxyServer.BeforeResponse += onResponse;
            proxyServer.AfterResponse += onAfterResponse;

            proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;

            //proxyServer.EnableWinAuth = true;

            explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, listenPort);

            // Fired when a CONNECT request is received
            explicitEndPoint.BeforeTunnelConnectRequest += onBeforeTunnelConnectRequest;
            explicitEndPoint.BeforeTunnelConnectResponse += onBeforeTunnelConnectResponse;

            // An explicit endpoint is where the client knows about the existence of a proxy
            // So client sends request in a proxy friendly manner
            proxyServer.AddEndPoint(explicitEndPoint);
            proxyServer.Start();

            // Transparent endpoint is useful for reverse proxy (client is not aware of the existence of proxy)
            // A transparent endpoint usually requires a network router port forwarding HTTP(S) packets or DNS
            // to send data to this endPoint
            //var transparentEndPoint = new TransparentProxyEndPoint(IPAddress.Any, 443, true)
            //{ 
            //    // Generic Certificate hostname to use
            //    // When SNI is disabled by client
            //    GenericCertificateName = "google.com"
            //};

            //proxyServer.AddEndPoint(transparentEndPoint);
            //proxyServer.UpStreamHttpProxy = new ExternalProxy("localhost", 8888);
            //proxyServer.UpStreamHttpsProxy = new ExternalProxy("localhost", 8888);

            // SOCKS proxy
            if (useSocksRelay)
            {
                proxyServer.UpStreamHttpProxy = new ExternalProxy("127.0.0.1", socksProxyPort)
                { ProxyType = ExternalProxyType.Socks5, UserName = "User1", Password = "Pass" };
                proxyServer.UpStreamHttpsProxy = new ExternalProxy("127.0.0.1", socksProxyPort)
                { ProxyType = ExternalProxyType.Socks5, UserName = "User1", Password = "Pass" };
            }

            //var socksEndPoint = new SocksProxyEndPoint(IPAddress.Any, 1080, true)
            //{
            //    // Generic Certificate hostname to use
            //    // When SNI is disabled by client
            //    GenericCertificateName = "google.com"
            //};

            //proxyServer.AddEndPoint(socksEndPoint);

            foreach (var endPoint in proxyServer.ProxyEndPoints)
            {
                Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ", endPoint.GetType().Name,
                    endPoint.IpAddress, endPoint.Port);
            }

            // Only explicit proxies can be set as system proxy!
            //proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
            //proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
            if (RunTime.IsWindows)
            {
                proxyServer.SetAsSystemProxy(explicitEndPoint, ProxyProtocolType.AllHttp);
            }
        }

        public void Stop()
        {
            explicitEndPoint.BeforeTunnelConnectRequest -= onBeforeTunnelConnectRequest;
            explicitEndPoint.BeforeTunnelConnectResponse -= onBeforeTunnelConnectResponse;

            proxyServer.BeforeRequest -= onRequest;
            proxyServer.BeforeResponse -= onResponse;
            proxyServer.ServerCertificateValidationCallback -= OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback -= OnCertificateSelection;

            proxyServer.Stop();

            // remove the generated certificates
            proxyServer.CertificateManager.RemoveTrustedRootCertificate();
        }

        private async Task<IExternalProxy> onGetCustomUpStreamProxyFunc(SessionEventArgsBase arg)
        {
            arg.GetState().PipelineInfo.AppendLine(nameof(onGetCustomUpStreamProxyFunc));

            // this is just to show the functionality, provided values are junk
            return new ExternalProxy
            {
                BypassLocalhost = true,
                HostName = "127.0.0.1",
                Port = RemotePort_Socks,
                ProxyType = ExternalProxyType.Socks5,
                //Password = "fake",
                //UserName = "fake",
                //UseDefaultCredentials = false
            };
        }

        private async Task<IExternalProxy> onCustomUpStreamProxyFailureFunc(SessionEventArgsBase arg)
        {
            arg.GetState().PipelineInfo.AppendLine(nameof(onCustomUpStreamProxyFailureFunc));

            // this is just to show the functionality, provided values are junk
            return new ExternalProxy
            {
                BypassLocalhost = true,
                HostName = "127.0.0.1",
                Port = RemotePort_Socks,
                ProxyType = ExternalProxyType.Socks5,
                //Password = "fake2",
                //UserName = "fake2",
                //UseDefaultCredentials = false
            };
        }

        private async Task onBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            string hostname = e.HttpClient.Request.RequestUri.Host;
            e.GetState().PipelineInfo.AppendLine(nameof(onBeforeTunnelConnectRequest) + ":" + hostname);
            await writeToConsole("Tunnel to: " + hostname);

            var clientLocalIp = e.ClientLocalEndPoint.Address;
            if (!clientLocalIp.Equals(IPAddress.Loopback) && !clientLocalIp.Equals(IPAddress.IPv6Loopback))
            {
                e.HttpClient.UpStreamEndPoint = new IPEndPoint(clientLocalIp, 0);
            }


            // Don't decrypt these domains
            var list = new List<string> { "dropbox.com", "boxcryptor.com", "google.com" };

            foreach (string value in list)
            {
                if (hostname.Contains(value))
                {
                    // Exclude Https addresses you don't want to proxy
                    // Useful for clients that use certificate pinning
                    // for example dropbox.com
                    e.DecryptSsl = false;
                }
            }

        }

        private void WebSocket_DataSent(object sender, DataEventArgs e)
        {
            var args = (SessionEventArgs)sender;
            WebSocketDataSentReceived(args, e, true);
        }

        private void WebSocket_DataReceived(object sender, DataEventArgs e)
        {
            var args = (SessionEventArgs)sender;
            WebSocketDataSentReceived(args, e, false);
        }

        private void WebSocketDataSentReceived(SessionEventArgs args, DataEventArgs e, bool sent)
        {
            var color = sent ? ConsoleColor.Green : ConsoleColor.Blue;

            foreach (var frame in args.WebSocketDecoder.Decode(e.Buffer, e.Offset, e.Count))
            {
                if (frame.OpCode == WebsocketOpCode.Binary)
                {
                    var data = frame.Data.ToArray();
                    string str = string.Join(",", data.ToArray().Select(x => x.ToString("X2")));
                    writeToConsole(str, color).Wait();
                }

                if (frame.OpCode == WebsocketOpCode.Text)
                {
                    writeToConsole(frame.GetText(), color).Wait();
                }
            }
        }

        private Task onBeforeTunnelConnectResponse(object sender, TunnelConnectSessionEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(onBeforeTunnelConnectResponse) + ":" + e.HttpClient.Request.RequestUri);

            return Task.CompletedTask;
        }

        // intercept & cancel redirect or update requests
        private async Task onRequest(object sender, SessionEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(onRequest) + ":" + e.HttpClient.Request.RequestUri);

            var clientLocalIp = e.ClientLocalEndPoint.Address;
            if (!clientLocalIp.Equals(IPAddress.Loopback) && !clientLocalIp.Equals(IPAddress.IPv6Loopback))
            {
                e.HttpClient.UpStreamEndPoint = new IPEndPoint(clientLocalIp, 0);
            }

            /*if (e.HttpClient.Request.Url.Contains("yahoo.com"))
            {
                e.CustomUpStreamProxy = new ExternalProxy("localhost", 8888);
            }*/

            await writeToConsole("Active Client Connections:" + ((ProxyServer)sender).ClientConnectionCount);
            await writeToConsole(e.HttpClient.Request.Url);

            // store it in the UserData property
            // It can be a simple integer, Guid, or any type
            //e.UserData = new CustomUserData()
            //{
            //    RequestHeaders = e.HttpClient.Request.Headers,
            //    RequestBody = e.HttpClient.Request.HasBody ? e.HttpClient.Request.Body:null,
            //    RequestBodyString = e.HttpClient.Request.HasBody? e.HttpClient.Request.BodyString:null
            //};

            ////This sample shows how to get the multipart form data headers
            //if (e.HttpClient.Request.Host == "mail.yahoo.com" && e.HttpClient.Request.IsMultipartFormData)
            //{
            //    e.MultipartRequestPartSent += MultipartRequestPartSent;
            //}

            // To cancel a request with a custom HTML content
            // Filter URL
            //if (e.HttpClient.Request.RequestUri.AbsoluteUri.Contains("yahoo.com"))
            //{ 
            //    e.Ok("<!DOCTYPE html>" +
            //          "<html><body><h1>" +
            //          "Website Blocked" +
            //          "</h1>" +
            //          "<p>Blocked by titanium web proxy.</p>" +
            //          "</body>" +
            //          "</html>");
            //} 

            ////Redirect example
            //if (e.HttpClient.Request.RequestUri.AbsoluteUri.Contains("wikipedia.org"))
            //{ 
            //   e.Redirect("https://www.paypal.com");
            //} 
        }

        // Modify response
        private async Task multipartRequestPartSent(object sender, MultipartRequestPartSentEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(multipartRequestPartSent));

            var session = (SessionEventArgs)sender;
            await writeToConsole("Multipart form data headers:");
            foreach (var header in e.Headers)
            {
                await writeToConsole(header.ToString());
            }
        }

        private async Task onResponse(object sender, SessionEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(onResponse));
            //e.WebSession.pi

            if (e.HttpClient.ConnectRequest?.TunnelType == TunnelType.Websocket)
            {
                e.DataSent += WebSocket_DataSent;
                e.DataReceived += WebSocket_DataReceived;
            }

            await writeToConsole("Active Server Connections:" + ((ProxyServer)sender).ServerConnectionCount);

            string ext = System.IO.Path.GetExtension(e.HttpClient.Request.RequestUri.AbsolutePath);

            // access user data set in request to do something with it
            //var userData = e.HttpClient.UserData as CustomUserData;

            //if (ext == ".gif" || ext == ".png" || ext == ".jpg")
            //{ 
            //    byte[] btBody = Encoding.UTF8.GetBytes("<!DOCTYPE html>" +
            //                                           "<html><body><h1>" +
            //                                           "Image is blocked" +
            //                                           "</h1>" +
            //                                           "<p>Blocked by Titanium</p>" +
            //                                           "</body>" +
            //                                           "</html>");

            //    var response = new OkResponse(btBody);
            //    response.HttpVersion = e.HttpClient.Request.HttpVersion;

            //    e.Respond(response);
            //    e.TerminateServerConnection();
            //} 

            //// print out process id of current session
            Console.WriteLine($"PID: {e.HttpClient.ProcessId.Value}");


            //if (!e.ProxySession.Request.Host.Equals("medeczane.sgk.gov.tr")) return;
            //if (!e.HttpClient.Request.Host.Equals("www.gutenberg.org")) return;
            if (e.HttpClient.Request.Method == "GET" || e.HttpClient.Request.Method == "POST")
            { 
                if (e.HttpClient.Response.StatusCode == (int)HttpStatusCode.OK)
                {
                    //string Key = e.HttpClient.Request.Url;


                    //var bodyBytes = await e.GetResponseBody();


                    //Console.Write("#" +Key + "#" + bodyBytes.Length + "#");

                    // Store received object into DeDuped cache
                    deDupe.StoreOrReplaceObject(e.HttpClient.Request.Url, await e.GetResponseBody(), out Chunks);
                    Console.WriteLine("<Dedupe> Titanium stored object: " + e.HttpClient.Request.Url);
                    Console.WriteLine("<Dedupe> [Objects:" + NumObjects + "]/[Chunks:" + NumChunks + "] - [Logical:" + String.Format("{0:n0}", (LogicalBytes / 1024)) + "Kb]/[Physical:" + String.Format("{0:n0}", (PhysicalBytes / 1024)) + "Kb] + [Ratio:" + Math.Round(DedupeRatioPercent, 4) + "%]");

                    /*if (e.HttpClient.Response.ContentType != null && e.HttpClient.Response.ContentType.Trim().ToLower().Contains("text/html"))
                    {
                        var bodyBytes = await e.GetResponseBody();
                        e.SetResponseBody(TitaniumHelper.ToByteArray("M"));

                        string body = await e.GetResponseBodyAsString();
                        e.SetResponseBodyString("M");

                        
                    }*/
                }
            } 
        }

        private async Task onAfterResponse(object sender, SessionEventArgs e)
        {
            await writeToConsole($"Pipelineinfo: {e.GetState().PipelineInfo}", ConsoleColor.Yellow);

        }

        /// <summary>
        ///     Allows overriding default certificate validation logic
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(OnCertificateValidation));

            // set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == SslPolicyErrors.None)
            {
                e.IsValid = true;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        ///     Allows overriding default client certificate selection logic during mutual authentication
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateSelection(object sender, CertificateSelectionEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(OnCertificateSelection));

            // set e.clientCertificate to override

            return Task.CompletedTask;
        }

        private async Task writeToConsole(string message, ConsoleColor? consoleColor = null)
        {
            await @lock.WaitAsync();

            if (consoleColor.HasValue)
            {
                ConsoleColor existing = Console.ForegroundColor;
                Console.ForegroundColor = consoleColor.Value;
                Console.WriteLine(message);
                Console.ForegroundColor = existing;
            }
            else
            {
                Console.WriteLine(message);
            }

            @lock.Release();
        }


        ///// <summary>
        ///// User data object as defined by user.
        ///// User data can be set to each SessionEventArgs.HttpClient.UserData property
        ///// </summary>
        //public class CustomUserData
        //{
        //    public HeaderCollection RequestHeaders { get; set; }
        //    public byte[] RequestBody { get; set; }
        //    public string RequestBodyString { get; set; }
        //}


        //
        #region Watson DeDupe

        bool WriteChunk(Chunk data)
        {
            File.WriteAllBytes("Chunks\\" + data.Key, data.Value);
            using (var fs = new FileStream(
                "Chunks\\" + data.Key,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                0x1000,
                FileOptions.WriteThrough))
            {
                fs.Write(data.Value, 0, data.Value.Length);
            }
            return true;
        }
        byte[] ReadChunk(string key)
        {
            return File.ReadAllBytes("Chunks\\" + key);
        }

        bool DeleteChunk(string key)
        {
            try
            {
                File.Delete("Chunks\\" + key);
            }
            catch (Exception)
            {

            }
            return true;
        }
        #endregion
        //END

    }

    // Extension helper classes
    public class TitaniumClientState
    {
        public StringBuilder PipelineInfo { get; } = new StringBuilder();
    }

    public static class ProxyEventArgsBaseExtensions
    {
        public static TitaniumClientState GetState(this ProxyEventArgsBase args)
        {
            if (args.ClientUserData == null)
            {
                args.ClientUserData = new TitaniumClientState();
            }

            return (TitaniumClientState)args.ClientUserData;
        }
    }

    public static class TitaniumHelper
    {
        public static byte[] ToByteArray(this string str)
        {
            return System.Text.Encoding.ASCII.GetBytes(str);
        }
    }
}

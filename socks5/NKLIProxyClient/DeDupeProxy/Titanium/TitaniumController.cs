﻿using Caching;
using DiskQueue;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
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

        public static List<string> dontDecrypt;

        public static IPersistentQueue chunkQueue;

        struct ObjectStruct
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string URI;
            [MarshalAs(UnmanagedType.SafeArray)]
            public byte[] Headers;
            [MarshalAs(UnmanagedType.SafeArray)]
            public byte[] Body;
        };


        // Watson DeDupe
        LRUCache<string, byte[]> memoryCache;
        FIFOCache<string, byte[]> writeCache;
        //END

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
        //END

        public TitaniumController()
        {
            proxyServer = new ProxyServer();

            // Setup Root certificates for machine
            proxyServer.CertificateManager.RootCertificateIssuerName = "NKLI DeDupe Engine";
            proxyServer.CertificateManager.RootCertificateName = "NKLI Certificate Authority";
            proxyServer.CertificateManager.SaveFakeCertificates = true;
            proxyServer.CertificateManager.EnsureRootCertificate();

            // Certificate trust is required to avoid host authentication errors!
            proxyServer.CertificateManager.TrustRootCertificate(true);
            proxyServer.CertificateManager.TrustRootCertificateAsAdmin(true);


            proxyServer.EnableHttp2 = true;

            // generate root certificate without storing it in file system
            //proxyServer.CertificateManager.CreateRootCertificate(false);


            // Fancy exception handling
            proxyServer.ExceptionFunc = async exception =>
            {
                if (exception is ProxyHttpException phex)
                {
                    await writeToConsole(exception.Message + ": " + phex.InnerException?.Message + Environment.NewLine + exception.StackTrace, ConsoleColor.Red);
                    foreach (var pair in exception.Data)
                    {
                        await writeToConsole(pair.ToString(), ConsoleColor.Red);
                    }
                }
                else
                {
                    await writeToConsole(exception.Message + Environment.NewLine + exception.StackTrace, ConsoleColor.Red);
                    foreach (var pair in exception.Data)
                    {
                        await writeToConsole(pair.ToString(), ConsoleColor.Red);
                    }
                }
            };

            proxyServer.TcpTimeWaitSeconds = 10;
            proxyServer.ConnectionTimeOutSeconds = 15;
            proxyServer.ReuseSocket = true;
            proxyServer.EnableConnectionPool = true;
            proxyServer.ForwardToUpstreamGateway = true;
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




            //Watson Cache: 1600 entries * 262144 max chunk size = Max 400Mb memory size
            memoryCache = new LRUCache<string, byte[]>(1600, 100, false);
            writeCache = new FIFOCache<string, byte[]>(1600, 100, false);



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

            int maxSizeMB = 50;
            maxObjectSizeHTTP = maxSizeMB * 1024 * 1024;
            Console.Write("<DeDupe> Maximum system supported HTTP object size:" + (Int32.MaxValue / 1024 / 1024) + "Mb / Maximum HTTP object size:" + (maxObjectSizeHTTP / 1024 / 1024) + "Mb" + Environment.NewLine);

            // Gather index and dedupe stats
            if (deDupe.IndexStats(out NumObjects, out NumChunks, out LogicalBytes, out PhysicalBytes, out DedupeRatioX, out DedupeRatioPercent))
            {
                Console.WriteLine("  [Objects:" + NumObjects + "]/[Chunks:" + NumChunks + "] - [Logical:" + String.Format("{0:n0}", (LogicalBytes / 1024)) + "Kb]/[Physical:" + String.Format("{0:n0}", (PhysicalBytes / 1024)) + "Kb] + [Ratio:" + Math.Round(DedupeRatioPercent, 4) + "%]");
                //Console.WriteLine("  Dedupe ratio     : " + DedupeRatioX + "X, " + DedupeRatioPercent + "%");
                Console.WriteLine("-------------------------");
            }
            ///END Watson DeDupe
        }

        public void StartProxy(int listenPort, bool useSocksRelay, int socksProxyPort)
        {
            // THREAD - Disk write queue
            var threadWriteChunks = new Thread(async () =>
            {
                string decodeKey;
                while (true)
                {
                    while (writeCache.Count() != 0)
                    {
                        try
                        {
                            decodeKey = writeCache.Oldest();
                        }
                        catch
                        {
                            decodeKey = null;
                            await writeToConsole("<Titanium> Expectedly ran out of keys in write cache", ConsoleColor.Red);
                        }

                        if (writeCache.TryGet(decodeKey, out byte[] Chunk))
                        {
                            // Then commit to disk
                            File.WriteAllBytes("Chunks\\" + decodeKey, Chunk);
                            using (var fs = new FileStream(
                                "Chunks\\" + decodeKey,
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.None,
                                0x1000,
                                FileOptions.WriteThrough))
                            {
                                fs.Write(Chunk, 0, Chunk.Length);
                            }

                            writeCache.Remove(decodeKey);
                        }
                        Thread.Sleep(100);
                    }
                    Thread.Sleep(500);
                }
            } );

            // THREAD - Deduplication queue
            var threadDeDupe = new Thread(async () => {
                while (true)
                {
                    using (var session = chunkQueue.OpenSession())
                    {
                        var data = session.Dequeue();
                        if (data == null) { Thread.Sleep(500); continue; }

                        try
                        {
                            ObjectStruct chunkStruct = fromBytes(data);
                            session.Flush();

                            try
                            {
                                deDupe.StoreObject(chunkStruct.URI + "Body", chunkStruct.Body, out Chunks);
                                deDupe.StoreObject(chunkStruct.URI + "Headers", chunkStruct.Headers, out Chunks);
                                
                            }
                            catch { await writeToConsole("<Titanium> Dedupilication attempt failed, URI:" + chunkStruct.URI, ConsoleColor.Red); }

                            await writeToConsole("<DeDupe> [Titanium] stored object Size:" + String.Format("{0:n2}", (chunkStruct.Body.Length / 1024)) + "Kb Chunks:" + Chunks.Count + " URI:" + chunkStruct.URI, ConsoleColor.Yellow);
                            // Gather index and dedupe stats

                            if (deDupe.IndexStats(out NumObjects, out NumChunks, out LogicalBytes, out PhysicalBytes, out DedupeRatioX, out DedupeRatioPercent))
                                await writeToConsole("<DeDupe> [Objects:" + NumObjects + "]/[Chunks:" + NumChunks + "] - [Logical:" + String.Format("{0:n0}", (LogicalBytes / 1024)) + "Kb]/[Physical:" + String.Format("{0:n0}", (PhysicalBytes / 1024)) + "Kb] + [Ratio:" + Math.Round(DedupeRatioPercent, 4) + "%]", ConsoleColor.Yellow);

                            //session.Flush();
                        }
                        catch (Exception err)
                        {
                            await writeToConsole("Unhandled exception in thread." + err, ConsoleColor.Red);
                            continue;
                        }
                    }
                }
            });

            // We prepare the DeDuplication queue before starting the worker threads
            if (File.Exists("chunkQueue//lock")) DeleteChildren("chunkQueue", true);
            chunkQueue = new PersistentQueue("chunkQueue");
            chunkQueue.Internals.ParanoidFlushing = false;

            threadWriteChunks.IsBackground = true;
            threadWriteChunks.Start();

            threadDeDupe.IsBackground = true;
            threadDeDupe.Start();




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

            // 
            /*proxyServer.SupportedSslProtocols =
                System.Security.Authentication.SslProtocols.Ssl2 |
                System.Security.Authentication.SslProtocols.Ssl3 |
                System.Security.Authentication.SslProtocols.Tls |
                System.Security.Authentication.SslProtocols.Tls11 |
                System.Security.Authentication.SslProtocols.Tls12 |
                System.Security.Authentication.SslProtocols.Tls13;*/

            // For speed
            proxyServer.CheckCertificateRevocation = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;

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

            // Don't decrypt these domains
            dontDecrypt = new List<string> { "plex.direct", "dropbox.com", "boxcryptor.com", "google.com" };

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

            chunkQueue.Dispose();
            DeleteChildren("chunkQueue", true);

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

            foreach (string value in dontDecrypt)
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
            /*try
            {
                e.UserData = new CustomUserData()
                {
                    RequestBody = e.HttpClient.Request.HasBody ? e.HttpClient.Request.Body : null,
                    //RequestBodyString = e.HttpClient.Request.HasBody ? e.HttpClient.Request.BodyString : null,
                    RequestMethod = e.HttpClient.Request.Method
                };
            }
            catch { await writeToConsole("<Titanium> (onRequest) Error = new CustomUserData", ConsoleColor.Red); }*/



            ////This sample shows how to get the multipart form data headers
            //if (e.HttpClient.Request.Host == "mail.yahoo.com" && e.HttpClient.Request.IsMultipartFormData)
            //{
            //    e.MultipartRequestPartSent += MultipartRequestPartSent;
            //}


            // We only attempt to replay from cache if cached headers also exist.
            if (deDupe.ObjectExists(e.HttpClient.Request.Url + "Headers"))
            {
                try
                {
                    deDupe.RetrieveObject(e.HttpClient.Request.Url + "Headers", out byte[] headerData);

                    // Convert byte[] back into dictionary
                    MemoryStream mStream = new MemoryStream();
                    BinaryFormatter binFormatter = new BinaryFormatter();
                    mStream.Write(headerData, 0, headerData.Length);
                    mStream.Position = 0;
                    Dictionary<string, string> restoredHeader = binFormatter.Deserialize(mStream) as Dictionary<string, string>;

                    // Complain if dictionary is unexpectedly empty
                    if (restoredHeader.Count == 0)
                    {
                        await writeToConsole("<Titanium> (onRequest) Cache deserialization resulted in 0 headers", ConsoleColor.Red);
                    }
                    else
                    {
                        // Convert dictionary into response format
                        Dictionary<string, HttpHeader> headerDictionary = new Dictionary<string, HttpHeader>();
                        foreach (var pair in restoredHeader)
                        {
                            //await writeToConsole("Key:" + pair.Key + " Value:" + pair.Value, ConsoleColor.Green);
                            e.HttpClient.Response.Headers.AddHeader(new HttpHeader(pair.Key, pair.Value));
                            headerDictionary.Add(pair.Key, new HttpHeader(pair.Key, pair.Value));
                        }
                        try
                        {
                            // Check matching body exists and retrive
                            if (deDupe.ObjectExists(e.HttpClient.Request.Url + "Body"))
                            {
                                await writeToConsole("<Titanium> found object, Size:" + " URI:" + e.HttpClient.Request.Url, ConsoleColor.Cyan);
                                deDupe.RetrieveObject(e.HttpClient.Request.Url + "Body", out byte[] objectData);

                                // Cancel request and respond cached data
                                try { e.Ok(objectData, headerDictionary); }
                                catch { await writeToConsole("<Titanium> (onRequest) Failure while attempting to send recontructed request", ConsoleColor.Red); }
                            }
                        }
                        catch { await writeToConsole("<Titanium> (onRequest) Failure while attempting to restore cached object", ConsoleColor.Red); }
                    }
                }
                catch { await writeToConsole("<Titanium> (onRequest) Failure while attempting to restore cached headers", ConsoleColor.Red); }
            }     


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

            if (e.HttpClient.ConnectRequest?.TunnelType == TunnelType.Websocket)
            {
                e.DataSent += WebSocket_DataSent;
                e.DataReceived += WebSocket_DataReceived;
            }

            await writeToConsole("Active Server Connections:" + ((ProxyServer)sender).ServerConnectionCount);

            //string ext = System.IO.Path.GetExtension(e.HttpClient.Request.RequestUri.AbsolutePath);

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
            //Console.WriteLine($"PID: {e.HttpClient.ProcessId.Value}");


            //if (!e.ProxySession.Request.Host.Equals("medeczane.sgk.gov.tr")) return;
            //if (!e.HttpClient.Request.Host.Equals("www.gutenberg.org")) return;
            if (e.HttpClient.Request.Method == "GET" || e.HttpClient.Request.Method == "POST")
            {
                if (e.HttpClient.Response.StatusCode == (int)HttpStatusCode.OK)
                {
                    byte[] output = await e.GetResponseBody();

                    string Key = e.HttpClient.Request.Url;

                    // Respect cache control headers
                    HttpHeader cacheControl = e.HttpClient.Response.Headers.GetFirstHeader("Cache-Control");
                    if (cacheControl.Value.Contains("no-cache") || cacheControl.Value.Contains("no-store"))
                    {
                        await writeToConsole("<DeDupe> [Titanium] Respecting no-cache header for key:" + Key, ConsoleColor.DarkYellow);

                        if (deDupe.ObjectExists(Key + "Body"))
                        {
                            deDupe.DeleteObject(Key + "Body");
                            deDupe.DeleteObject(Key + "Headers");
                        }
                    }
                    else
                    {

                        try
                        {
                            // For now we only want to avoid caching range-requests and put a max size on incoming objects
                            if ( (output.Count() == e.HttpClient.Response.ContentLength) && (maxObjectSizeHTTP > e.HttpClient.Response.ContentLength) )
                            {
                                //Struct for marshal transform
                                ObjectStruct responseStruct = new ObjectStruct();


                                // If no headers than don't even bother
                                if (e.HttpClient.Response.Headers.Count() != 0)
                                {
                                    // Serialize headers
                                    Dictionary<string, string> headerDictionary = new Dictionary<string, string>();
                                    IEnumerator<HttpHeader> headerEnumerator = e.HttpClient.Response.Headers.GetEnumerator();
                                    while (headerEnumerator.MoveNext()) headerDictionary.Add(headerEnumerator.Current.Name, headerEnumerator.Current.Value);
                                    var binFormatter = new BinaryFormatter();
                                    var mStream = new MemoryStream();
                                    binFormatter.Serialize(mStream, headerDictionary);

                                    responseStruct.Headers = mStream.ToArray();

                                    // Store response body in cache provided it's the expected length
                                    try
                                    {
                                        responseStruct.URI = e.HttpClient.Request.Url;
                                        responseStruct.Headers = mStream.ToArray();
                                        responseStruct.Body = output;

                                        byte[] responseOutput = getBytes(responseStruct);

                                        IPersistentQueueSession sessionQueue = chunkQueue.OpenSession();
                                        sessionQueue.Enqueue(responseOutput);
                                        sessionQueue.Flush();
                                    }
                                    catch { await writeToConsole("<Titanium> Unable to write response body to cache", ConsoleColor.Red); }
                                }
                            }
                        }
                        catch
                        {
                            //throw new Exception("Unable to output headers to cache");
                            await writeToConsole("<DeDupe> (Titanium) Unable to output headers to cache", ConsoleColor.Red);
                        }


                    }

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
            // This is the massive console spam!
            //await writeToConsole($"Pipelineinfo: {e.GetState().PipelineInfo}", ConsoleColor.Magenta);

            // access user data set in request to do something with it
            //var userData = e.HttpClient.UserData as CustomUserData;
        }

        /// <summary>
        ///     Allows overriding default certificate validation logic
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(OnCertificateValidation));

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
        public class CustomUserData
        {
            public byte[] RequestBody { get; set; }
            public byte[] ResponseBody { get; set; }
            //public string RequestBodyString { get; set; }
            public string RequestMethod { get; set; }
            public int ResponsePosition { get; set; }
        }


        //
        #region Watson DeDupe

        bool WriteChunk(Chunk data)
        {
            // First commit to memory cache
            try
            {
                writeCache.AddReplace(data.Key, data.Value);
                memoryCache.AddReplace(data.Key, data.Value);
            }
            catch
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("<Titanium> ERROR writing to memory cache");
                Console.ForegroundColor = ConsoleColor.White;
            }
            return true;
        }
        byte[] ReadChunk(string key)
        {
            // Is possible return from memory
            if (!memoryCache.TryGet(key, out byte[] data))
            {
                byte[] readData = File.ReadAllBytes("Chunks\\" + key);
                // Copy back read chunks to memory cache
                memoryCache.AddReplace(Key, readData);
                return readData;
            }
            else
            {
                return data;
            }
        }

        bool DeleteChunk(string key)
        {
            try
            {
                // First delete from memory
                memoryCache.Remove(key);
                // Then delete from disk
                File.Delete("Chunks\\" + key);
            }
            catch (Exception)
            {

            }
            return true;
        }
        #endregion
        //END

        //
        #region Struct-ByteArray Conversion
        byte[] getBytes(ObjectStruct str)
        {
            int size = Marshal.SizeOf(str);
            byte[] arr = new byte[size];

            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(str, ptr, false);
            Marshal.Copy(ptr, arr, 0, size);
            Marshal.FreeHGlobal(ptr);
            return arr;
        }

        ObjectStruct fromBytes(byte[] arr)
        {
            try
            {
                ObjectStruct str = new ObjectStruct();

                int size = Marshal.SizeOf(str);
                IntPtr ptr = Marshal.AllocHGlobal(size);

                Marshal.Copy(arr, 0, ptr, size);

                str = (ObjectStruct)Marshal.PtrToStructure(ptr, str.GetType());
                Marshal.FreeHGlobal(ptr);

                return str;
            }
            catch (System.AccessViolationException)
            {
                Console.WriteLine("DeSuplication queue corrupt, recreating!");

                chunkQueue.Dispose();

                if (DeleteChildren("chunkQueue", true))
                {
                    chunkQueue = new PersistentQueue("chunkQueue");
                }
                else
                {
                    Console.Write("Recreation of DeDuplication queue failed!");
                }

                return new ObjectStruct();
            }

            
        }
        #endregion
        //

        static bool DeleteChildren(string directory, bool recursive)
        {
            var deletedAll = true;

            //Recurse if needed
            if (recursive)
            {
                //Enumerate the child directories
                foreach (var child in Directory.GetDirectories(directory))
                {
                    //Assume there are no locked files and just delete the directory - happy path
                    if (!TryDeleteDirectory(child))
                    {
                        //Couldn't delete it so we have to delete the children individually
                        if (!DeleteChildren(child, true))
                            deletedAll = false;
                    };
                };
            };

            //Now delete the files in the current directory
            foreach (var file in Directory.GetFiles(directory))
            {
                if (!TryDeleteFile(file))
                    deletedAll = false;
            };

            return deletedAll;
        }

        static bool TryDeleteDirectory(string filePath)
        {
            try
            {
                Directory.Delete(filePath, true);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }; //May need to add more exceptions to catch - DO NOT use a wildcard catch (e.g. Exception)
        }

        static bool TryDeleteFile(string filePath)
        {
            try
            {
                File.Delete(filePath);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }; //May need to add more exceptions to catch - DO NOT use a wildcard catch (e.g. Exception)
        }

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

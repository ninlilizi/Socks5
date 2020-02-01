/*
    Socks5 - A full-fledged high-performance socks5 proxy server written in C#. Plugin support included.
    Copyright (C) 2016 ThrDev

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using socks5.HTTP;
using socks5.Plugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using WatsonDedupe;
using xxHashSharp;

namespace socks5.ExamplePlugins
{
    public class DataHandlerDeDupe : DataHandler
    {
        //Watson DeDupe
        DedupeLibrary Dedupe;
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
        ///END Watson DeDupe
        ///

        public static int maxObjectSizeHTTP;
        
        public static int packetSize = 4096;
        public static int stripeSize = 131072;
        public static ReadWriteBuffer stripeBuffer;

        static RequestData requestData;
        public struct RequestData
        {
            public bool isHTTP;
            public bool isChunked;
            public bool isHeader;
            public int contentLength;
            public int currentposition;
            public int startPayload;
            public double totalPackets;
            public double currentRound;
            public double remainingRounds;
            public string host;
            public System.Net.IPAddress ip;
            public string uRI;
            public int port;
            public int packetSize;

            public int Key;

            public DedupeLibrary dedupeStream;
            public DedupeStream cacheStream;

            public int requestType;
            public enum RequestType
            {
                UNDEF = 0,
                NONE = 1,
                GET = 2,
                PUT = 3,
                POST = 4
            }
        }



        string replaceWith = "X-Requested-With: NKLI-DeDupe-Engine";

        public override bool OnStart()
        {
            //Watson DeDupe
            if (!Directory.Exists("Chunks")) Directory.CreateDirectory("Chunks");
            if (File.Exists("Test.db"))
            {
                Dedupe = new DedupeLibrary("Test.db", WriteChunk, ReadChunk, DeleteChunk, DebugDedupe, DebugSql);
            }
            else
            {
                Dedupe = new DedupeLibrary("Test.db", 64, 131072, 64, 2, WriteChunk, ReadChunk, DeleteChunk, DebugDedupe, DebugSql);
            }
            ///END Watson DeDupe
            ///

            // Buffer to maximise search size
            //stripeBuffer = new ReadWriteBuffer(stripeSize);

            Console.Write(Environment.NewLine + "-------------------------" + Environment.NewLine + "DeDupe Engine Initialized" + Environment.NewLine + "-------------------------" + Environment.NewLine);

            int maxSizeMB = 50;
            maxObjectSizeHTTP = maxSizeMB * 1024 * 1024;
            Console.Write("<DeDupe> Maximum system supported HTTP object size:" + (Int32.MaxValue / 1024 / 1024) + "Mb / Maximum HTTP object size:" + (maxObjectSizeHTTP / 1024 / 1024) + "Mb" + Environment.NewLine);

            // Gather index and dedupe stats
            if (Dedupe.IndexStats(out NumObjects, out NumChunks, out LogicalBytes, out PhysicalBytes, out DedupeRatioX, out DedupeRatioPercent))
            {
                Console.WriteLine("  Number of objects: " + NumObjects + "/  Number of chunks: " + NumChunks);
                Console.WriteLine("  Logical bytes    : " + LogicalBytes + " bytes" + "/  Physical bytes   : " + PhysicalBytes + " bytes");
                Console.WriteLine("  Dedupe ratio     : " + DedupeRatioX + "X, " + DedupeRatioPercent + "%");
                Console.WriteLine("-------------------------");
            }

            return true;
        }

        //private string httpString = "HTTP/1.1";
        private bool enabled = false;
        public override bool Enabled
        {
            get { return enabled; }
            set { enabled = value; }
        }

        public override void OnServerDataReceived(object sender, TCP.DataEventArgs e)
        {
            if (e.Request.Error != Socks.SocksError.Granted || e.Buffer.Length == 0)
            {
                Console.Write("<DeDupe> Error:" + e.Request.Error + " Length:" + e.Buffer.Length.ToString() + Environment.NewLine);
                return;
            }

            requestData.packetSize = e.Count;


            // Special handling for HTTP
            if (requestData.isHTTP)
            {

                // Handle HTTP header gracefully
                if (DataHandlerDeDupe.IsHTTPResponse200(e.Buffer) || requestData.isHeader)
                {
                    requestData.isHeader = true;
                    requestData.isChunked = DataHandlerDeDupe.IsChunked(e.Buffer);
                    requestData.contentLength = GetContentLength(e.Buffer);
                    requestData.totalPackets = Math.Ceiling((float)(requestData.contentLength) / (float)packetSize);
                    Console.Write(Environment.NewLine + "<DeDupe> [Server response] HTTP        - RequestType:" + requestData.requestType.ToString() + " Address:" + requestData.host + " Port:" + requestData.port.ToString() + " URL:" + requestData.uRI + Environment.NewLine);
                    Console.Write("<DeDupe> [Server response] HTTP Header - " + "ContentLength:" + requestData.contentLength + " ExpectedPackets:" + requestData.totalPackets + " Chunked:" + DataHandlerDeDupe.IsChunked(e.Buffer).ToString() + Environment.NewLine);

                    requestData.startPayload = e.Buffer.FindString("\r\n\r\n");
                    if (requestData.startPayload != -1)
                    {
                        requestData.startPayload += 4;
                        requestData.currentposition = 0;
                        requestData.isHeader = false;
                    }
                    if (requestData.contentLength != 0) stripeBuffer = new ReadWriteBuffer(requestData.contentLength + 1);

                    requestData.currentRound = 0;
                }
                //else requestData.startPayload = 0;
                ///END

                if (requestData.startPayload > 0)
                {
                    //int stripeCount = stripeBuffer.Count;
                    //int stripeSpaceRemaining = stripeSize - stripeCount - 1;
                    int payloadLength = Math.Min((requestData.contentLength - requestData.currentposition), requestData.packetSize - requestData.startPayload);
                    int contentRemaining = requestData.contentLength - requestData.currentposition;


                    if (requestData.isChunked)
                    {
                        //Console.Write("Chunk Size = " + Chunked.GetChunkSize(e.Buffer, 10).ToString() + Environment.NewLine);
                        //e.Buffer = Chunked.GetChunkData(e.Buffer, 
                        //.GetChunkSize(e.Buffer, e.Count));

                        //Chunked chunked = new Chunked(f, e.Buffer, e.Buffer.Length)


                    }
                    else
                    {

                        /*xxHash hash0 = new xxHash();
                        hash0.Init();
                        hash0.Update(e.Buffer, e.Count);
                        Key = hash0.Digest().ToString();*/


                        //if (Dedupe.StoreObject(Key, e.Buffer, out Chunks)) Console.WriteLine("<DeDupe> Stored: " + Key + Environment.NewLine);
                        Key = "HTTP/" + WebUtility.UrlEncode(requestData.host + requestData.port + requestData.uRI);
                        if (Dedupe.ObjectExists(Key))
                        {
                            Console.Write("x");




                        }
                        else
                        {



                            // Stream entire payload into buffer to maximise efficiency
                            int writeLength = Math.Min(e.Count - requestData.startPayload, (requestData.contentLength - requestData.currentposition));
                            if (contentRemaining > 0)
                            {
                                
                                byte[] shortBuffer = new byte[writeLength];
                                try
                                {
                                    shortBuffer = e.Buffer.GetInBetween(requestData.startPayload, requestData.startPayload + writeLength);
                                    requestData.startPayload = 0;

                                    try
                                    {
                                        Console.Write(Environment.NewLine + "---------------------------------------" + Key + Environment.NewLine + shortBuffer.GetBetween(0, Math.Max(shortBuffer.Length, 10)) + Environment.NewLine + "---------------------------------------" + Environment.NewLine);

                                        stripeBuffer.Write(shortBuffer);
                                        requestData.currentposition += shortBuffer.Length;

                                        //Console.WriteLine("---!!----" + shortBuffer.Length + "/" +  stripeBuffer.Count + "---------!!----");
                                    }
                                    catch
                                    {
                                        Console.Write("<DeDupe> bufferLength:" + stripeBuffer.Count + " shortBufferLength:" + shortBuffer.Length + " currentposition:" + requestData.currentposition + " contentRemaining:" + contentRemaining + Environment.NewLine);
                                        throw new Exception("<DeDupe> Write ERROR to resourceBuffer");
                                    }
                                }
                                catch
                                {
                                    Console.Write("<DeDupe> writeLength:" + writeLength + " shortBufferLength:" + shortBuffer + " bufferCount:" + e.Count + " length:" + e.Buffer.Length + " startPayload:" + requestData.startPayload + Environment.NewLine);
                                    throw new Exception("<DeDupe> Write ERROR to shortBuffer");
                                }


                                Console.Write("-");
                            }

                            // When buffer is full
                            if ( (contentRemaining == writeLength) && (stripeBuffer.Count != 0) )
                            {

                                // Open a stream if none already exists
                                if (requestData.cacheStream == null)
                                {
                                    Key = "HTTP/" + WebUtility.UrlEncode(requestData.host + requestData.port + requestData.uRI);


                                    //byte[] derp = new byte[stripeBuffer.Count];
                                    //derp = stripeBuffer.Read(stripeBuffer.Count);
                                    //Console.WriteLine("---!!----" + stripeBuffer.Count + "---------!!----");

                                    if (Dedupe == null) throw new Exception("<DeDupe> Library pointer == null");

                                    try
                                    {
                                        int derpCount = stripeBuffer.Count;
                                        byte[] derp = new byte[derpCount];
                                        derp = stripeBuffer.Read(derpCount);
                                        //Console.Write(Environment.NewLine + "---------------------------------------" + Environment.NewLine + derp.GetBetween(0, derpCount) + Environment.NewLine + "---------------------------------------" + Environment.NewLine);


                                        Dedupe.StoreObject(Key, derp, out Chunks);
                                    }
                                    catch
                                    {
                                        Console.Write(Environment.NewLine + "<DeDupe> [ERROR] Failed to open output stream!" + Environment.NewLine + "                 Key Named:" + Key + Environment.NewLine);
                                        Console.Write("                 Key Found:" + Dedupe.ObjectExists(Key).ToString() + Environment.NewLine);
                                        Console.Write("contentRemaining:" + contentRemaining + " writeLength:" + writeLength + " stripeBufferCount:" + stripeBuffer.Count + Environment.NewLine);

                                        throw new Exception(Environment.NewLine + "<DeDupe> [FATAL ERROR] Unable to write to cache:" + Key + Environment.NewLine);
                                    }
                                }

                                Console.Write(Environment.NewLine + "<DeDupe> (STORED) length:" + requestData.contentLength + " key:" + Key + Environment.NewLine);
                            }

                        }


                    }
                    //We maintain this per 4k socks packet.
                    // Update position
                    //requestData.currentposition += payloadLength;
                }

            }
            else Console.Write("<DeDupe> [Server response] DATA - RequestType:" + requestData.requestType.ToString() + " - Chunked:" + DataHandlerDeDupe.IsChunked(e.Buffer).ToString() + " Address:" + requestData.host + " Port:" + requestData.port.ToString() + Environment.NewLine);

            //if data is HTTP, make sure it's not compressed so we can capture it in plaintext.
            /*if (e.Buffer.FindString(" HTTP/1.1") != -1 && e.Buffer.FindString("Accept-Encoding") != -1)
            {
                int x = e.Buffer.FindString("Accept-Encoding:");
                int y = e.Buffer.FindString("\r\n", x + 1);
                e.Buffer = e.Buffer.ReplaceBetween(x, y, Encoding.ASCII.GetBytes(replaceWith));
                e.Buffer = e.Buffer.ReplaceString("HTTP/1.1", "HTTP/1.0");
                e.Count = e.Count - (y - x) + replaceWith.Length;
            }*/


        }
        public override void OnClientDataReceived(object sender, TCP.DataEventArgs e)
        {
            // Attempt to learn more about the incoming connection
            requestData.ip = e.Request.IP;
            requestData.port = e.Request.Port;
            requestData.isHTTP = DataHandlerDeDupe.IsHTTP(e.Buffer);
            if (requestData.isHTTP)
            {
                //If HTTP then retrieve hostname from request.
                requestData.requestType = DataHandlerDeDupe.IsRequest(e.Buffer);
                int start = e.Buffer.FindString("Host:") + 6;
                int end = e.Buffer.FindString("\r\n", start + 1);
                requestData.host = e.Buffer.GetBetween(start, end);
                requestData.host = requestData.host.Replace(":" + requestData.port, "");
                // Get URI
                requestData.uRI = GetURI(e.Buffer);

                Console.Write(Environment.NewLine + "<DeDupe> [Client received] HTTP        - RequestType:" + requestData.requestType.ToString() + " Address:" + requestData.host + " Port:" + requestData.port.ToString() + Environment.NewLine);

                requestData.isChunked = DataHandlerDeDupe.IsChunked(e.Buffer);
                Console.Write("<DeDupe> [Client received] HTTP Header - Chunked:" + DataHandlerDeDupe.IsChunked(e.Buffer).ToString() + Environment.NewLine);
            }
            else
            {
                requestData.host = e.Request.Address;

                Console.Write("<DeDupe> Received Client Data - Address:" + requestData.host + " Port:" + requestData.port.ToString() + Environment.NewLine);
            }          
            //END


            //Console.Write("----" + Environment.NewLine + "[ClientDataReceived]" + Environment.NewLine + e.Buffer.GetBetween(0, e.Buffer.Length - 1) + Environment.NewLine + "----" + Environment.NewLine);
            //Console.Write("<DeDupe> Received Client Data" + Environment.NewLine);

            //if data is HTTP, make sure it's not compressed so we can capture it in plaintext.
            if (e.Buffer.FindString(" HTTP/1.1") != -1 && e.Buffer.FindString("Accept-Encoding") != -1)
            {
                int x = e.Buffer.FindString("Accept-Encoding:");
                int y = e.Buffer.FindString("\r\n", x + 1);
                e.Buffer = e.Buffer.ReplaceBetween(x, y, Encoding.ASCII.GetBytes(replaceWith));
                e.Buffer = e.Buffer.ReplaceString("HTTP/1.1", "HTTP/1.0");
                e.Count = e.Count - (y - x) + replaceWith.Length;
            }
        }

        // Helper stream identification classes
        public static bool IsHTTP(byte[] buffer)
        {
            return (((buffer.FindString("HTTP/1.1") != -1) || (buffer.FindString("HTTP/1.0") != -1)) && buffer.FindString("\r\n\r\n") != -1);
        }
        public static bool IsHTTPResponse200(byte[] buffer)
        {
            return (((buffer.FindString("HTTP/1.1 200 OK") != -1) || (buffer.FindString("HTTP/1.1 200 OK") != -1)) && buffer.FindString("\r\n\r\n") != -1);
        }
        public static int IsRequest(byte[] buffer)
        {
            if (buffer.FindString("GET") != -1) return requestData.requestType = (int)RequestData.RequestType.GET;
            else if (buffer.FindString("PUT") != -1) return requestData.requestType = (int)RequestData.RequestType.PUT;
            else if (buffer.FindString("POST") != -1) return requestData.requestType = (int)RequestData.RequestType.POST;
            else return requestData.requestType = (int)RequestData.RequestType.NONE;
        }
        public static int GetContentLength(byte[] buffer)
        {
            int startIndex = buffer.FindString("Content-Length:");

            int endIndex = buffer.FindString("\r\n", startIndex + 1);
            int contentLength = Convert.ToInt32(buffer.GetBetween(startIndex + 16, endIndex));

            return contentLength;
        }
        public static string GetURI(byte[] buffer)
        {
            // Get Start Index
            int startIndex = buffer.FindString("GET ", 0);
            if (startIndex != -1)
            {
                startIndex += 4;
                goto HeaderFound;
            }

            startIndex = buffer.FindString("PUT ", 0);
            if (startIndex != -1)
            {
                startIndex += 4;
                goto HeaderFound;
            }

            startIndex = buffer.FindString("POST ", 0);
            if (startIndex != -1)
            {
                startIndex += 5;
                goto HeaderFound;
            }
            else return null;
            HeaderFound:

            int endIndex = buffer.FindString(" ", startIndex + 1);
            if (endIndex == -1) endIndex = buffer.FindString(" \r\n", startIndex + 1);
            if (endIndex == -1) return null;

            return buffer.GetBetween(startIndex, endIndex);
        }
        public static bool IsChunked(byte[] buffer)
        {
            return (IsHTTP(buffer) && buffer.FindString("Transfer-Encoding: chunked\r\n") != -1);
        }
        public static bool IsCompressed(byte[] buffer)
        {
            return (IsCompressed(buffer) && buffer.FindString("X-Content-Encoding-Over-Network: gzip\r\n") != -1);
        }


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

    // Stream Buffering
    public class ReadWriteBuffer
    {
        private readonly byte[] _buffer;
        private int _startIndex, _endIndex;

        public ReadWriteBuffer(int capacity)
        {
            _buffer = new byte[capacity];
        }

        public int Count
        {
            get
            {
                if (_endIndex > _startIndex)
                    return _endIndex - _startIndex;
                if (_endIndex < _startIndex)
                    return (_buffer.Length - _startIndex) + _endIndex;
                return 0;
            }
        }

        public void Write(byte[] data)
        {
            if (Count + data.Length > _buffer.Length)
                throw new Exception("buffer overflow");
            if (_endIndex + data.Length >= _buffer.Length)
            {
                var endLen = _buffer.Length - _endIndex;
                var remainingLen = data.Length - endLen;

                Array.Copy(data, 0, _buffer, _endIndex, endLen);
                Array.Copy(data, endLen, _buffer, 0, remainingLen);
                _endIndex = remainingLen;
            }
            else
            {
                Array.Copy(data, 0, _buffer, _endIndex, data.Length);
                _endIndex += data.Length;
            }
        }

        public byte[] Read(int len, bool keepData = false)
        {
            if (len > Count)
                throw new Exception("not enough data in buffer");
            var result = new byte[len];
            if (_startIndex + len < _buffer.Length)
            {
                Array.Copy(_buffer, _startIndex, result, 0, len);
                if (!keepData)
                    _startIndex += len;
                return result;
            }
            else
            {
                var endLen = _buffer.Length - _startIndex;
                var remainingLen = len - endLen;
                Array.Copy(_buffer, _startIndex, result, 0, endLen);
                Array.Copy(_buffer, 0, result, endLen, remainingLen);
                if (!keepData)
                    _startIndex = remainingLen;
                return result;
            }
        }

        public byte this[int index]
        {
            get
            {
                if (index >= Count)
                    throw new ArgumentOutOfRangeException();
                return _buffer[(_startIndex + index) % _buffer.Length];
            }
        }

        public IEnumerable<byte> Bytes
        {
            get
            {
                for (var i = 0; i < Count; i++)
                    yield return _buffer[(_startIndex + i) % _buffer.Length];
            }
        }
    }
}

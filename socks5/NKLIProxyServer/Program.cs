﻿/*
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

using System;
using System.Collections.Generic;
using System.Text;
using socks5;
using System.Net;
using System.Threading;
using socks5.Plugin;
using socks5.ExamplePlugins;
using socks5.Socks5Client;

namespace NKLISocksServer
{
    class Program
    {
        static void Main(string[] args)
        {
            Socks5Server x = new Socks5Server(IPAddress.Any, 1081);

            //Enable plugins
            PluginLoader.ChangePluginStatus(false, typeof(Auth));
            PluginLoader.ChangePluginStatus(true, typeof(DataHandlerDeDupe));

            x.Start();

            while (true)
            {
                //Console.Clear();
                //Console.Write("Total Clients: \t{0}\nTotal Recvd: \t{1:0.00##}MB\nTotal Sent: \t{2:0.00##}MB\n", x.Stats.TotalClients, ((x.Stats.NetworkReceived / 1024f) / 1024f), ((x.Stats.NetworkSent / 1024f) / 1024f));
                //Console.Write("Receiving/sec: \t{0}\nSending/sec: \t{1}", x.Stats.SBytesReceivedPerSec, x.Stats.SBytesSentPerSec);
                Thread.Sleep(1000);
            }
        }
    }
}

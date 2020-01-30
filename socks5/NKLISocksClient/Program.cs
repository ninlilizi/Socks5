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

using System;
using System.Collections.Generic;
using System.Text;
using socks5;
using System.Net;
using System.Threading;
using socks5.Plugin;
using socks5.ExamplePlugins;
using socks5.Socks5Client;

namespace NKLISocksClient
{
    class Program
    {
        public static void Main(string[] args)
        {
            //to test this - use something like proxifier or configure firefox for this proxy.
            Socks5Server s = new Socks5Server(IPAddress.Any, 1080, 1024);
            s.Start();
        }
    }



    class TorSocks : socks5.Plugin.ConnectSocketOverrideHandler
    {
        public override socks5.TCP.Client OnConnectOverride(socks5.Socks.SocksRequest sr)
        {
            //use a socks5client to connect to it and passthru the data.
            //port 9050 is where torsocks is running (linux & windows)
            Socks5Client s = new Socks5Client("localhost", 8080, sr.Address, sr.Port, "un", "pw");

            if (s.Connect())
            {
                //connect synchronously to block the thread.
                return s.Client;
            }
            else
            {
                Console.WriteLine("Failed!");
                return null;
            }

        }

        private bool enabled = true;
        public override bool Enabled
        {
            get
            {
                return this.enabled;
            }
            set
            {
                this.enabled = value;
            }
        }
        public override bool OnStart() { return true; }
    }
}

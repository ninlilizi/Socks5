using System;
using System.Collections.Generic;
using System.Text;

namespace NKLISocksServer
{
    class ConnectPlugin : socks5.Plugin.ConnectHandler
    {
        public override bool OnStart()
        {
            return true;
        }

        public override bool OnConnect(socks5.Socks.SocksRequest Request)
        {
            Console.WriteLine(Request.Port);
            return true;
        }

        public override bool Enabled
        {
            get
            {
                return true;
            }
            set
            {
                throw new NotImplementedException();
            }
        }
    }
}

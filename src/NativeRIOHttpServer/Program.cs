// Copyright (c) Illyriad Games. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NativeRIOHttpServer.RegisteredIO;

namespace NativeRIOHttpServer
{
    public class Program
    {
        static readonly string responseStr = "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain;charset=UTF-8\r\n" +
            "Content-Length: 10\r\n" +
            "Connection: keep-alive\r\n" +
            "Server: -RIO-\r\n" +
            "\r\n" +
            "HelloWorld";
        
        private static byte[] _responseBytes = Encoding.UTF8.GetBytes(responseStr);

        static void Main(string[] args)
        {
            // TODO: Use safehandles everywhere!
            var ss = new RIOTcpServer(5000, 127, 0, 0, 1);
            
            ThreadPool.SetMinThreads(100, 100);

            while (true)
            {
                var socket = ss.Accept();
                ThreadPool.QueueUserWorkItem(async _ => await Serve(socket).ConfigureAwait(false));
            }
        }

        static async Task Serve(TcpConnection socket)
        {
            try
            {
                //var x = 0;
                var buffer = new byte[2048];
                var sendBuffer = new ArraySegment<byte>(_responseBytes,0, _responseBytes.Length);
                var receiveBuffer = new ArraySegment<byte>(buffer, 0, buffer.Length);

                var receiveTask = socket.ReceiveAsync(receiveBuffer, CancellationToken.None);

                while (true)
                {
                    uint r = await receiveTask;
                    receiveTask = socket.ReceiveAsync(receiveBuffer, CancellationToken.None);

                    if (r == 0)
                    {
                        break;
                    }

                    //for (int i = 0; i < r; i++)
                    //{
                    //    x += buffer[i];
                    //}

                    //socket.SendQueue(sendBuffer);
                    socket.SendCachedOk();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            finally
            {
                socket.Close();
            }
        }
    }
}


﻿// Copyright (c) Illyriad Games. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ManagedRIOHttpServer.RegisteredIO;

namespace ManagedRIOHttpServer
{
    public sealed class Program
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
            unsafe
            {
                if (sizeof(IntPtr) != 8)
                {
                    Console.WriteLine("ManagedRIOHttpServer needs to be run in x64 mode");
                    return;
                }
            }
            
            // TODO: Use safehandles everywhere!
            var ss = new RIOTcpServer(5000, 127, 0, 0, 1);
            
            ThreadPool.SetMinThreads(100, 100);

            while (true)
            {
                var socket = ss.Accept();
                ThreadPool.UnsafeQueueUserWorkItem(Serve, socket);
            }
        }


        static void Serve(object state)
        {
            var socket = (TcpConnection)state;
            #pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            ServeSocket(socket);
            #pragma warning restore CS4014
        }

        static async Task ServeSocket(TcpConnection socket)
        {
            try
            {
                var sendBuffer = new ArraySegment<byte>(_responseBytes,0, _responseBytes.Length);
                var buffer0 = new byte[2048];
                var buffer1 = new byte[2048];
                var receiveBuffer0 = new ArraySegment<byte>(buffer0, 0, buffer0.Length);
                var receiveBuffer1 = new ArraySegment<byte>(buffer1, 0, buffer1.Length);

                var receiveTask = socket.ReceiveAsync(receiveBuffer0, CancellationToken.None);

                var loop = 0;

                while (true)
                {
                    int r = (int)await receiveTask;
                    receiveTask = socket.ReceiveAsync((loop & 1) == 1 ? receiveBuffer0 : receiveBuffer1, CancellationToken.None);

                    if (r == 0)
                    {
                        break;
                    }

                    var buffer = (loop & 1) == 0 ? buffer0 : buffer1;
                    var count = 0;
                    r -= 3;
                    if (r > 4)
                    {
                        for (var i = 0; i < r; i++)
                        {
                            if (buffer[i] == 0xd && buffer[i + 1] == 0xa && buffer[i + 2] == 0xd && buffer[i + 3] == 0xa)
                            {
                                count++;
                            }
                        }
                    }
                    else
                    {
                        count = 1;
                    }

                    if (count == 1)
                    {
                        socket.SendCachedOk();
                    }
                    else
                    {
                        for (var i = 1; i < count; i++)
                        {
                            socket.QueueSend(sendBuffer, false);
                        }
                        socket.QueueSend(sendBuffer, true);
                    }

                    for (var i = 1; i < count; i++)
                    {
                        socket.QueueSend(sendBuffer, false);
                    }
                    // force send if not more ready to recieve/pack
                    socket.QueueSend(sendBuffer, !receiveTask.IsCompleted);

                    loop++;
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


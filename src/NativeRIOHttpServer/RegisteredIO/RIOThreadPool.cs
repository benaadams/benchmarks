﻿// Copyright (c) Illyriad Games. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NativeRIOHttpServer.RegisteredIO
{
    internal unsafe class WorkBundle
    {
        public int id;
        public IntPtr completionPort;
        public IntPtr completionQueue;

        public ConcurrentDictionary<long, TcpConnection> connections;
        public Thread thread;

        public RIOBufferPool bufferPool;
        public RIO_BUFSEGMENT cachedOK;
        public RIO_BUFSEGMENT cachedBusy;
    }

    internal class RIOThreadPool
    {
        private RIO _rio;
        private CancellationToken _token;
        private int _maxThreads;

        public const int MaxOpenSocketsPerThread = 1024;
        private const int MaxOutsandingCompletions = (TcpConnection.MaxPendingReceives + TcpConnection.MaxPendingSends) * MaxOpenSocketsPerThread;

        private IntPtr _socket;

        internal WorkBundle GetWorker(long connetionId)
        {
            return _workers[(connetionId % _maxThreads)];
        }

        private WorkBundle[] _workers;

        public unsafe RIOThreadPool(RIO rio, IntPtr socket, CancellationToken token)
        {
            _socket = socket;
            _rio = rio;
            _token = token;

            _maxThreads = Environment.ProcessorCount;

            _workers = new WorkBundle[_maxThreads];
            for (var i = 0; i < _workers.Length; i++)
            {
                var worker = new WorkBundle()
                {
                    id = i,
                    bufferPool = new RIOBufferPool(_rio)
                };
                worker.completionPort = CreateIoCompletionPort(INVALID_HANDLE_VALUE, IntPtr.Zero, 0, 0);

                if (worker.completionPort == IntPtr.Zero)
                {
                    var error = GetLastError();
                    RIOImports.WSACleanup();
                    throw new Exception(String.Format("ERROR: CreateIoCompletionPort returned {0}", error));
                }

                var completionMethod = new RIO_NOTIFICATION_COMPLETION()
                {
                    Type = RIO_NOTIFICATION_COMPLETION_TYPE.IOCP_COMPLETION,
                    Iocp = new RIO_NOTIFICATION_COMPLETION_IOCP()
                    {
                        IocpHandle = worker.completionPort,
                        QueueCorrelation = (ulong)i,
                        Overlapped = (NativeOverlapped*)(-1)// nativeOverlapped
                    }
                };
                worker.completionQueue = _rio.CreateCompletionQueue(MaxOutsandingCompletions, completionMethod);

                if (worker.completionQueue == IntPtr.Zero)
                {
                    var error = RIOImports.WSAGetLastError();
                    RIOImports.WSACleanup();
                    throw new Exception(String.Format("ERROR: CreateCompletionQueue returned {0}", error));
                }

                worker.connections = new ConcurrentDictionary<long, TcpConnection>();
                worker.thread = new Thread(GetThreadStart(i));
                worker.thread.IsBackground = true;
                _workers[i] = worker;
            }

            // gc
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);

            for (var i = 0; i < _workers.Length; i++)
            {
                // pin buffers
                _workers[i].bufferPool.Initalize();
            }


            for (var i = 0; i < _workers.Length; i++)
            {
                _workers[i].thread.Start();
            }
        }
        private ThreadStart GetThreadStart(int i)
        {
            return new ThreadStart(() =>
            {
                Process(i);
            });

        }

        static readonly string okResponseStr = "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain;charset=UTF-8\r\n" +
            "Content-Length: 10\r\n" +
            "Connection: keep-alive\r\n" +
            "Server: -RIO-\r\n" +
            "\r\n" +
            "HelloWorld";

        private static byte[] _okResponseBytes = Encoding.UTF8.GetBytes(okResponseStr);

        static readonly string busyResponseStr = "HTTP/1.1 503 Service Unavailable\r\n" +
            "Content-Type: text/plain;charset=UTF-8\r\n" +
            "Content-Length: 4\r\n" +
            "Connection: keep-alive\r\n" +
            "Server: -RIO-\r\n" +
            "\r\n" +
            "Busy";

        private static byte[] _busyResponseBytes = Encoding.UTF8.GetBytes(busyResponseStr);

        const int maxResults = 512;
        private unsafe void Process(int id)
        {
            RIO_RESULT* results = stackalloc RIO_RESULT[maxResults];
            uint bytes, key;
            NativeOverlapped* overlapped;

            var worker = _workers[id];
            var completionPort = worker.completionPort;
            var cq = worker.completionQueue;

            PooledSegment cachedOKBuffer = worker.bufferPool.GetBuffer();
            Buffer.BlockCopy(_okResponseBytes, 0, cachedOKBuffer.Buffer, cachedOKBuffer.Offset, _okResponseBytes.Length);
            cachedOKBuffer.RioBuffer.Length = (uint)_okResponseBytes.Length;
            worker.cachedOK = cachedOKBuffer.RioBuffer;

            PooledSegment cachedBusyBuffer = worker.bufferPool.GetBuffer();
            Buffer.BlockCopy(_busyResponseBytes, 0, cachedBusyBuffer.Buffer, cachedBusyBuffer.Offset, _busyResponseBytes.Length);
            cachedBusyBuffer.RioBuffer.Length = (uint)_busyResponseBytes.Length;
            worker.cachedBusy = cachedBusyBuffer.RioBuffer;

            uint count;
            int ret;
            RIO_RESULT result;
            while (!_token.IsCancellationRequested)
            {
                _rio.Notify(cq);
                var sucess = GetQueuedCompletionStatus(completionPort, out bytes, out key, out overlapped, -1);
                if (sucess)
                {
                    count = _rio.DequeueCompletion(cq, (IntPtr)results, maxResults);
                    for (var i = 0; i < count; i++)
                    {
                        result = results[i];
                        if (result.RequestCorrelation == RIO.CachedValue)
                        {
                            // cached send response, don't release buffer
                        }
                        else if (result.RequestCorrelation < 0)
                        {
                            // first send 2 buffers are cached responses
                            // so should next have a 0 for a send
                            worker.bufferPool.ReleaseBuffer((int)-result.RequestCorrelation);
                        }
                        else
                        {
                            // receive
                            TcpConnection connection;
                            if (worker.connections.TryGetValue(result.ConnectionCorrelation, out connection))
                            {
                                connection.CompleteReceive(result.RequestCorrelation, result.BytesTransferred);
                            }
                        }
                    }
                    ret = _rio.Notify(cq);
                }
                else
                {
                    var error = GetLastError();
                    if (error != 258)
                    {
                        throw new Exception(String.Format("ERROR: GetQueuedCompletionStatusEx returned {0}", error));
                    }
                }
            }
            cachedOKBuffer.Dispose();
            cachedBusyBuffer.Dispose();
        }

        const string Kernel_32 = "Kernel32";
        const long INVALID_HANDLE_VALUE = -1;

        [DllImport(Kernel_32, SetLastError = true), SuppressUnmanagedCodeSecurity]
        private unsafe static extern IntPtr CreateIoCompletionPort(long handle, IntPtr hExistingCompletionPort, int puiCompletionKey, uint uiNumberOfConcurrentThreads);

        [DllImport(Kernel_32, SetLastError = true), SuppressUnmanagedCodeSecurity]
        private static extern unsafe bool GetQueuedCompletionStatus(IntPtr CompletionPort, out uint lpNumberOfBytes, out uint lpCompletionKey, out NativeOverlapped* lpOverlapped, int dwMilliseconds);

        [DllImport(Kernel_32, SetLastError = true), SuppressUnmanagedCodeSecurity]
        private static extern long GetLastError();

    }
}

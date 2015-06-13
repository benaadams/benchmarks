// Copyright (c) Illyriad Games. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NativeRIOHttpServer.RegisteredIO
{
    public struct RIOTcpServer
    {
        IntPtr _socket;
        RIO _rio;
        RIOThreadPool _pool;

        long _connectionId;

        public RIOTcpServer(ushort port, byte address1, byte address2, byte address3, byte address4)
        {
            var version = new RIOTcpServer.Version(2, 2);
            WSAData data;
            SocketError result = RIOImports.WSAStartup((short)version.Raw, out data);
            if (result != SocketError.Success)
            {
                var error = RIOImports.WSAGetLastError();
                throw new Exception(String.Format("ERROR: WSAStartup returned {0}", error));
            }

            _socket = RIOImports.WSASocket(ADDRESS_FAMILIES.AF_INET, SOCKET_TYPE.SOCK_STREAM, PROTOCOL.IPPROTO_TCP, IntPtr.Zero, 0, SOCKET_FLAGS.REGISTERED_IO);
            if (_socket == IntPtr.Zero)
            {
                var error = RIOImports.WSAGetLastError();
                RIOImports.WSACleanup();
                throw new Exception(String.Format("ERROR: WSASocket returned {0}", error));
            }

            _rio = RIOImports.Initalize(_socket);
            _pool = new RIOThreadPool(_rio, _socket, CancellationToken.None);
            _connectionId = 0;
            Start(port, address1, address2, address3, address4);
        }

        private void Start(ushort port, byte address1, byte address2, byte address3, byte address4)
        {
            // BIND
            in_addr inAddress = new in_addr();
            inAddress.s_b1 = address1;
            inAddress.s_b2 = address2;
            inAddress.s_b3 = address3;
            inAddress.s_b4 = address4;

            sockaddr_in sa = new sockaddr_in();
            sa.sin_family = ADDRESS_FAMILIES.AF_INET;
            sa.sin_port = RIOImports.htons(port);
            sa.sin_addr = inAddress;

            int result;
            unsafe
            {
                var size = sizeof(sockaddr_in);
                result = RIOImports.bind(_socket, ref sa, size);
            }
            if (result == RIOImports.SOCKET_ERROR)
            {
                RIOImports.WSACleanup();
                throw new Exception("bind failed");
            }

            // LISTEN
            result = RIOImports.listen(_socket, 10);
            if (result == RIOImports.SOCKET_ERROR)
            {
                RIOImports.WSACleanup();
                throw new Exception("listen failed");
            }
        }
        public TcpConnection Accept()
        {
            IntPtr accepted = RIOImports.accept(_socket, IntPtr.Zero, 0);
            if (accepted == new IntPtr(-1))
            {
                var error = RIOImports.WSAGetLastError();
                RIOImports.WSACleanup();
                throw new Exception(String.Format("listen failed with {0}", error));
            }
            var connection = Interlocked.Increment(ref _connectionId);
            return new TcpConnection(accepted, connection, _pool.GetWorker(connection), _rio);
        }

        public void Stop()
        {
            RIOImports.WSACleanup();
        }

        public struct Version
        {
            public ushort Raw;

            public Version(byte major, byte minor)
            {
                Raw = major;
                Raw <<= 8;
                Raw += minor;
            }

            public byte Major
            {
                get
                {
                    UInt16 result = Raw;
                    result >>= 8;
                    return (byte)result;
                }
            }

            public byte Minor
            {
                get
                {
                    UInt16 result = Raw;
                    result &= 0x00FF;
                    return (byte)result;
                }
            }

            public override string ToString()
            {
                return String.Format("{0}.{1}", Major, Minor);
            }
        }
    }



    public unsafe class TcpConnection : IDisposable
    {
        long _connectionId;
        IntPtr _socket;
        IntPtr _requestQueue;
        RIO _rio;

        long _sendCount = 0;
        long _receiveCount = 0;

        internal TcpConnection(IntPtr socket, long connectionId, WorkBundle wb, RIO rio)
        {
            _socket = socket;
            _connectionId = connectionId;
            _rio = rio;

            _requestQueue = _rio.CreateRequestQueue(_socket, 10, 1, 100, 1, wb.completionQueue, wb.completionQueue, connectionId);
            if (_requestQueue == IntPtr.Zero)
            {
                var error = RIOImports.WSAGetLastError();
                RIOImports.WSACleanup();
                throw new Exception(String.Format("ERROR: CreateRequestQueue returned {0}", error));
            }

            _receiveTask = new ReceiveTask(_rio.bufferPool.GetBuffer());
            wb.connections.TryAdd(connectionId, this);
        }

        const RIO_SEND_FLAGS MessagePart = RIO_SEND_FLAGS.DEFER | RIO_SEND_FLAGS.DONT_NOTIFY;
        const RIO_SEND_FLAGS MessageEnd = RIO_SEND_FLAGS.NONE;

        public void SendQueue(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var count = buffer.Count;
            var offset = buffer.Offset;

            var requestId = Interlocked.Increment(ref _sendCount);

            while (count > 0 && !cancellationToken.IsCancellationRequested)
            {
                var segment = _rio.bufferPool.GetBuffer();

                var length = count >= RIOBufferPool.PacketSize ? RIOBufferPool.PacketSize : count;

                Buffer.BlockCopy(buffer.Array, offset, segment.Buffer, segment.Offset, length);

                offset += length;
                count -= length;
                segment.RioBuffer.Length = (uint)length;

                var type = (count > 0 ? MessagePart : MessageEnd);
                _rio.Send(_requestQueue, ref segment.RioBuffer, 1, type, -segment.PoolIndex);
            }
        }

        ReceiveTask _receiveTask;
        public void CompleteReceive(long RequestCorrelation, uint BytesTransferred)
        {
            _receiveTask.Complete(BytesTransferred);
        }


        public ReceiveTask ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            _receiveTask.Reset(buffer);
            
            _rio.Receive(_requestQueue, ref _receiveTask._segment.RioBuffer, 1, RIO_RECEIVE_FLAGS.NONE, 12 );
            
            return _receiveTask;
        }

        public void Close()
        {
            Dispose(true);
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    //_receiveTask.Dispose();
                }
                // Makes it unhappy
                // _rio.CloseCompletionQueue(_requestQueue);


                RIOImports.closesocket(_socket);

                disposedValue = true;
            }
        }
        
        ~TcpConnection()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(false);
        }
        
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion

    }

    public sealed class ReceiveTask : INotifyCompletion, ICriticalNotifyCompletion
    {
        private readonly static Action CALLBACK_RAN = () => { };
        private bool _isCompleted;
        private Action _continuation;

        private uint _bytesTransferred;
        private ArraySegment<byte> _buffer;
        internal PooledSegment _segment;

        public ReceiveTask(PooledSegment segment)
        {
            _segment = segment;
        }

        internal void Reset(ArraySegment<byte> buffer)
        {
            _buffer = buffer;
            _bytesTransferred = 0;
            _isCompleted = false;
            _continuation = null;
        }
        internal void Complete(uint bytesTransferred)
        {
            _bytesTransferred = bytesTransferred;
            Buffer.BlockCopy(_segment.Buffer, _segment.Offset, _buffer.Array, _buffer.Offset, (int)bytesTransferred);
            

            Action continuation = _continuation ?? Interlocked.CompareExchange( ref _continuation, CALLBACK_RAN, null);
            if (continuation != null)
            {
                continuation();
            }
            _isCompleted = true;
        }

        public ReceiveTask GetAwaiter() { return this; }

        public bool IsCompleted { get { return _isCompleted; } }

        private void UnsafeCallback(object state)
        {
            ((Action)state)();
        }

        public void OnCompleted(Action continuation)
        {
            throw new NotImplementedException();
        }
        public uint GetResult()
        {
            return _bytesTransferred;
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        internal void Dispose()
        {
            if (!disposedValue)
            {
                disposedValue = true;
            }
        }

        [System.Security.SecurityCritical]
        public void UnsafeOnCompleted(Action continuation)
        {
            if (_continuation == CALLBACK_RAN ||
                    Interlocked.CompareExchange(
                        ref _continuation, continuation, null) == CALLBACK_RAN)
            {
                ThreadPool.UnsafeQueueUserWorkItem(UnsafeCallback, continuation);
            }
        }
        #endregion

    }
}

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
        WorkBundle _wb;

        long _sendCount = 0;
        long _receiveRequestCount = 0;
        DateTime _lastSend;

        ReceiveTask[] _receiveTasks;
        PooledSegment[] _sendSegments;
        ArraySegment<byte>[] _receiveRequestBuffers;
        public const int MaxPendingReceives = 32;
        public const int MaxPendingSends = MaxPendingReceives * 2;
        const int ReceiveMask = MaxPendingReceives - 1;
        const int SendMask = MaxPendingSends - 1;

        internal TcpConnection(IntPtr socket, long connectionId, WorkBundle wb, RIO rio)
        {
            _socket = socket;
            _connectionId = connectionId;
            _rio = rio;
            _wb = wb;

            _requestQueue = _rio.CreateRequestQueue(_socket, MaxPendingReceives, 1, MaxPendingSends, 1, wb.completionQueue, wb.completionQueue, connectionId);
            if (_requestQueue == IntPtr.Zero)
            {
                var error = RIOImports.WSAGetLastError();
                RIOImports.WSACleanup();
                throw new Exception(String.Format("ERROR: CreateRequestQueue returned {0}", error));
            }

            _receiveTasks = new ReceiveTask[MaxPendingReceives];
            _receiveRequestBuffers = new ArraySegment<byte>[MaxPendingReceives];

            for (var i = 0; i < _receiveTasks.Length; i++)
            {
                _receiveTasks[i] = new ReceiveTask(this, wb.bufferPool.GetBuffer());
            }

            _sendSegments = new PooledSegment[MaxPendingSends];
            for (var i = 0; i < _sendSegments.Length; i++)
            {
                _sendSegments[i] = wb.bufferPool.GetBuffer();
            }

            wb.connections.TryAdd(connectionId, this);
            
            for (var i = 0; i < _receiveTasks.Length; i++)
            {
                PostReceive( i );
            }
            _lastSend = DateTime.UtcNow;
        }

        const RIO_SEND_FLAGS MessagePart = RIO_SEND_FLAGS.DEFER | RIO_SEND_FLAGS.DONT_NOTIFY;
        const RIO_SEND_FLAGS MessageEnd = RIO_SEND_FLAGS.NONE;

        int _currentOffset = 0;
        public void QueueSend(ArraySegment<byte> buffer)
        {
            var segment = _sendSegments[_sendCount & SendMask];
            var count = buffer.Count;
            var offset = buffer.Offset;

            while (count > 0)
            {
                var length = (count >= RIOBufferPool.PacketSize ? RIOBufferPool.PacketSize : count) - _currentOffset;
                Buffer.BlockCopy(buffer.Array, offset, segment.Buffer, segment.Offset + _currentOffset, length);

                if (_currentOffset == RIOBufferPool.PacketSize)
                {
                    segment.RioBuffer.Length = RIOBufferPool.PacketSize;
                    _rio.Send(_requestQueue, &segment.RioBuffer, 1, MessageEnd, -_sendCount -1);
                    _currentOffset = 0;
                    _sendCount++;
                    segment = _sendSegments[_sendCount & SendMask];
                    _lastSend = DateTime.UtcNow;
                }
                else if (_currentOffset > RIOBufferPool.PacketSize)
                {
                    throw new Exception("Overflowed buffer");
                }
                else
                {
                    _currentOffset += length;
                }
                offset += length;
                count -= length;
            }
        }

        public void TriggerSend()
        {
            if (_currentOffset > 0 && (DateTime.UtcNow - _lastSend).TotalMilliseconds > 200)
            {
                var segment = _sendSegments[_sendCount & SendMask];
                segment.RioBuffer.Length = (uint)_currentOffset;
                _rio.Send(_requestQueue, &segment.RioBuffer, 1, MessageEnd, -_sendCount -1);
                _lastSend = DateTime.UtcNow;
                _currentOffset = 0;
                _sendCount++;
            }
        }

        public void CompleteReceive(long RequestCorrelation, uint BytesTransferred)
        {
            var receiveIndex = RequestCorrelation & ReceiveMask;
            var receiveTask = _receiveTasks[receiveIndex];
            receiveTask.Complete(BytesTransferred, (uint)receiveIndex);
        }

        internal void PostReceive(long receiveIndex)
        {
            var receiveTask = _receiveTasks[receiveIndex];
            _rio.Receive(_requestQueue, ref receiveTask._segment.RioBuffer, 1, RIO_RECEIVE_FLAGS.NONE, receiveIndex);
        }

        public ReceiveTask ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var receiveIndex = (Interlocked.Increment(ref _receiveRequestCount) - 1) & ReceiveMask;
            var receiveTask = _receiveTasks[receiveIndex];
            receiveTask.SetBuffer(buffer);
            return receiveTask;
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

                TcpConnection connection;
                _wb.connections.TryRemove(_connectionId, out connection);
                RIOImports.closesocket(_socket);
                for (var i = 0; i < _receiveTasks.Length; i++)
                {
                    _receiveTasks[i].Dispose();
                }

                for (var i = 0; i < _sendSegments.Length; i++)
                {
                    _sendSegments[i].Dispose();
                }

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
        private uint _requestCorrelation;
        private ArraySegment<byte> _buffer;
        internal PooledSegment _segment;
        private TcpConnection _connection;

        public ReceiveTask(TcpConnection connection, PooledSegment segment)
        {
            _segment = segment;
            _connection = connection;
        }

        internal void Reset()
        {
            _bytesTransferred = 0;
            _isCompleted = false;
            _continuation = null;
        }
        internal void SetBuffer(ArraySegment<byte> buffer)
        {
            _buffer = buffer;
        }
        internal void Complete(uint bytesTransferred, uint requestCorrelation)
        {
            _bytesTransferred = bytesTransferred;
            _requestCorrelation = requestCorrelation;
            _isCompleted = true;

            Action continuation = _continuation ?? Interlocked.CompareExchange( ref _continuation, CALLBACK_RAN, null);
            if (continuation != null)
            {
                continuation();
            }
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
        public uint GetResult()
        {
            var bytesTransferred = _bytesTransferred;
            Buffer.BlockCopy(_segment.Buffer, _segment.Offset, _buffer.Array, _buffer.Offset, (int)bytesTransferred);
            Reset();
            _connection.PostReceive(_requestCorrelation);
            return bytesTransferred;
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        internal void Dispose()
        {
            if (!disposedValue)
            {
                disposedValue = true;
                _segment.Dispose();
            }
        }

        #endregion

    }
}

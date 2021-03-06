﻿// cassandra-sharp - high performance .NET driver for Apache Cassandra
// Copyright (c) 2011-2013 Pierre Chalamet
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace CassandraSharp.Transport
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Security.Authentication;
    using System.Threading;
    using System.Threading.Tasks;
    using CassandraSharp.CQLBinaryProtocol;
    using CassandraSharp.Config;
    using CassandraSharp.Extensibility;
    using CassandraSharp.Instrumentation;
    using CassandraSharp.Utils;
    using CassandraSharp.Utils.Stream;

    internal class LongRunningConnection : IConnection,
                                           IDisposable
    {
        private const byte MAX_STREAMID = 0x80;

        private readonly Stack<byte> _availableStreamIds = new Stack<byte>();

        private readonly TransportConfig _config;

        private readonly IInstrumentation _instrumentation;

        private readonly object _lock = new object();

        private readonly ILogger _logger;

        private readonly Queue<QueryInfo> _pendingQueries = new Queue<QueryInfo>();

        private readonly QueryInfo[] _queryInfos = new QueryInfo[MAX_STREAMID];

        private readonly Socket _socket;

        private readonly TcpClient _tcpClient;

        private bool _isClosed;

        public LongRunningConnection(IPAddress address, TransportConfig config, ILogger logger, IInstrumentation instrumentation)
        {
            for (byte streamId = 0; streamId < MAX_STREAMID; ++streamId)
            {
                _availableStreamIds.Push(streamId);
            }

            _config = config;
            _logger = logger;
            _instrumentation = instrumentation;

            Endpoint = address;

            _tcpClient = new TcpClient
                {
                        ReceiveTimeout = _config.ReceiveTimeout,
                        SendTimeout = _config.SendTimeout,
                        NoDelay = true,
                        LingerState = {Enabled = true, LingerTime = 0},
                };

            _tcpClient.Connect(address, _config.Port);
            _socket = _tcpClient.Client;

            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, _config.KeepAlive);
            if (_config.KeepAlive && 0 != _config.KeepAliveTime)
            {
                SetTcpKeepAlive(_socket, _config.KeepAliveTime, 1000);
            }

            Task.Factory.StartNew(ReadResponseWorker, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(SendQueryWorker, TaskCreationOptions.LongRunning);

            // readify the connection
            _logger.Debug("Readyfying connection for {0}", Endpoint);
            //GetOptions();
            ReadifyConnection();
            _logger.Debug("Connection to {0} is ready", Endpoint);
        }

        public IPAddress Endpoint { get; private set; }

        public event EventHandler<FailureEventArgs> OnFailure;

        public void Execute(Action<IFrameWriter> writer, Func<IFrameReader, IEnumerable<object>> reader, InstrumentationToken token,
                            IObserver<object> observer)
        {
            QueryInfo queryInfo = new QueryInfo(writer, reader, token, observer);
            lock (_lock)
            {
                Monitor.Pulse(_lock);
                if (_isClosed)
                {
                    throw new OperationCanceledException();
                }

                _pendingQueries.Enqueue(queryInfo);
            }
        }

        public void Dispose()
        {
            Close(false);
        }

        public static void SetTcpKeepAlive(Socket socket, int keepaliveTime, int keepaliveInterval)
        {
            // marshal the equivalent of the native structure into a byte array
            byte[] inOptionValues = new byte[12];

            int enable = 0 != keepaliveTime
                                 ? 1
                                 : 0;
            BitConverter.GetBytes(enable).CopyTo(inOptionValues, 0);
            BitConverter.GetBytes(keepaliveTime).CopyTo(inOptionValues, 4);
            BitConverter.GetBytes(keepaliveInterval).CopyTo(inOptionValues, 8);

            // write SIO_VALS to Socket IOControl
            socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
        }

        private void Close(bool notifyFailure)
        {
            // already in close state ?
            lock (_lock)
            {
                Monitor.Pulse(_lock);
                if (_isClosed)
                {
                    return;
                }

                _isClosed = true;
            }

            _tcpClient.SafeDispose();

            OperationCanceledException canceledException = new OperationCanceledException();
            foreach (QueryInfo queryInfo in _queryInfos.Where(queryInfo => null != queryInfo))
            {
                queryInfo.Observer.OnError(canceledException);
                _instrumentation.ClientTrace(queryInfo.Token, EventType.Cancellation);
            }

            if (notifyFailure && null != OnFailure)
            {
                FailureEventArgs failureEventArgs = new FailureEventArgs(null);
                OnFailure(this, failureEventArgs);
            }

            OnFailure = null;
        }

        private void SendQueryWorker()
        {
            try
            {
                SendQuery();
            }
            catch (Exception ex)
            {
                _logger.Fatal("Error while trying to send query : {0}", ex);
                HandleError(ex);
            }
        }

        private void SendQuery()
        {
            while (true)
            {
                QueryInfo queryInfo;
                lock (_lock)
                {
                    while (!_isClosed && 0 == _pendingQueries.Count)
                    {
                        Monitor.Wait(_lock);
                    }
                    if (_isClosed)
                    {
                        Monitor.Pulse(_lock);
                        return;
                    }

                    queryInfo = _pendingQueries.Dequeue();
                }

                try
                {
                    // acquire the global lock to write the request
                    InstrumentationToken token = queryInfo.Token;
                    bool tracing = 0 != (token.ExecutionFlags & ExecutionFlags.ServerTracing);
                    using (BufferingFrameWriter bufferingFrameWriter = new BufferingFrameWriter(tracing))
                    {
                        queryInfo.Writer(bufferingFrameWriter);

                        byte streamId;
                        lock (_lock)
                        {
                            while (!_isClosed && 0 == _availableStreamIds.Count)
                            {
                                Monitor.Wait(_lock);
                            }
                            if (_isClosed)
                            {
                                Monitor.Pulse(_lock);
                                return;
                            }

                            streamId = _availableStreamIds.Pop();
                        }

                        _logger.Debug("Starting writing frame for stream {0}@{1}", streamId, Endpoint);
                        _instrumentation.ClientTrace(token, EventType.BeginWrite);

                        _queryInfos[streamId] = queryInfo;
                        bufferingFrameWriter.SendFrame(streamId, _socket);

                        _logger.Debug("Done writing frame for stream {0}@{1}", streamId, Endpoint);
                        _instrumentation.ClientTrace(token, EventType.EndWrite);
                    }
                }
                catch (Exception ex)
                {
                    queryInfo.Observer.OnError(ex);
                    if (ex is SocketException || ex is IOException)
                    {
                        throw;
                    }
                }
            }
        }

        private void ReadResponseWorker()
        {
            try
            {
                ReadResponse();
            }
            catch (Exception ex)
            {
                _logger.Fatal("Error while trying to receive response: {0}", ex);
                HandleError(ex);
            }
        }

        private void ReadResponse()
        {
            while (true)
            {
                using (IFrameReader frameReader = new StreamingFrameReader(_socket))
                {
                    byte streamId = frameReader.StreamId;
                    QueryInfo queryInfo = _queryInfos[streamId];
                    _queryInfos[streamId] = null;
                    lock (_lock)
                    {
                        Monitor.Pulse(_lock);
                        if (_isClosed)
                        {
                            throw new OperationCanceledException();
                        }
                        
                        _availableStreamIds.Push(streamId);
                    }

                    _instrumentation.ClientTrace(queryInfo.Token, EventType.BeginRead);
                    IObserver<object> observer = queryInfo.Observer;
                    try
                    {
                        if (null == frameReader.ResponseException)
                        {
                            IEnumerable<object> data = queryInfo.Reader(frameReader);
                            foreach (object datum in data)
                            {
                                observer.OnNext(datum);
                            }
                            observer.OnCompleted();
                        }
                        else
                        {
                            observer.OnError(frameReader.ResponseException);
                        }
                    }
                    catch (Exception ex)
                    {
                        observer.OnError(ex);
                        if (ex is SocketException || ex is IOException)
                        {
                            throw;
                        }
                    }

                    _instrumentation.ClientTrace(queryInfo.Token, EventType.EndRead);

                    InstrumentationToken token = queryInfo.Token;
                    if (0 != (token.ExecutionFlags & ExecutionFlags.ServerTracing))
                    {
                        _logger.Debug("Requesting tracing info for query {0}", frameReader.TraceId);
                        TracingHelpers.AsyncQueryAndPushTracingSession(this, frameReader.TraceId, token, _instrumentation, _logger);
                    }
                }
            }
        }

        private void HandleError(Exception ex)
        {
            Close(true);
        }

        private void GetOptions()
        {
            IObservable<object> obsOptions = CQLCommandHelpers.CreateOptionsQuery(this);
            Task<IList<object>> res = obsOptions.AsFuture();
            res.Wait();
        }

        private void ReadifyConnection()
        {
            IObservable<object> obsReady = CQLCommandHelpers.CreateReadyQuery(this, _config.CqlVersion);
            Task<IList<object>> res = obsReady.AsFuture();
            res.Wait();

            bool authenticate = (bool) res.Result.Single();
            if (authenticate)
            {
                Authenticate();
            }
        }

        private void Authenticate()
        {
            if (null == _config.User || null == _config.Password)
            {
                throw new InvalidCredentialException();
            }

            IObservable<object> obsAuth = CQLCommandHelpers.CreateAuthenticateQuery(this, _config.User, _config.Password);
            obsAuth.AsFuture().Wait();
        }

        private class QueryInfo
        {
            public QueryInfo(Action<IFrameWriter> writer, Func<IFrameReader, IEnumerable<object>> reader,
                             InstrumentationToken token, IObserver<object> observer)
            {
                Writer = writer;
                Reader = reader;
                Token = token;
                Observer = observer;
            }

            public Func<IFrameReader, IEnumerable<object>> Reader { get; private set; }

            public InstrumentationToken Token { get; private set; }

            public Action<IFrameWriter> Writer { get; private set; }

            public IObserver<object> Observer { get; private set; }
        }
    }
}
// threaded transport to handle all the magic.
// implementations are automatically elevated to the worker thread
// by simply overwriting all the thread functions
//
// note that ThreadLog.cs is required for Debug.Log from threads to work in builds.
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace Mirror
{
    // buffered events for main thread
    enum MainEventType
    {
        OnServerConnected,
        OnServerSent,
        OnServerReceived,
        OnServerError,
        OnServerDisconnected,

        OnClientConnected,
        OnClientSent,
        OnClientReceived,
        OnClientError,
        OnClientDisconnected,
    }

    // buffered events for worker thread
    enum ThreadEventType
    {
        DoServerStart,
        DoServerSend,
        DoServerDisconnect,
        DoServerStop,

        DoClientConnect,
        DoClientSend,
        DoClientDisconnect,

        DoShutdown
    }

    struct MainEvent
    {
        public MainEventType type;
        public object        param;

        // some events have value type parameters: connectionId, error.
        // store them explicitly to avoid boxing allocations to 'object param'.
        public int?            connectionId; // only server needs connectionId
        public int?            channelId;    // connect/disconnect don't have a channel
        public TransportError? error;

        public MainEvent(
            MainEventType type,
            object param,
            int? connectionId,
            int? channelId = null,
            TransportError? error = null)
        {
            this.type = type;
            this.channelId = channelId;
            this.connectionId = connectionId;
            this.error = error;
            this.param = param;
        }
    }

    struct ThreadEvent
    {
        public ThreadEventType type;
        public object          param;

        // some events have value type parameters: connectionId.
        // store them explicitly to avoid boxing allocations to 'object param'.
        public int? connectionId;
        public int? channelId;

        public ThreadEvent(
            ThreadEventType type,
            object param,
            int? connectionId = null,
            int? channelId = null)
        {
            this.type = type;
            this.connectionId = connectionId;
            this.channelId = channelId;
            this.param = param;
        }
    }

    public abstract class ThreadedTransport : Transport
    {
        WorkerThread thread;

        // main thread's event queue.
        // worker thread puts events in, main thread processes them.
        // client & server separate because EarlyUpdate is separate too.
        // TODO nonalloc
        readonly ConcurrentQueue<MainEvent> clientMainQueue = new ConcurrentQueue<MainEvent>();
        readonly ConcurrentQueue<MainEvent> serverMainQueue = new ConcurrentQueue<MainEvent>();

        // worker thread's event queue
        // main thread puts events in, worker thread processes them.
        // TODO nonalloc
        readonly ConcurrentQueue<ThreadEvent> threadQueue = new ConcurrentQueue<ThreadEvent>();

        // communication between main & worker thread //////////////////////////
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnqueueClientMain(
            MainEventType type,
            object param,
            int? connectionId,
            int? channelId,
            TransportError? error) =>
            clientMainQueue.Enqueue(new MainEvent(type, param, connectionId, channelId, error));

        // add an event for main thread
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnqueueServerMain(
            MainEventType type,
            object param,
            int? connectionId,
            int? channelId,
            TransportError? error) =>
            serverMainQueue.Enqueue(new MainEvent(type, param, connectionId, channelId, error));

        void EnqueueThread(
            ThreadEventType type,
            object param,
            int? channelId,
            int? connectionId) =>
            threadQueue.Enqueue(new ThreadEvent(type, param, connectionId, channelId));

        // Unity callbacks /////////////////////////////////////////////////////
        protected void Awake()
        {
            // start the thread.
            // if main application terminates, this thread needs to terminate too.
            thread         = new WorkerThread(ToString());
            thread.Tick    = ThreadTick;
            thread.Cleanup = ThreadedShutdown;
            thread.Start();
        }

        protected virtual void OnDestroy()
        {
            // stop thread fully
            Shutdown();

            // TODO recycle writers.
        }

        // worker thread ///////////////////////////////////////////////////////
        void ProcessThreadQueue()
        {
            // TODO deadlock protection. worker thread may be to slow to process all.
            while (threadQueue.TryDequeue(out ThreadEvent elem))
            {
                switch (elem.type)
                {
                    // SERVER EVENTS ///////////////////////////////////////////
                    case ThreadEventType.DoServerStart: // start listening
                    {
                        // call the threaded function
                        ThreadedServerStart();
                        break;
                    }
                    case ThreadEventType.DoServerSend:
                    {
                        // call the threaded function
                        ConcurrentNetworkWriterPooled writer = (ConcurrentNetworkWriterPooled)elem.param;
                        ThreadedServerSend(elem.connectionId.Value, writer, elem.channelId.Value);

                        // recycle writer to thread safe pool for reuse
                        ConcurrentNetworkWriterPool.Return(writer);
                        break;
                    }
                    case ThreadEventType.DoServerDisconnect:
                    {
                        // call the threaded function
                        ThreadedServerDisconnect(elem.connectionId.Value);
                        break;
                    }
                    case ThreadEventType.DoServerStop: // stop listening
                    {
                        // call the threaded function
                        ThreadedServerStop();
                        break;
                    }

                    // CLIENT EVENTS ///////////////////////////////////////////
                    case ThreadEventType.DoClientConnect:
                    {
                        // call the threaded function
                        ThreadedClientConnect((string)elem.param); // address
                        break;
                    }
                    case ThreadEventType.DoClientSend:
                    {
                        // call the threaded function
                        ConcurrentNetworkWriterPooled writer = (ConcurrentNetworkWriterPooled)elem.param;
                        ThreadedClientSend(writer, elem.channelId.Value);

                        // recycle writer to thread safe pool for reuse
                        ConcurrentNetworkWriterPool.Return(writer);
                        break;
                    }
                    case ThreadEventType.DoClientDisconnect:
                    {
                        // call the threaded function
                        ThreadedClientDisconnect();
                        break;
                    }

                    // SHUTDOWN ////////////////////////////////////////////////
                    case ThreadEventType.DoShutdown:
                    {
                        // call the threaded function
                        ThreadedShutdown();
                        break;
                    }
                }
            }
        }

        void ThreadTick()
        {
            // early update the implementation first
            ThreadedNetworkEarlyUpdate();

            // process queued user requests
            ProcessThreadQueue();

            // late update the implementation at the end
            ThreadedNetworkLateUpdate();

            // save some cpu power.
            // TODO update interval and sleep extra time would be ideal
            Thread.Sleep(1);
        }

        // threaded callbacks to call from transport thread.
        // they will be queued up for main thread automatically.
        protected void OnThreadedServerConnect(int connectionId, string address)
        {
            EnqueueServerMain(MainEventType.OnServerConnected, address, connectionId, null, null);
        }

        protected void OnThreadedServerSend(int connectionId, ArraySegment<byte> message, int channelId)
        {
            // ArraySegment is only valid until returning.
            // copy to a writer until main thread processes it.
            // make sure to recycle the writer in main thread.
            ConcurrentNetworkWriterPooled writer = ConcurrentNetworkWriterPool.Get();
            writer.WriteArraySegment(message);
            EnqueueServerMain(MainEventType.OnServerSent, writer, connectionId, channelId, null);
        }

        protected void OnThreadedServerReceive(int connectionId, ArraySegment<byte> message, int channelId)
        {
            // ArraySegment is only valid until returning.
            // copy to a writer until main thread processes it.
            // make sure to recycle the writer in main thread.
            ConcurrentNetworkWriterPooled writer = ConcurrentNetworkWriterPool.Get();
            writer.WriteArraySegment(message);
            EnqueueServerMain(MainEventType.OnServerReceived, writer, connectionId, channelId, null);
        }

        protected void OnThreadedServerError(int connectionId, TransportError error, string reason)
        {
            EnqueueServerMain(MainEventType.OnServerError, reason, connectionId, null, error);
        }

        protected void OnThreadedServerDisconnect(int connectionId)
        {
            EnqueueServerMain(MainEventType.OnServerDisconnected, null, connectionId, null, null);
        }

        protected void OnThreadedClientConnect(string address)
        {
            EnqueueClientMain(MainEventType.OnClientConnected, address, null, null, null);
        }

        protected void OnThreadedClientSend(ArraySegment<byte> message, int channelId)
        {
            // ArraySegment is only valid until returning.
            // copy to a writer until main thread processes it.
            // make sure to recycle the writer in main thread.
            ConcurrentNetworkWriterPooled writer = ConcurrentNetworkWriterPool.Get();
            writer.WriteArraySegment(message);
            EnqueueClientMain(MainEventType.OnClientSent, writer, null, channelId, null);
        }

        protected void OnThreadedClientReceive(ArraySegment<byte> message, int channelId)
        {
            // ArraySegment is only valid until returning.
            // copy to a writer until main thread processes it.
            // make sure to recycle the writer in main thread.
            ConcurrentNetworkWriterPooled writer = ConcurrentNetworkWriterPool.Get();
            writer.WriteArraySegment(message);
            EnqueueClientMain(MainEventType.OnClientReceived, writer, null, channelId, null);
        }

        protected void OnThreadedClientError(TransportError error, string reason)
        {
            EnqueueClientMain(MainEventType.OnClientError, reason, null, null, error);
        }

        protected void OnThreadedClientDisconnect()
        {
            EnqueueClientMain(MainEventType.OnClientDisconnected, null, null, null, null);
        }

        protected abstract void ThreadedServerStart();
        protected abstract void ThreadedServerStop();
        protected abstract void ThreadedServerSend(int connectionId, ArraySegment<byte> message, int channelId);
        protected abstract void ThreadedServerDisconnect(int connectionId);

        protected abstract void ThreadedClientConnect(string address);
        protected abstract void ThreadedClientSend(ArraySegment<byte> message, int channelId);
        protected abstract void ThreadedClientDisconnect();

        // threaded update functions.
        // make sure not to call main thread OnReceived etc. events.
        // queue everything.
        protected abstract void ThreadedNetworkEarlyUpdate();
        protected abstract void ThreadedNetworkLateUpdate();

        protected abstract void ThreadedShutdown();

        // client //////////////////////////////////////////////////////////////
        // implementations need to use ThreadedEarlyUpdate
        public override void ClientEarlyUpdate()
        {
            // regular transports process OnReceive etc. from early update.
            // need to process the worker thread's queued events here too.

            // TODO deadlock protection. main thread may be to slow to process all.
            while (clientMainQueue.TryDequeue(out MainEvent elem))
            {
                switch (elem.type)
                {
                    // CLIENT EVENTS ///////////////////////////////////////////
                    case MainEventType.OnClientConnected:
                    {
                        // call original transport event
                        OnClientConnected?.Invoke();
                        break;
                    }
                    case MainEventType.OnClientSent:
                    {
                        // call original transport event
                        ConcurrentNetworkWriterPooled writer = (ConcurrentNetworkWriterPooled)elem.param;
                        OnClientDataSent?.Invoke(writer, elem.channelId.Value);

                        // recycle writer to thread safe pool for reuse
                        ConcurrentNetworkWriterPool.Return(writer);
                        break;
                    }
                    case MainEventType.OnClientReceived:
                    {
                        // call original transport event
                        ConcurrentNetworkWriterPooled writer = (ConcurrentNetworkWriterPooled)elem.param;
                        OnClientDataReceived?.Invoke(writer, elem.channelId.Value);

                        // recycle writer to thread safe pool for reuse
                        ConcurrentNetworkWriterPool.Return(writer);
                        break;
                    }
                    case MainEventType.OnClientError:
                    {
                        // call original transport event
                        OnClientError?.Invoke(elem.error.Value, (string)elem.param);
                        break;
                    }
                    case MainEventType.OnClientDisconnected:
                    {
                        // call original transport event
                        OnClientDisconnected?.Invoke();
                        break;
                    }
                }
            }
        }

        public override void ClientConnect(string address)
        {
            // don't connect the thread twice
            if (ClientConnected())
            {
                Debug.LogWarning($"Threaded transport: client already connected!");
                return;
            }

            // enqueue to process in worker thread
            EnqueueThread(ThreadEventType.DoServerStart, null, null, null);
        }

        public override void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable)
        {
            if (!ClientConnected()) return;

            // segment is only valid until returning.
            // copy it to a writer.
            // make sure to recycle it from worker thread.
            ConcurrentNetworkWriterPooled writer = ConcurrentNetworkWriterPool.Get();
            writer.WriteArraySegment(segment);

            // enqueue to process in worker thread
            EnqueueThread(ThreadEventType.DoClientSend, writer, null, null);
        }

        public override void ClientDisconnect()
        {
            EnqueueThread(ThreadEventType.DoClientDisconnect, null, null, null);
        }

        // server //////////////////////////////////////////////////////////////
        // implementations need to use ThreadedEarlyUpdate
        public override void ServerEarlyUpdate()
        {
            // regular transports process OnReceive etc. from early update.
            // need to process the worker thread's queued events here too.

            // TODO deadlock protection. main thread may be to slow to process all.
            while (serverMainQueue.TryDequeue(out MainEvent elem))
            {
                switch (elem.type)
                {
                    // SERVER EVENTS ///////////////////////////////////////////
                    case MainEventType.OnServerConnected:
                    {
                        // call original transport event
                        // TODO pass client address in OnConnect here later
                        OnServerConnected?.Invoke(elem.connectionId.Value);//, (string)elem.param);
                        break;
                    }
                    case MainEventType.OnServerSent:
                    {
                        // call original transport event
                        ConcurrentNetworkWriterPooled writer = (ConcurrentNetworkWriterPooled)elem.param;
                        OnServerDataSent?.Invoke(elem.connectionId.Value, writer, elem.channelId.Value);

                        // recycle writer to thread safe pool for reuse
                        ConcurrentNetworkWriterPool.Return(writer);
                        break;
                    }
                    case MainEventType.OnServerReceived:
                    {
                        // call original transport event
                        ConcurrentNetworkWriterPooled writer = (ConcurrentNetworkWriterPooled)elem.param;
                        OnServerDataReceived?.Invoke(elem.connectionId.Value, writer, elem.channelId.Value);

                        // recycle writer to thread safe pool for reuse
                        ConcurrentNetworkWriterPool.Return(writer);
                        break;
                    }
                    case MainEventType.OnServerError:
                    {
                        // call original transport event
                        OnServerError?.Invoke(elem.connectionId.Value, elem.error.Value, (string)elem.param);
                        break;
                    }
                    case MainEventType.OnServerDisconnected:
                    {
                        // call original transport event
                        OnServerDisconnected?.Invoke(elem.connectionId.Value);
                        break;
                    }
                }
            }
        }

        // implementations need to use ThreadedLateUpdate
        public override void ServerLateUpdate() {}

        public override void ServerStart()
        {
            // don't start the thread twice
            if (ServerActive())
            {
                Debug.LogWarning($"Threaded transport: server already started!");
                return;
            }

            // enqueue to process in worker thread
            EnqueueThread(ThreadEventType.DoServerStart, null, null, null);
        }

        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId = Channels.Reliable)
        {
            if (!ServerActive()) return;

            // segment is only valid until returning.
            // copy it to a writer.
            // make sure to recycle it from worker thread.
            ConcurrentNetworkWriterPooled writer = ConcurrentNetworkWriterPool.Get();
            writer.WriteArraySegment(segment);

            // enqueue to process in worker thread
            EnqueueThread(ThreadEventType.DoServerSend, writer, connectionId, null);
        }

        public override void ServerDisconnect(int connectionId)
        {
            // enqueue to process in worker thread
            EnqueueThread(ThreadEventType.DoServerDisconnect, null, connectionId, null);
        }

        // TODO pass address in OnConnected.
        // querying this at runtime won't work for threaded transports.
        public override string ServerGetClientAddress(int connectionId)
        {
            throw new NotImplementedException();
        }

        public override void ServerStop()
        {
            // enqueue to process in worker thread
            EnqueueThread(ThreadEventType.DoServerStop, null, null, null);
        }

        // shutdown ////////////////////////////////////////////////////////////
        public override void Shutdown()
        {
            // enqueue to process in worker thread
            EnqueueThread(ThreadEventType.DoShutdown, null, null, null);

            // need to wait a little for worker thread to process the enqueued
            // Shutdown event and do proper cleanup.
            //
            // otherwise if a server with a connected client is stopped,
            // and then started, a warning would be shown when starting again
            // about an old connection not being found because it wasn't cleared
            // in KCP
            // TODO cleaner
            Thread.Sleep(100);

            // stop thread fully, with timeout
            // ?.: 'thread' might be null after script reload -> stop play
            thread?.StopBlocking(1);

            // clear queues so we don't process old messages
            // when listening again later
            clientMainQueue.Clear();
            serverMainQueue.Clear();
            threadQueue.Clear();
        }
    }
}

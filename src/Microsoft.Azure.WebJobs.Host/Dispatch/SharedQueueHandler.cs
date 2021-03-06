﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Listeners;
using Microsoft.Azure.WebJobs.Host.Protocols;
using Microsoft.Azure.WebJobs.Host.Queues;
using Microsoft.Azure.WebJobs.Host.Queues.Listeners;
using Microsoft.Azure.WebJobs.Host.Storage.Queue;
using Microsoft.Azure.WebJobs.Host.Timers;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Host.Dispatch
{
    internal class SharedQueueHandler
    {
        private readonly IStorageAccountProvider _accountProvider;
        private readonly IHostIdProvider _hostIdProvider;
        private readonly IWebJobsExceptionHandler _exceptionHandler;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IQueueConfiguration _queueConfiguration;
        private readonly ISharedContextProvider _sharedContextProvider;
        private readonly IContextSetter<IMessageEnqueuedWatcher> _messageEnqueuedWatcherSetter;

        private SharedQueueExecutor _triggerExecutor;
        private State _state;
        private QueueListener _sharedQueuelistener;
        private SharedQueueWriter _sharedQueueWriter;

        internal SharedQueueHandler(IStorageAccountProvider accountProvider,
                           IHostIdProvider hostIdProvider,
                           IWebJobsExceptionHandler exceptionHandler,
                           ILoggerFactory loggerFactory,
                           IQueueConfiguration queueConfiguration,
                           ISharedContextProvider sharedContextProvider,
                           IContextSetter<IMessageEnqueuedWatcher> messageEnqueuedWatcherSetter)
        {
            _accountProvider = accountProvider;
            _hostIdProvider = hostIdProvider;
            _exceptionHandler = exceptionHandler;
            _loggerFactory = loggerFactory;
            _queueConfiguration = queueConfiguration;
            _sharedContextProvider = sharedContextProvider;
            _messageEnqueuedWatcherSetter = messageEnqueuedWatcherSetter;
            _state = State.Created;
        }

        // this is used to prevent illegal call sequence
        // unexpected State will throw exception which is likely a developer error
        private enum State
        {
            Created,
            Initialized,
            Started,
            Stopped,
        }

        internal Exception InitializationEx { get; private set; }

        internal async Task StopQueueAsync(CancellationToken cancellationToken)
        {
            if (_state != State.Started)
            {
                throw new InvalidOperationException(ErrorMessage(State.Started, _state));
            }
            if (_triggerExecutor.HasMessageHandlers())
            {
                await _sharedQueuelistener.StopAsync(cancellationToken);
            }
            // if there's no messageHandlers registed, stopQueue is a NOOP
            _state = State.Stopped;
        }

        private static string ErrorMessage(State expected, State actual)
        {
            return $"Expected state to be \"{expected}\" but actualy state is \"{actual}\", this is probably because methods are not called in correct order";
        }

        // initialize following fields async
        // _triggerExecutor --> register messageHandler
        // _sharedQueuelistener --> dequeue messages and call messageHandler
        // _sharedQueueWriter --> enqueue messages
        internal async Task InitializeAsync(CancellationToken cancellationToken)
        {
            if (_state != State.Created)
            {
                // only initialized once, since _state is incremental
                throw new InvalidOperationException(ErrorMessage(State.Created, _state));
            }

            // concurrent dictionary that we can register messageHandler
            _triggerExecutor = new SharedQueueExecutor();

            try
            {
                IStorageQueueClient primaryQueueClient = (await _accountProvider.GetStorageAccountAsync(cancellationToken)).CreateQueueClient();
                string hostId = await _hostIdProvider.GetHostIdAsync(cancellationToken);

                // one host level shared queue
                // queue is not created here, only after 1st message added
                IStorageQueue sharedQueue = primaryQueueClient.GetQueueReference(HostQueueNames.GetHostSharedQueueName(hostId));
                // default host level poison queue
                IStorageQueue sharedPoisonQueue = primaryQueueClient.GetQueueReference(HostQueueNames.GetHostSharedPoisonQueueName(hostId));

                // queueWatcher will update queueListener's polling interval when queueWriter performes an enqueue operation
                SharedQueueWatcher sharedQueueWatcher = _sharedContextProvider.GetOrCreateInstance<SharedQueueWatcher>(
                                                            new SharedQueueWatcherFactory(_messageEnqueuedWatcherSetter));
                _sharedQueueWriter = new SharedQueueWriter(sharedQueue, sharedQueueWatcher);

                // use default poisonQueue setup
                _sharedQueuelistener = new QueueListener(sharedQueue, sharedPoisonQueue, _triggerExecutor,
                    _exceptionHandler, _loggerFactory, sharedQueueWatcher, _queueConfiguration);
            }
            catch (Exception ex)
            {
                // possible issue with the connection String "TriggerTests.TestByteArrayDispatch"
                string errorMessage = "SharedQueue initialization error, fallback to InMemorySharedQueue";
                // surface this error to user
                var logger = _loggerFactory?.CreateLogger(LogCategories.Startup);
                logger?.LogError(ex, errorMessage);

                // initialization exception make registration a NOOP
                InitializationEx = ex;
            }

            _state = State.Initialized;
        }

        internal async Task StartQueueAsync(CancellationToken cancellationToken)
        {
            if (_state != State.Initialized)
            {
                // cannot start listener if its already started or it was not yet initialized
                throw new InvalidOperationException(ErrorMessage(State.Initialized, _state));
            }
            // if there's no messageHandlers registed, startQueue is a NOOP
            if (_triggerExecutor.HasMessageHandlers())
            {
                await _sharedQueuelistener.StartAsync(cancellationToken);
            }

            _state = State.Started;
        }

        // assume if we have initialization error, this method will not be called
        internal void RegisterHandler(string functionId, IMessageHandler handler)
        {
            if (_state != State.Initialized)
            {
                // once listener started, we don't allow messageHandler registrations
                // this makes it easier to determine whether we should start queuelistener or just pretending
                throw new InvalidOperationException(ErrorMessage(State.Initialized, _state));
            }
            _triggerExecutor.Register(functionId, handler);
        }

        // assume functionId is already registered with _triggerExecutor
        internal Task EnqueueAsync(JObject message, string functionId, CancellationToken cancellationToken)
        {
            if (_state < State.Initialized || _state > State.Started)
            {
                throw new InvalidOperationException("Cannot enqueue messages, shared queue is either uninitialized or has already stopped listening");
            }
            return _sharedQueueWriter.EnqueueAsync(message, functionId, cancellationToken);
        }

        // IStorageQueueMessage is used in QueueTriggerBindingData
        private class SharedQueueExecutor : ITriggerExecutor<IStorageQueueMessage>
        {
            // concurrent dictionary, since CompositeListener start all Listeners in parallele
            // if we can assume all users of sharedQueue register their handler before calling listener.startAsync
            // ie, at createListenerAsync() which is ran sequantially, we don't need a concurrentDictionary (CompositeListenerFactory.CreateAsync)
            private readonly ConcurrentDictionary<string, IMessageHandler> _messageHandlers;
            internal SharedQueueExecutor()
            {
                _messageHandlers = new ConcurrentDictionary<string, IMessageHandler>();
            }
            // handle dequeued message, execute the function
            public async Task<FunctionResult> ExecuteAsync(IStorageQueueMessage value, CancellationToken cancellationToken)
            {
                QueueMessage message = JsonConvert.DeserializeObject<QueueMessage>(value.AsString, JsonSerialization.Settings);
                if (message == null)
                {
                    throw new InvalidOperationException("Invalid shared queue message.");
                }

                string functionId = message.FunctionId;

                if (functionId == null)
                {
                    throw new InvalidOperationException("Invalid function ID.");
                }

                // Ensure that the function ID is still valid
                FunctionResult successResult = new FunctionResult(true);
                IMessageHandler handler;
                if (!_messageHandlers.TryGetValue(functionId, out handler))
                {
                    // if we cannot find the functionID, return success
                    // this message will not be put to the poisonQueue
                    return successResult;
                }

                return await handler.TryExecuteAsync(message.Data, cancellationToken);
            }

            internal void Register(string functionId, IMessageHandler handler)
            {
                _messageHandlers.AddOrUpdate(functionId, handler, (i1, i2) => handler);
            }

            internal bool HasMessageHandlers()
            {
                return _messageHandlers.Count > 0;
            }
        }

        private class SharedQueueWriter
        {
            private readonly IStorageQueue _queue;
            private readonly IMessageEnqueuedWatcher _watcher;

            internal SharedQueueWriter(IStorageQueue queue, IMessageEnqueuedWatcher watcher)
            {
                _queue = queue;
                Debug.Assert(watcher != null);
                _watcher = watcher;
            }

            internal async Task EnqueueAsync(JObject data, String functionId, CancellationToken cancellationToken)
            {
                string contents = JsonConvert.SerializeObject(new QueueMessage(data, functionId), JsonSerialization.Settings);
                await _queue.AddMessageAndCreateIfNotExistsAsync(_queue.CreateMessage(contents), cancellationToken);
                _watcher.Notify(_queue.Name);
            }
        }

        private class QueueMessage
        {
            // public so that we can deserialize it
            public QueueMessage(JObject data, string functionId)
            {
                Data = data;
                FunctionId = functionId;
            }
            // public so that we can deserialize it
            public JObject Data { get; private set; }
            // public so that we can deserialize it
            public string FunctionId { get; private set; }
        }
    }
}

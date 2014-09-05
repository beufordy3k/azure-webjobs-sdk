﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Listeners;
using Microsoft.Azure.WebJobs.Host.Queues;
using Microsoft.Azure.WebJobs.Host.Queues.Listeners;
using Microsoft.Azure.WebJobs.Host.Timers;
using Microsoft.Azure.WebJobs.Host.Triggers;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Queue;

namespace Microsoft.Azure.WebJobs.Host.Blobs.Listeners
{
    internal class BlobListenerFactory : IListenerFactory
    {
        private readonly string _functionId;
        private readonly CloudBlobContainer _container;
        private readonly IBlobPathSource _input;
        private readonly ITriggeredFunctionInstanceFactory<ICloudBlob> _instanceFactory;

        public BlobListenerFactory(string functionId, CloudBlobContainer container, IBlobPathSource input,
            ITriggeredFunctionInstanceFactory<ICloudBlob> instanceFactory)
        {
            _functionId = functionId;
            _container = container;
            _input = input;
            _instanceFactory = instanceFactory;
        }

        public Task<IListener> CreateAsync(IFunctionExecutor executor, ListenerFactoryContext context)
        {
            SharedQueueWatcher sharedQueueWatcher = context.SharedListeners.GetOrCreate<SharedQueueWatcher>(
                new SharedQueueWatcherFactory(context));
            SharedBlobListener sharedBlobListener = context.SharedListeners.GetOrCreate<SharedBlobListener>(
                new SharedBlobListenerFactory(context));

            // Note that these clients are intentionally for the storage account rather than for the dashboard account.
            // We use the storage, not dashboard, account for the blob receipt container and blob trigger queues.
            CloudQueueClient queueClient = context.StorageAccount.CreateCloudQueueClient();
            CloudBlobClient blobClient = context.StorageAccount.CreateCloudBlobClient();

            string hostBlobTriggerQueueName = HostQueueNames.GetHostBlobTriggerQueueName(context.HostId);
            CloudQueue hostBlobTriggerQueue = queueClient.GetQueueReference(hostBlobTriggerQueueName);

            IListener blobDiscoveryToQueueMessageListener = CreateBlobDiscoveryToQueueMessageListener(context,
                sharedBlobListener, blobClient, hostBlobTriggerQueue, sharedQueueWatcher);
            IListener queueMessageToTriggerExecutionListener = CreateQueueMessageToTriggerExecutionListener(executor,
                context, sharedQueueWatcher, queueClient, hostBlobTriggerQueue, blobClient,
                sharedBlobListener.BlobWritterWatcher);
            IListener compositeListener = new CompositeListener(
                blobDiscoveryToQueueMessageListener,
                queueMessageToTriggerExecutionListener);
            return Task.FromResult(compositeListener);
        }

        private IListener CreateBlobDiscoveryToQueueMessageListener(ListenerFactoryContext context,
            SharedBlobListener sharedBlobListener,
            CloudBlobClient blobClient,
            CloudQueue hostBlobTriggerQueue,
            IMessageEnqueuedWatcher messageEnqueuedWatcher)
        {
            BlobTriggerExecutor triggerExecutor = new BlobTriggerExecutor(context.HostId, _functionId, _input,
                BlobETagReader.Instance, new BlobReceiptManager(blobClient),
                new BlobTriggerQueueWriter(hostBlobTriggerQueue, messageEnqueuedWatcher));
            sharedBlobListener.Register(_container, triggerExecutor);
            return new BlobListener(sharedBlobListener);
        }

        private IListener CreateQueueMessageToTriggerExecutionListener(IFunctionExecutor executor,
            ListenerFactoryContext context,
            SharedQueueWatcher sharedQueueWatcher,
            CloudQueueClient queueClient,
            CloudQueue hostBlobTriggerQueue,
            CloudBlobClient blobClient,
            IBlobWrittenWatcher blobWrittenWatcher)
        {
            SharedBlobQueueListener sharedListener = context.SharedListeners.GetOrCreate<SharedBlobQueueListener>(
                new SharedBlobQueueListenerFactory(executor, context, sharedQueueWatcher, queueClient,
                    hostBlobTriggerQueue, blobClient, blobWrittenWatcher));
            sharedListener.Register(_functionId, _instanceFactory);
            return new BlobListener(sharedListener);
        }
    }
}
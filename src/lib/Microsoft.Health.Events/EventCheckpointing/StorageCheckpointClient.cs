﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using EnsureThat;
using Microsoft.Health.Events.Model;

namespace Microsoft.Health.Events.EventCheckpointing
{
    public class StorageCheckpointClient : ICheckpointClient
    {
        private ConcurrentDictionary<string, Checkpoint> _checkpoints;
        private BlobContainerClient _storageClient;
        private static System.Timers.Timer _publisherTimer;
        private int _publishTimerInterval = 10000;

        public StorageCheckpointClient(StorageCheckpointOptions options)
        {
            EnsureArg.IsNotNull(options);
            EnsureArg.IsNotNullOrWhiteSpace(options.BlobPrefix);
            EnsureArg.IsNotNullOrWhiteSpace(options.BlobStorageConnectionString);
            EnsureArg.IsNotNullOrWhiteSpace(options.BlobContainerName);

            BlobPrefix = options.BlobPrefix;

            _checkpoints = new ConcurrentDictionary<string, Checkpoint>();
            _storageClient = new BlobContainerClient(options.BlobStorageConnectionString, options.BlobContainerName);

            SetPublisherTimer();
        }

        public string BlobPrefix { get; }

        public async Task UpdateCheckpointAsync(Checkpoint checkpoint)
        {
            EnsureArg.IsNotNull(checkpoint);
            EnsureArg.IsNotNullOrWhiteSpace(checkpoint.Id);

            var blobName = $"{BlobPrefix}/checkpoint/{checkpoint.Id}";
            var blobClient = _storageClient.GetBlobClient(blobName);

            var metadata = new Dictionary<string, string>()
            {
                { "LastProcessed", checkpoint.LastProcessed.DateTime.ToString("MM/dd/yyyy hh:mm:ss.fff tt") },
            };

            try
            {
                try
                {
                    await blobClient.SetMetadataAsync(metadata);
                }
                catch (RequestFailedException ex) when ((ex.ErrorCode == BlobErrorCode.BlobNotFound) || (ex.ErrorCode == BlobErrorCode.ContainerNotFound))
                {
                    using (var blobContent = new MemoryStream(Array.Empty<byte>()))
                    {
                        await blobClient.UploadAsync(blobContent, metadata: metadata).ConfigureAwait(false);
                    }
                }
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
            {
                // todo: log
                throw;
            }
            catch
            {
                // todo: log
                throw;
            }
            finally
            {
                // todo: log
            }
        }

        public Task<List<Checkpoint>> ListCheckpointsAsync()
        {
            var prefix = $"{BlobPrefix}/checkpoint";

            Task<List<Checkpoint>> GetCheckpointsAsync()
            {
                var checkpoints = new List<Checkpoint>();

                foreach (BlobItem blob in _storageClient.GetBlobs(traits: BlobTraits.Metadata, states: BlobStates.All, prefix: prefix, cancellationToken: CancellationToken.None))
                {
                    var partitionId = blob.Name.Split('/').Last();
                    DateTimeOffset lastEventTimestamp = DateTime.MinValue;

                    if (blob.Metadata.TryGetValue("LastProcessed", out var str))
                    {
                        DateTimeOffset.TryParse(str, null, DateTimeStyles.AssumeUniversal, out lastEventTimestamp);
                    }

                    checkpoints.Add(new Checkpoint
                    {
                        Prefix = BlobPrefix,
                        Id = partitionId,
                        LastProcessed = lastEventTimestamp,
                    });
                }

                return Task.FromResult(checkpoints);
            }

            try
            {
                // todo: consider retries
                return GetCheckpointsAsync();
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
            {
                // todo: log errors
                throw;
            }
            catch
            {
                // todo: log errors
                throw;
            }
            finally
            {
                // todo: log complete
            }
        }

        public Task<Checkpoint> GetCheckpointForPartitionAsync(string partitionIdentifier)
        {
            var prefix = $"{BlobPrefix}/checkpoint/{partitionIdentifier}";

            Task<Checkpoint> GetCheckpointAsync()
            {
                var checkpoint = new Checkpoint();

                foreach (BlobItem blob in _storageClient.GetBlobs(traits: BlobTraits.Metadata, states: BlobStates.All, prefix: prefix, cancellationToken: CancellationToken.None))
                {
                    var partitionId = blob.Name.Split('/').Last();
                    DateTimeOffset lastEventTimestamp = DateTime.MinValue;

                    if (blob.Metadata.TryGetValue("LastProcessed", out var str))
                    {
                        DateTimeOffset.TryParse(str, null, DateTimeStyles.AssumeUniversal, out lastEventTimestamp);
                    }

                    checkpoint.Prefix = BlobPrefix;
                    checkpoint.Id = partitionId;
                    checkpoint.LastProcessed = lastEventTimestamp;
                }

                return Task.FromResult(checkpoint);
            }

            try
            {
                // todo: consider retries
                return GetCheckpointAsync();
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
            {
                // todo: log errors
                throw;
            }
            catch
            {
                // todo: log errors
                throw;
            }
            finally
            {
                // todo: log complete
            }
        }

        public Task SetCheckpointAsync(IEventMessage eventArgs)
        {
            EnsureArg.IsNotNull(eventArgs);
            EnsureArg.IsNotNullOrWhiteSpace(eventArgs.PartitionId);

            try
            {
                var checkpoint = new Checkpoint();
                checkpoint.LastProcessed = eventArgs.EnqueuedTime;
                checkpoint.Id = eventArgs.PartitionId;
                checkpoint.Prefix = BlobPrefix;
                _checkpoints[eventArgs.PartitionId] = checkpoint;
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Console.WriteLine($"Checkpointing error: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public async Task PublishCheckpointsAsync(CancellationToken ct)
        {
            foreach (KeyValuePair<string, Checkpoint> checkpoint in _checkpoints)
            {
                await UpdateCheckpointAsync(checkpoint.Value);
            }
        }

        private void SetPublisherTimer()
        {
            _publisherTimer = new System.Timers.Timer(_publishTimerInterval);
            _publisherTimer.Elapsed += OnTimedEvent;
            _publisherTimer.AutoReset = true;
            _publisherTimer.Enabled = true;
        }

        private async void OnTimedEvent(object source, ElapsedEventArgs e)
        {
            try
            {
                await PublishCheckpointsAsync(CancellationToken.None);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Console.WriteLine(ex.Message);
            }
        }
    }
}
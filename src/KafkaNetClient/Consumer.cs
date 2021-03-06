﻿using KafkaNet.Model;
using KafkaNet.Protocol;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KafkaNet
{
    /// <summary>
    /// Provides a basic consumer of one Topic across all partitions or over a given whitelist of partitions.
    ///
    /// TODO: provide automatic offset saving when the feature is available in 0.8.2 https://cwiki.apache.org/confluence/display/KAFKA/A+Guide+To+The+Kafka+Protocol#AGuideToTheKafkaProtocol-OffsetCommit/FetchAPI
    /// TODO: replace _fetchResponseQueue to AsyncCollection!! it is not recommended to  use async and blocking code together look on https://github.com/StephenCleary/AsyncEx/blob/77b9711c2c5fd4ca28b220ce4c93d209eeca2b4a/Source/Nito.AsyncEx.Concurrent%20(NET4%2C%20Win8)/AsyncCollection.cs
    /// I don't use this consumer so i stop develop it i am using manual consumer instend
    /// </summary>

    public class Consumer : IMetadataQueries, IDisposable
    {
        private readonly ConsumerOptions _options;
        private readonly BlockingCollection<Message> _fetchResponseQueue;
        private readonly CancellationTokenSource _disposeToken = new CancellationTokenSource();
        private TaskCompletionSource<int> _disposeTask;
        private readonly ConcurrentDictionary<int, Task> _partitionPollingIndex = new ConcurrentDictionary<int, Task>();
        private readonly ConcurrentDictionary<int, long> _partitionOffsetIndex = new ConcurrentDictionary<int, long>();
        private readonly IMetadataQueries _metadataQueries;

        private int _disposeCount;
        private int _ensureOneThread;
        private Topic _topic;

        public Consumer(ConsumerOptions options, params OffsetPosition[] positions)
        {
            _options = options;
            _fetchResponseQueue = new BlockingCollection<Message>(_options.ConsumerBufferSize);
            _metadataQueries = new MetadataQueries(_options.Router);
            _disposeTask = new TaskCompletionSource<int>();
            SetOffsetPosition(positions);
        }

        /// <summary>
        /// Get the number of tasks created for consuming each partition.
        /// </summary>
        public int ConsumerTaskCount { get { return _partitionPollingIndex.Count; } }

        /// <summary>
        /// Returns a blocking enumerable of messages received from Kafka.
        /// </summary>
        /// <returns>Blocking enumberable of messages from Kafka.</returns>
        public IEnumerable<Message> Consume(CancellationToken? cancellationToken = null)
        {
            _options.Log.DebugFormat("Consumer: Beginning consumption of topic: {0}", _options.Topic);
            EnsurePartitionPollingThreads();
            return _fetchResponseQueue.GetConsumingEnumerable(cancellationToken ?? CancellationToken.None);
        }

        /// <summary>
        /// Force reset the offset position for a specific partition to a specific offset value.
        /// </summary>
        /// <param name="positions">Collection of positions to reset to.</param>
        public void SetOffsetPosition(params OffsetPosition[] positions)
        {
            foreach (var position in positions)
            {
                var temp = position;
                _partitionOffsetIndex.AddOrUpdate(position.PartitionId, i => temp.Offset, (i, l) => temp.Offset);
            }
        }

        /// <summary>
        /// Get the current running position (offset) for all consuming partition.
        /// </summary>
        /// <returns>List of positions for each consumed partitions.</returns>
        /// <remarks>Will only return data if the consumer is actively being consumed.</remarks>
        public List<OffsetPosition> GetOffsetPosition()
        {
            return _partitionOffsetIndex.Select(x => new OffsetPosition { PartitionId = x.Key, Offset = x.Value }).ToList();
        }

        private void EnsurePartitionPollingThreads()
        {
            try
            {
                if (Interlocked.Increment(ref _ensureOneThread) == 1)
                {
                    _options.Log.DebugFormat("Consumer: Refreshing partitions for topic: {0}", _options.Topic);
                    _options.Router.RefreshMissingTopicMetadata(_options.Topic).Wait();
                    var topic = _options.Router.GetTopicMetadataFromLocalCache(_options.Topic);
                    if (topic.Count <= 0) throw new ApplicationException(string.Format("Unable to get metadata for topic:{0}.", _options.Topic));
                    _topic = topic.First();

                    //create one thread per partition, if they are in the white list.
                    foreach (var partition in _topic.Partitions)
                    {
                        var partitionId = partition.PartitionId;
                        if (_options.PartitionWhitelist.Count == 0 || _options.PartitionWhitelist.Any(x => x == partitionId))
                        {
                            _partitionPollingIndex.AddOrUpdate(partitionId,
                                                               i => ConsumeTopicPartitionAsync(_topic.Name, partitionId),
                                                               (i, task) => task);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _options.Log.ErrorFormat("Exception occured trying to setup consumer for topic:{0}.  Exception={1}", _options.Topic, ex);
            }
            finally
            {
                Interlocked.Decrement(ref _ensureOneThread);
            }
        }

        private Task ConsumeTopicPartitionAsync(string topic, int partitionId)
        {
            bool needToRefreshMetadata = false;
            return Task.Run(async () =>
            {
                try
                {
                    var bufferSizeHighWatermark = FetchRequest.DefaultBufferSize;

                    _options.Log.DebugFormat("Consumer: Creating polling task for topic: {0} on parition: {1}", topic, partitionId);
                    while (_disposeToken.IsCancellationRequested == false)
                    {
                        try
                        {
                            //after error
                            if (needToRefreshMetadata)
                            {
                                await _options.Router.RefreshTopicMetadata(topic).ConfigureAwait(false);
                                EnsurePartitionPollingThreads();
                                needToRefreshMetadata = false;
                            }
                            //get the current offset, or default to zero if not there.
                            long offset = 0;
                            _partitionOffsetIndex.AddOrUpdate(partitionId, i => offset, (i, currentOffset) =>
                            {
                                offset = currentOffset;
                                return currentOffset;
                            });

                            //build a fetch request for partition at offset
                            var fetch = new Fetch
                            {
                                Topic = topic,
                                PartitionId = partitionId,
                                Offset = offset,
                                MaxBytes = bufferSizeHighWatermark,
                            };

                            var fetches = new List<Fetch> { fetch };

                            var fetchRequest = new FetchRequest
                            {
                                MaxWaitTime = (int)Math.Min(int.MaxValue, _options.MaxWaitTimeForMinimumBytes.TotalMilliseconds),
                                MinBytes = _options.MinimumBytes,
                                Fetches = fetches
                            };

                            //make request and post to queue
                            var route = _options.Router.SelectBrokerRouteFromLocalCache(topic, partitionId);

                            var taskSend = route.Connection.SendAsync(fetchRequest);

                            await Task.WhenAny(taskSend, _disposeTask.Task).ConfigureAwait(false);
                            if (_disposeTask.Task.IsCompleted) return;

                            //already done
                            var responses = await taskSend;
                            if (responses.Count > 0)
                            {
                                //we only asked for one response
                                var response = responses.FirstOrDefault();

                                if (response != null && response.Messages.Any())
                                {
                                    HandleResponseErrors(fetch, response);

                                    foreach (var message in response.Messages)
                                    {
                                        await Task.Run(() =>
                                        {
                                            //this is a block operation
                                            _fetchResponseQueue.Add(message, _disposeToken.Token);
                                        }, _disposeToken.Token).ConfigureAwait(false);

                                        if (_disposeToken.IsCancellationRequested) return;
                                    }

                                    var nextOffset = response.Messages.Last().Meta.Offset + 1;
                                    _partitionOffsetIndex.AddOrUpdate(partitionId, i => nextOffset, (i, l) => nextOffset);

                                    // sleep is not needed if responses were received
                                    continue;
                                }
                            }

                            //no message received from server wait a while before we try another long poll
                            await Task.Delay(_options.BackoffInterval, _disposeToken.Token);
                        }
                        catch (BufferUnderRunException ex)
                        {
                            bufferSizeHighWatermark = (int)(ex.RequiredBufferSize * _options.FetchBufferMultiplier) +
                                                      ex.MessageHeaderSize;
                            _options.Log.InfoFormat("Buffer underrun.  Increasing buffer size to: {0}",
                                bufferSizeHighWatermark);
                        }
                        catch (OffsetOutOfRangeException ex)
                        {
                            //TODO this turned out really ugly.  Need to fix this section.
                            _options.Log.ErrorFormat(ex.Message);
                            FixOffsetOutOfRangeExceptionAsync(ex.FetchRequest);
                        }
                        catch (InvalidMetadataException ex)
                        {
                            //refresh our metadata and ensure we are polling the correct partitions
                            needToRefreshMetadata = true;
                            _options.Log.ErrorFormat(ex.Message);
                        }
                        catch (TaskCanceledException ex)
                        {
                            //TODO :LOG
                        }
                        catch (Exception ex)
                        {
                            _options.Log.ErrorFormat("Exception occured while polling topic:{0} partition:{1}.  Polling will continue.  Exception={2}", topic, partitionId, ex);
                        }
                    }
                }
                finally
                {
                    _options.Log.DebugFormat("Consumer: Disabling polling task for topic: {0} on parition: {1}", topic, partitionId);
                    Task tempTask;
                    _partitionPollingIndex.TryRemove(partitionId, out tempTask);
                }
            });
        }

        private void HandleResponseErrors(Fetch request, FetchResponse response)
        {
            switch ((ErrorResponseCode)response.Error)
            {
                case ErrorResponseCode.NoError:
                    return;

                case ErrorResponseCode.OffsetOutOfRange:
                    throw new OffsetOutOfRangeException("FetchResponse indicated we requested an offset that is out of range.  Requested Offset:{0}", request.Offset) { FetchRequest = request };
                case ErrorResponseCode.BrokerNotAvailable:
                case ErrorResponseCode.ConsumerCoordinatorNotAvailableCode:
                case ErrorResponseCode.LeaderNotAvailable:
                case ErrorResponseCode.NotLeaderForPartition:
                    throw new InvalidMetadataException("FetchResponse indicated we may have mismatched metadata.  ErrorCode:{0}", response.Error) { ErrorCode = response.Error };
                default:
                    throw new KafkaApplicationException("FetchResponse returned error condition.  ErrorCode:{0}", response.Error) { ErrorCode = response.Error };
            }
        }

        private void FixOffsetOutOfRangeExceptionAsync(Fetch request)
        {
            _metadataQueries.GetTopicOffsetAsync(request.Topic)
                   .ContinueWith(t =>
                   {
                       try
                       {
                           var offsets = t.Result.FirstOrDefault(x => x.PartitionId == request.PartitionId);
                           if (offsets == null) return;

                           if (offsets.Offsets.Min() > request.Offset)
                               SetOffsetPosition(new OffsetPosition(request.PartitionId, offsets.Offsets.Min()));

                           if (offsets.Offsets.Max() < request.Offset)
                               SetOffsetPosition(new OffsetPosition(request.PartitionId, offsets.Offsets.Max()));
                       }
                       catch (Exception ex)
                       {
                           _options.Log.ErrorFormat("Failed to fix the offset out of range exception on topic:{0} partition:{1}.  Polling will continue.  Exception={2}",
                               request.Topic, request.PartitionId, ex);
                       }
                   });
        }

        public Topic GetTopicFromCache(string topic)
        {
            return _metadataQueries.GetTopicFromCache(topic);
        }

        public Task<List<OffsetResponse>> GetTopicOffsetAsync(string topic, int maxOffsets = 2, int time = -1)
        {
            return _metadataQueries.GetTopicOffsetAsync(topic, maxOffsets, time);
        }

        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposeCount) != 1) return;

            _options.Log.DebugFormat("Consumer: Disposing...");
            _disposeToken.Cancel();
            _disposeTask.SetResult(1);
            //wait for all threads to unwind
            foreach (var task in _partitionPollingIndex.Values.Where(task => task != null))
            {
                task.Wait(TimeSpan.FromSeconds(5));
            }

            using (_metadataQueries)
            using (_disposeToken)
            { }
        }
    }
}
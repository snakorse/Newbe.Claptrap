using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Newbe.Claptrap.AppMetrics;

namespace Newbe.Claptrap
{
    public class ConcurrentListBatchOperator<T> : IBatchOperator<T>
    {
        private readonly BatchOperatorOptions<T> _options;
        private readonly ObjectPool<BatchItem> _itemPool;
        private readonly IConcurrentListPool<BatchItem> _concurrentListPool;
        private readonly ILogger<ConcurrentListBatchOperator<T>> _logger;

        public delegate ConcurrentListBatchOperator<T> Factory(BatchOperatorOptions<T> options);

        private readonly BlockingCollection<ExceptionConcurrentList<BatchItem>> _tasksCollection;
        private readonly BlockingCollection<ExceptionConcurrentList<BatchItem>> _resultCollection;
        private readonly AutoFlushList<BatchItem> _autoFlushList;
        private readonly string _doManyFuncName;

        // ReSharper disable once NotAccessedField.Local
        private readonly Task _task;

        // ReSharper disable once NotAccessedField.Local
        private readonly Task _task2;

        // ReSharper disable once NotAccessedField.Local
        private readonly IDisposable _logHandler;

        public ConcurrentListBatchOperator(
            BatchOperatorOptions<T> options,
            ObjectPool<BatchItem> itemPool,
            AutoScaleAutoFlushListOptions.Factory autoScaleAutoFlushListOptionsFactory,
            AutoFlushList<BatchItem>.Factory autoFlushListFactory,
            IConcurrentListPool<BatchItem> concurrentListPool,
            ILogger<ConcurrentListBatchOperator<T>> logger)
        {
            if (options.BufferTime == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.BufferCount == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.MinBufferCount == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.MaxBufferCount == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _options = options;
            _itemPool = itemPool;
            _concurrentListPool = concurrentListPool;
            _logger = logger;
            _doManyFuncName = _options.DoManyFuncName ?? _options.DoManyFunc.ToString();
            var autoFlushListOptions = autoScaleAutoFlushListOptionsFactory
                .Invoke(_options.BufferCount.Value,
                    _options.BufferTime.Value,
                    _options.MinBufferCount.Value,
                    _options.MaxBufferCount.Value);
            _autoFlushList = autoFlushListFactory.Invoke(autoFlushListOptions,
                concurrentListPool,
                Func);
            _tasksCollection = new BlockingCollection<ExceptionConcurrentList<BatchItem>>();
            _resultCollection = new BlockingCollection<ExceptionConcurrentList<BatchItem>>();
            _task = Task.Factory.StartNew(ConsumeTasks, TaskCreationOptions.LongRunning).Unwrap();
            _task2 = Task.Factory.StartNew(ConsumeResult, TaskCreationOptions.LongRunning);
            _logHandler = Observable.Interval(TimeSpan.FromSeconds(10))
                .Select(x => _tasksCollection.Count)
                .DistinctUntilChanged()
                .Subscribe(count =>
                {
                    _logger.LogInformation(
                        "{funcName}: there are {count} batch tasks in list waiting to be handled",
                        _doManyFuncName,
                        count);
                });
        }

        private void ConsumeResult()
        {
            while (true)
            {
                try
                {
                    foreach (var (batchItems, exception) in _resultCollection.GetConsumingEnumerable())
                    {
                        var items = batchItems.Buffer[..batchItems.Count];
                        _logger.LogTrace("new batch coming with {count} items", batchItems.Count);
                        try
                        {
                            if (exception != null)
                            {
                                foreach (var item in items)
                                {
                                    item.Vts.SetException(exception);
                                }
                            }
                            else
                            {
                                foreach (var item in items)
                                {
                                    item.Vts.SetResult(0);
                                }
                            }
                        }

                        finally
                        {
                            _concurrentListPool.Return(batchItems);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "error while handle result");
                }
            }
        }

        private async Task ConsumeTasks()
        {
            while (true)
            {
                try
                {
                    foreach (var (batchItems, _) in _tasksCollection.GetConsumingEnumerable())
                    {
                        var items = batchItems.Buffer[..batchItems.Count];
                        ClaptrapMetrics.MeasureBatchOperatorGauge(_doManyFuncName, batchItems.Count);
                        ClaptrapMetrics.MeasureBatchOperatorMaxCountGauge(_doManyFuncName, _options.BufferCount!.Value);
                        using var _ = ClaptrapMetrics.MeasureBatchOperatorTime(_doManyFuncName);
                        try
                        {
                            await _options.DoManyFunc.Invoke(items.Select(x => x.Input), null);
                            _resultCollection.Add(new ExceptionConcurrentList<BatchItem>
                            {
                                ConcurrentList = batchItems
                            });
                        }
                        catch (Exception e)
                        {
                            _resultCollection.Add(new ExceptionConcurrentList<BatchItem>
                            {
                                ConcurrentList = batchItems,
                                Exception = e
                            });
                            _logger.LogError(e, "error while handle a batch");
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "error while try to consume data from task collection");
                }
            }
        }

        private Task Func(ConcurrentList<BatchItem> arg)
        {
            _tasksCollection.Add(new ExceptionConcurrentList<BatchItem>
            {
                ConcurrentList = arg
            });
            return Task.CompletedTask;
        }

        public async ValueTask CreateTask(T input)
        {
            var item = _itemPool.Get();
            item.Input = input;
            var valueTask = _autoFlushList.Push(item);
            if (valueTask.IsCompleted)
            {
                await valueTask;
            }

            var final = new ValueTask(item.Vts, item.Vts.Version);
            if (!final.IsCompleted)
            {
                await final;
            }
        }

        public class BatchItem
        {
            public T Input { get; set; } = default!;
            public ManualResetValueTaskSource<int> Vts { get; set; } = null!;
        }
    }
}
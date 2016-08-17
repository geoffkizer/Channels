// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Channels
{
    public class MemoryPoolChannel : IReadableChannel, IWritableChannel, IDisposable
    {
        private static readonly Action _awaitableIsCompleted = () => { };
        private static readonly Action _awaitableIsNotCompleted = () => { };

        private readonly MemoryPool _memory;
        private readonly ManualResetEventSlim _manualResetEvent = new ManualResetEventSlim(false, 0);

        private Action _awaitableState;

//        private MemoryPoolBlock _head;
//        private MemoryPoolBlock _tail;
        private LinkedBuffers _head;
        private LinkedBuffers _tail;

        private bool _completedWriting;
        private bool _completedReading;

        private int _consumingState;
        private object _sync = new object();
        private readonly TaskCompletionSource<object> _tcs = new TaskCompletionSource<object>();

        private Action _startReadingCallback;
        private Action _disposeCallback;

        public MemoryPoolChannel(MemoryPool memory)
        {
            _memory = memory;
            _awaitableState = _awaitableIsNotCompleted;
        }

        public void OnStartReading(Action callback)
        {
            _startReadingCallback = callback;
        }

        public void OnDispose(Action disposeCallback)
        {
            _disposeCallback = disposeCallback;
        }

        public Task Completion => _tcs.Task;

        public bool IsCompleted => ReferenceEquals(_awaitableState, _awaitableIsCompleted);

        public WritableBuffer BeginWrite(int minimumSize = 0)
        {
            LinkedBuffers link = null;

            if (_tail != null)
            {
                int remaining = _tail.Block.Data.Offset + _tail.Block.Data.Count - _tail.Block.End;

                if (minimumSize <= remaining && remaining > 0)
                {
                    link = _tail;
                }
            }

            if (link == null)
            {
                var block = _memory.Lease();
                link = new LinkedBuffers() { Block = block, Next = null };
            }

            lock (_sync)
            {
                if (_head == null)
                {
                    _head = link;
                }
                else if (link != _tail)
                {
//                    Volatile.Write(ref _tail.Next, block);
                    _tail.Next = link;
                    _tail = link;
                }

                return new WritableBuffer(link, link.Block.End);
            }
        }

        public Task EndWriteAsync(WritableBuffer end)
        {
            lock (_sync)
            {
                if (!end.IsDefault)
                {
                    _tail = end.Link;
                    _tail.Block.End = end.Index;
                }

                Complete();

                return Task.FromResult(0);
            }
        }

        private void Complete(bool dispatch = false)
        {
            var awaitableState = Interlocked.Exchange(
                ref _awaitableState,
                _awaitableIsCompleted);

            _manualResetEvent.Set();

            if (!ReferenceEquals(awaitableState, _awaitableIsCompleted) &&
                !ReferenceEquals(awaitableState, _awaitableIsNotCompleted))
            {
                if (dispatch)
                {
                    Task.Run(awaitableState);
                }
                else
                {
                    awaitableState();
                }
            }
        }

        public ReadableBuffer BeginRead()
        {
            if (Interlocked.CompareExchange(ref _consumingState, 1, 0) != 0)
            {
                throw new InvalidOperationException("Already consuming input.");
            }

            return new ReadableBuffer(_head);
        }

        public void EndRead(ReadableBuffer end)
        {
            EndRead(end, end);
        }

        public void EndRead(
            ReadableBuffer consumed,
            ReadableBuffer examined)
        {
//            MemoryPoolBlock returnStart = null;
//            MemoryPoolBlock returnEnd = null;
            LinkedBuffers returnStart = null;
            LinkedBuffers returnEnd = null;

            lock (_sync)
            {
                if (!consumed.IsDefault)
                {
                    returnStart = _head;
                    returnEnd = consumed.Link;
                    _head = consumed.Link;
                    _head.Block.Start = consumed.Index;
                }

                if (!examined.IsDefault &&
                    examined.IsEnd &&
                    Completion.Status == TaskStatus.WaitingForActivation)
                {
                    _manualResetEvent.Reset();

                    Interlocked.CompareExchange(
                        ref _awaitableState,
                        _awaitableIsNotCompleted,
                        _awaitableIsCompleted);
                }
            }

            while (returnStart != returnEnd)
            {
                var returnBlock = returnStart;
                returnStart = returnStart.Next;
                returnBlock.Block.Pool.Return(returnBlock.Block);
            }

            if (Interlocked.CompareExchange(ref _consumingState, 0, 1) != 1)
            {
                throw new InvalidOperationException("No ongoing consuming operation to complete.");
            }
        }

        public void CompleteAwaiting() => Complete();

        public void CompleteWriting(Exception error = null)
        {
            lock (_sync)
            {
                _completedWriting = true;

                if (error != null)
                {
                    _tcs.TrySetResult(error);
                }
                else
                {
                    _tcs.TrySetResult(null);
                }

                Complete();

                if (_completedReading)
                {
                    Dispose();
                }
            }
        }

        public void CompleteReading()
        {
            lock (_sync)
            {
                _completedReading = true;

                if (_completedWriting)
                {
                    Dispose();
                }
            }
        }

        public IReadableChannel GetAwaiter()
        {
            return this;
        }

        public void OnCompleted(Action continuation)
        {
            Interlocked.Exchange(ref _startReadingCallback, null)?.Invoke();

            var awaitableState = Interlocked.CompareExchange(
                ref _awaitableState,
                continuation,
                _awaitableIsNotCompleted);

            if (ReferenceEquals(awaitableState, _awaitableIsNotCompleted))
            {
                return;
            }
            else if (ReferenceEquals(awaitableState, _awaitableIsCompleted))
            {
                // Dispatch here to avoid stack diving
                Task.Run(continuation);
            }
            else
            {
                _tcs.SetException(new InvalidOperationException("Concurrent reads are not supported."));

                Interlocked.Exchange(
                    ref _awaitableState,
                    _awaitableIsCompleted);

                _manualResetEvent.Set();

                Task.Run(continuation);
                Task.Run(awaitableState);
            }
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            OnCompleted(continuation);
        }

        public void GetResult()
        {
            if (!IsCompleted)
            {
                _manualResetEvent.Wait();
            }

            var error = _tcs.Task.Exception?.InnerException;
            if (error != null)
            {
                throw error;
            }
        }

        public void Dispose()
        {
            Debug.Assert(_completedWriting, "Not completed writing");
            Debug.Assert(_completedReading, "Not completed reading");

            lock (_sync)
            {
                // Return all blocks
                var block = _head;
                while (block != null)
                {
                    var returnBlock = block;
                    block = block.Next;

                    returnBlock.Block.Pool.Return(returnBlock.Block);
                }

                _head = null;
                _tail = null;

                Interlocked.Exchange(ref _disposeCallback, null)?.Invoke();
            }
        }
    }
}

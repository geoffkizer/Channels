﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Channels
{
    public abstract class ReadableChannel : IReadableChannel
    {
        protected MemoryPoolChannel _channel;

        public ReadableChannel(MemoryPool pool)
        {
            _channel = new MemoryPoolChannel(pool);
        }

        public Task Completion => _channel.Completion;

        public bool IsCompleted => _channel.IsCompleted;

        public ReadableBuffer BeginRead() => _channel.BeginRead();

        public void CompleteReading() => _channel.CompleteReading();

        public void EndRead(ReadableBuffer consumed, ReadableBuffer examined) => _channel.EndRead(consumed, examined);

        public IReadableChannel GetAwaiter() => _channel.GetAwaiter();

        public void GetResult() => _channel.GetResult();

        public void OnCompleted(Action continuation) => _channel.OnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation) => _channel.UnsafeOnCompleted(continuation);
    }
}

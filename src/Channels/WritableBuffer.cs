// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;

namespace Channels
{
    public struct WritableBuffer
    {
        private LinkedBuffers _buffers;

        // These are now cached values from the head of LinkedBuffers
        private MemoryPoolBlock _block;
        private int _index;

#if false
        public WritableBuffer(MemoryPoolBlock block)
        {
            _buffers = new LinkedBuffers() { Block = block, Next = null };
            _block = block;
            _index = _block?.Start ?? 0;
        }

        public WritableBuffer(MemoryPoolBlock block, int index)
        {
            _buffers = new LinkedBuffers() { Block = block, Next = null };
            _block = block;
            _index = index;
        }
#endif

        public WritableBuffer(LinkedBuffers buffers, int index)
        {
            _buffers = buffers;
            _block = buffers.Block;
            _index = index;
        }

        internal MemoryPoolBlock Block => _block;

        internal int Index => _index;

        internal LinkedBuffers Link => _buffers; 

        internal bool IsDefault => _block == null;

        public BufferSpan Memory => new BufferSpan(Block.DataArrayPtr, Block.Array, Block.End, Block.Data.Offset + Block.Data.Count - Block.End);

        public void Write(byte data)
        {
            if (IsDefault)
            {
                return;
            }

            Debug.Assert(_block != null);
            Debug.Assert(_buffers.Next == null);
            Debug.Assert(_block.End == _index);

            var pool = _block.Pool;
            var block = _block;
            var blockIndex = _index;

            var bytesLeftInBlock = block.Data.Offset + block.Data.Count - blockIndex;

            var link = _buffers;
            if (bytesLeftInBlock == 0)
            {
                var nextBlock = pool.Lease();
                block.End = blockIndex;

                var nextLink = new LinkedBuffers() { Block = nextBlock, Next = null };
//                    Volatile.Write(ref link.Next, nextLink);
                link.Next = nextLink;
                block = nextBlock;
                link = nextLink;

                blockIndex = block.Data.Offset;
                bytesLeftInBlock = block.Data.Count;
            }

            block.Array[blockIndex] = data;

            blockIndex++;

            block.End = blockIndex;
            _buffers = link;
            _block = block;
            _index = blockIndex;
        }

        public void Write(byte[] data, int offset, int count)
        {
            if (IsDefault)
            {
                return;
            }

            Debug.Assert(_block != null);
            Debug.Assert(_buffers.Next == null);
            Debug.Assert(_block.End == _index);

            var pool = _block.Pool;
            var block = _block;
            var blockIndex = _index;

            var bufferIndex = offset;
            var remaining = count;
            var bytesLeftInBlock = block.Data.Offset + block.Data.Count - blockIndex;

            var link = _buffers;
            while (remaining > 0)
            {
                if (bytesLeftInBlock == 0)
                {
                    var nextBlock = pool.Lease();
                    block.End = blockIndex;

                    var nextLink = new LinkedBuffers() { Block = nextBlock, Next = null };
//                    Volatile.Write(ref link.Next, nextLink);
                    link.Next = nextLink;
                    block = nextBlock;
                    link = nextLink;

                    blockIndex = block.Data.Offset;
                    bytesLeftInBlock = block.Data.Count;
                }

                var bytesToCopy = remaining < bytesLeftInBlock ? remaining : bytesLeftInBlock;

                Buffer.BlockCopy(data, bufferIndex, block.Array, blockIndex, bytesToCopy);

                blockIndex += bytesToCopy;
                bufferIndex += bytesToCopy;
                remaining -= bytesToCopy;
                bytesLeftInBlock -= bytesToCopy;
            }

            block.End = blockIndex;
            _buffers = link;
            _block = block;
            _index = blockIndex;
        }

#if false       // is this used?
        public void Append(ReadableBuffer begin, ReadableBuffer end)
        {
            Debug.Assert(_block != null);
            Debug.Assert(_buffers.Next == null);
            Debug.Assert(_block.End == _index);

            _buffers.Next = begin.Block
            _block.Next = begin.Block;
            _block = end.Block;
            _index = end.Block.End;
        }
#endif

        public void UpdateWritten(int bytesWritten)
        {
            Debug.Assert(_block != null);
            Debug.Assert(_buffers.Next == null);
            Debug.Assert(_block.End == _index);

            var block = _block;
            var blockIndex = _index + bytesWritten;

            Debug.Assert(blockIndex <= block.Data.Offset + block.Data.Count);

            block.End = blockIndex;
            _block = block;
            _index = blockIndex;
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            for (int i = 0; i < (_block.End - _index); i++)
            {
                builder.Append(_block.Array[i + _index].ToString("X2"));
                builder.Append(" ");
            }
            return builder.ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Channels
{
    public class LinkedBuffers
    {
        // Simplistic implementation for now
        public MemoryPoolBlock Block { get; set; }
        public LinkedBuffers Next { get; set; }
    }
}

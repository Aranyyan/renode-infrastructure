//
// Copyright (c) 2010-2018 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;
using Antmicro.Renode.Logging;
using System.Threading;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class SimpleTicker : IDoubleWordPeripheral
    {
        public SimpleTicker(ulong periodInMs, Machine machine)
        {
            var clockSource = machine.ObtainClockSource();
            clockSource.AddClockEntry(new ClockEntry(periodInMs, ClockEntry.FrequencyToRatio(this, 1000), OnTick));
        }

        public virtual uint ReadDoubleWord(long offset)
        {
            return (uint)Interlocked.CompareExchange(ref counter, 0, 0);
        }

        public virtual void WriteDoubleWord(long offset, uint value)
        {
            this.LogUnhandledWrite(offset, value);
        }

        public virtual void Reset()
        {
            Interlocked.Exchange(ref counter, 0);
        }

        private void OnTick()
        {
            Interlocked.Increment(ref counter);
        }

        private int counter;
    }
}


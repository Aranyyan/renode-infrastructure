//
// Copyright (c) 2010-2020 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Linq;
using System.Collections.Generic;
using Antmicro.Renode.Peripherals.Sensor;
using Antmicro.Renode.Peripherals.I2C;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Peripherals.UART;

namespace Antmicro.Renode.Peripherals.Sensors
{
    public class BMI088_Gyroscope : II2CPeripheral, IProvidesRegisterCollection<ByteRegisterCollection>, ISensor, IUART
    {
        public BMI088_Gyroscope()
        {
            fifo = new SensorSamplesFifo<Vector3DSample>();
            RegistersCollection = new ByteRegisterCollection(this);
            Int3 = new GPIO();
            Int4 = new GPIO();
            DefineRegisters();
        }

        //HACK To make communication with an external program easy, implements the IUART interface
        #pragma warning disable 0067
            public event Action<byte> CharReceived;
        #pragma warning restore 0067
        public void WriteChar(byte value)
        {
            TriggerDataInterrupt();
        }

        public uint BaudRate { get; }
        public Bits StopBits { get; }
        public Parity ParityBit { get; }

        public void Reset()
        {
            RegistersCollection.Reset();
            registerAddress = 0;
            Int3.Unset();
            Int4.Unset();
            this.Log(LogLevel.Noisy, "Reset registers");
        }

        public void Write(byte[] data)
        {
            if(data.Length == 0)
            {
                this.Log(LogLevel.Warning, "Unexpected write with no data");
                return;
            }

            this.Log(LogLevel.Noisy, "Write with {0} bytes of data: {1}", data.Length, Misc.PrettyPrintCollectionHex(data));
            registerAddress = (Registers)data[0];

            if(data.Length > 1)
            {
                // Skip the first byte as it contains register address
                // Must skip final byte, problem with I2C
                for(var i = 1; i < data.Length - 1; i++)
                {
                 this.Log(LogLevel.Noisy, "Writing 0x{0:X} to register {1} (0x{1:X})", data[i], registerAddress);
                 RegistersCollection.Write((byte)registerAddress, data[i]);
                 registerAddress++;
                }
            }
            else
            {
                this.Log(LogLevel.Noisy, "Preparing to read register {0} (0x{0:X})", registerAddress);
            }
        }

        public byte ReadRegister(byte offset)
        {
            return RegistersCollection.Read(offset);
        }

        public byte[] Read(int count)
        {
            // If registerAddress = 0x02 (xLSB) return 6 bytes (x,y,z)
            // else return 1 byte i.e. the register
            var result = new byte[registerAddress==Registers.RateXLSB?6:1];
            for(var i = 0; i < result.Length; i++)
            {
                result[i] = RegistersCollection.Read((byte)registerAddress + i);
                this.Log(LogLevel.Noisy, "Read value 0x{0:X} from register {1} (0x{1:X})", result[i], (Registers)registerAddress + i);
            }
            return result;
        }

        public void FinishTransmission()
        {
        }

        public ByteRegisterCollection RegistersCollection { get; }
        public GPIO Int3 { get; }
        public GPIO Int4 { get; }

        public void TriggerDataInterrupt()
        {
            if(dataEn.Value)
            {
                if(int3Data.Value)
                {
                    Int3.Set(false);
                    Int3.Set(true);
                    Int3.Set(false);
                    this.Log(LogLevel.Noisy, "Data interrupt triggered on pin 3!");
                }
                if(int4Data.Value)
                {
                    Int4.Set(false);
                    Int4.Set(true);
                    Int4.Set(false);
                    this.Log(LogLevel.Noisy, "Data interrupt triggered on pin 4!");
                }
            }
        }

        public void FeedGyroSample(decimal x, decimal y, decimal z, int repeat = 1)
        {
            var sample = new Vector3DSample(x, y, z);
            for(var i = 0; i < repeat; i++)
            {
                fifo.FeedSample(sample);
                fifo.TryDequeueNewSample();
            }
        }

        public void FeedGyroSample(string path)
        {
            fifo.FeedSamplesFromFile(path);
        }

        private void DefineRegisters()
        {
            Registers.GyroChipID.Define(this, 0x0F); //RO
            Registers.RateXLSB.Define(this, 0x00)
                .WithValueField(0, 8, FieldMode.Read, name: "RATE_X_LSB", valueProviderCallback: _ => DPStoByte(fifo.Sample.X, false)); //RO
            Registers.RateXMSB.Define(this, 0x00)
                .WithValueField(0, 8, FieldMode.Read, name: "RATE_X_MSB", valueProviderCallback: _ => DPStoByte(fifo.Sample.X, true)); //RO
            Registers.RateYLSB.Define(this, 0x00)
                .WithValueField(0, 8, FieldMode.Read, name: "RATE_Y_LSB", valueProviderCallback: _ => DPStoByte(fifo.Sample.Y, false)); //RO
            Registers.RateYMSB.Define(this, 0x00)
                .WithValueField(0, 8, FieldMode.Read, name: "RATE_Y_MSB", valueProviderCallback: _ => DPStoByte(fifo.Sample.Y, true)); //RO
            Registers.RateZLSB.Define(this, 0x00)
                .WithValueField(0, 8, FieldMode.Read, name: "RATE_Z_LSB", valueProviderCallback: _ => DPStoByte(fifo.Sample.Z, false)); //RO
            Registers.RateZMSB.Define(this, 0x00)
                .WithValueField(0, 8, FieldMode.Read, name: "RATE_Z_MSB", valueProviderCallback: _ => DPStoByte(fifo.Sample.Z, true)); //RO

            Registers.GyroIntStat1.Define(this, 0x00)
                .WithReservedBits(0, 4)
                .WithFlag(4, name: "fifo_int")
                .WithReservedBits(5, 2)
                .WithFlag(7, name: "gyro_drdy"); //RO

            Registers.GyroRange.Define(this, 0x00)
                .WithValueField(0, 8, out gyroRange, name: "gyro_range"); //RW
            Registers.GyroBandwidth.Define(this, 0x80)
                .WithValueField(0, 8, name: "gyro_bw"); //RW //TODO should be used to determine output data rate
            Registers.GyroLPM1.Define(this, 0x00); //RW
            Registers.GyroSoftreset.Define(this, 0x00) //WO
                .WithWriteCallback((_, val) =>
                {
                    if(val == resetCommand)
                    {
                        Reset();
                    }
                });
            Registers.GyroIntCtrl.Define(this, 0x00)
                .WithReservedBits(0, 6)
                .WithFlag(6, out fifoEn, name: "fifo_en") // Currently unused
                .WithFlag(7, out dataEn, name: "data_en");
            Registers.Int3Int4IOConf.Define(this, 0x0F)
                .WithFlag(0, name: "int3_lvl")
                .WithFlag(1, name: "int3_od")
                .WithFlag(2, name: "int4_lvl")
                .WithFlag(3, name: "int4_od")
                .WithReservedBits(4, 4); // TODO implement?
            Registers.Int3Int4IOMap.Define(this, 0x00)
                .WithFlag(0, out int3Data, name: "int3_data")
                .WithReservedBits(1, 1)
                .WithFlag(2, out int3Fifo, name: "int3_fifo")
                .WithReservedBits(3, 2)
                .WithFlag(5, out int4Fifo, name: "int4_fifo")
                .WithReservedBits(6, 1)
                .WithFlag(7, out int4Data, name: "int4_data");
            Registers.GyroSelfTest.Define(this, 0x12); // HACK: Reset value is value to be read on succesful self test and not actual reset value.
        }

        private Registers registerAddress;
        private readonly SensorSamplesFifo<Vector3DSample> fifo;

        private IValueRegisterField gyroRange;

        private IFlagRegisterField dataEn;
        private IFlagRegisterField fifoEn;
        private IFlagRegisterField int3Data;
        private IFlagRegisterField int3Fifo;
        private IFlagRegisterField int4Fifo;
        private IFlagRegisterField int4Data;

        private const byte resetCommand = 0xB6;

        private byte DPStoByte(decimal rawData, bool msb)
        {
            rawData = rawData*(decimal)16.384*(1<<(short)gyroRange.Value);
            short converted = (short)(rawData > Int16.MaxValue ? Int16.MaxValue : rawData < Int16.MinValue ? Int16.MinValue : rawData);
            return (byte)(converted >> (msb ? 8 : 0));
        }

        private enum Registers
        {
            GyroChipID = 0x00, // Read-Only
            // 0x01 reserved
            RateXLSB = 0x02, // Read-Only
            RateXMSB = 0x03, // Read-Only
            RateYLSB = 0x04, // Read-Only
            RateYMSB = 0x05, // Read-Only
            RateZLSB = 0x06, // Read-Only
            RateZMSB = 0x07, // Read-Only
            // 0x08 - 0x09 reserved
            GyroIntStat1 = 0x0A, // Read-Only
            // 0x0B - 0x0D reserved
            FIFOStatus = 0x0E, // Read-Only
            GyroRange = 0x0F, // Read-Write
            GyroBandwidth = 0x10, // Read-Write
            GyroLPM1 = 0x11, // Read-Write
            // 0x12 - 0x13 reserved
            GyroSoftreset = 0x14, // Write-Only
            GyroIntCtrl = 0x15, // Read-Write
            Int3Int4IOConf = 0x16, // Read-Write
            // 0x17 reserved
            Int3Int4IOMap = 0x18, // Read-Write
            // 0x19 - 0x1D reserved
            FIFOWmEn = 0x1E, // Read-Write
            // 0x1F - 0x33 reseved
            FIFOExtIntS = 0x34, // Read-Write
            // 0x35 - 0x3B reserved
            GyroSelfTest = 0x3C,
            FIFOConfig0 = 0x3D, // Read-Write
            FIFOConfig1 = 0x3E, // Read-Write
            FIFOData = 0x3F // Read-Only
        }
    }
}

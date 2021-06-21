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

namespace Antmicro.Renode.Peripherals.Sensors
{
    public class BMI088_Accelerometer : II2CPeripheral, IProvidesRegisterCollection<ByteRegisterCollection>, ISensor
    {
        public BMI088_Accelerometer()
        {
            fifo = new SensorSamplesFifo<Vector3DSample>();
            RegistersCollection = new ByteRegisterCollection(this);
            Int1 = new GPIO();
            Int2 = new GPIO();
            DefineRegisters();
        }

        public void Reset()
        {
            RegistersCollection.Reset();
            registerAddress = 0;
            Int1.Unset();
            Int2.Unset();
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
            var result = new byte[registerAddress==Registers.AccXLSB?6:1];
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
        public GPIO Int1 { get; }
        public GPIO Int2 { get; }

        public void TriggerDataInterrupt()
        {
           // TODO: TriggerDataInterrupt
        }

        public void FeedAccSample(decimal x, decimal y, decimal z, int repeat = 1)
        {

            var sample = new Vector3DSample(x, y, z);
            for(var i = 0; i < repeat; i++)
            {
                fifo.FeedSample(sample);
                fifo.TryDequeueNewSample();
            }
        }

        public void FeedAccSample(string path)
        {
            fifo.FeedSamplesFromFile(path);
        }

        private void DefineRegisters()
        {
            Registers.AccChipID.Define(this, 0x1E); //RO
            Registers.AccXLSB.Define(this, 0x00)
                .WithValueField(0, 8, FieldMode.Read, name: "ACC_X_LSB", valueProviderCallback: _ => mgToByte(fifo.Sample.X, false)); //RO
            Registers.AccXMSB.Define(this, 0x00)
                .WithValueField(0, 8, FieldMode.Read, name: "ACC_X_MSB", valueProviderCallback: _ => mgToByte(fifo.Sample.X, true)); //RO
            Registers.AccYLSB.Define(this, 0x00)
                .WithValueField(0, 8, FieldMode.Read, name: "ACC_Y_LSB", valueProviderCallback: _ => mgToByte(fifo.Sample.Y, false)); //RO
            Registers.AccYMSB.Define(this, 0x00)
                .WithValueField(0, 8, FieldMode.Read, name: "ACC_Y_MSB", valueProviderCallback: _ => mgToByte(fifo.Sample.Y, true)); //RO
            Registers.AccZLSB.Define(this, 0x00)
                .WithValueField(0, 8, FieldMode.Read, name: "ACC_Z_LSB", valueProviderCallback: _ => mgToByte(fifo.Sample.Z, false)); //RO
            Registers.AccZMSB.Define(this, 0x00)
                .WithValueField(0, 8, FieldMode.Read, name: "ACC_Z_MSB", valueProviderCallback: _ => mgToByte(fifo.Sample.Z, true)); //RO
            Registers.AccConf.Define(this, 0xA8)
                .WithValueField(0, 4, name: "acc_odr")
                .WithValueField(4, 4, name: "acc_bwp"); //RW
            Registers.AccRange.Define(this, 0x01)
                .WithValueField(0, 2, out accRange, name: "acc_range")
                .WithReservedBits(2, 6); //RW
            Registers.AccPwrConf.Define(this, 0x03)
                .WithValueField(0, 8, name: "pwr_save_mode"); //RW
            Registers.AccPwrCtrl.Define(this, 0x00)
                .WithValueField(0, 8, name: "acc_enable"); //RW
            Registers.AccSoftreset.Define(this, 0x00) //WO
                .WithWriteCallback((_, val) =>
                {
                    if(val == resetCommand)
                    {
                        Reset();
                    }
                });
        }

        private Registers registerAddress;
        private readonly SensorSamplesFifo<Vector3DSample> fifo;

        private IValueRegisterField accRange;

        private const byte resetCommand = 0xB6;

        private byte mgToByte(decimal rawData, bool msb)
        {
            rawData = rawData * 65536 / ((decimal)(1000 * 3 * (1 << (short)accRange.Value)));
            short converted = (short)(rawData > Int16.MaxValue ? Int16.MaxValue : rawData < Int16.MinValue ? Int16.MinValue : rawData);
            return (byte)(converted >> (msb ? 8 : 0));
        }

        private enum Registers
        {
            AccChipID = 0x00, // Read-Only
            // 0x01 reserved
            AccErrReg = 0x02, // Read-Only
            AccStatus = 0x03, // Read-Only
            // 0x04 - 0x11 reserved
            AccXLSB = 0x12, // Read-Only
            AccXMSB = 0x13, // Read-Only
            AccYLSB = 0x14, // Read-Only
            AccYMSB = 0x15, // Read-Only
            AccZLSB = 0x16, // Read-Only
            AccZMSB = 0x17, // Read-Only
            Sensortime0 = 0x18, // Read-Only
            Sensortime1 = 0x19, // Read-Only
            Sensortime2 = 0x1A,  // Read-Only
            // 0x1B - 0x1C reserved
            AccIntStat1 = 0x1D, // Read-Only
            // 0x1E - 0x21 reserved
            TempMSB = 0x22, // Read-Only
            TempLSB = 0x23, // Read-Only
            FIFOLength0 = 0x24, // Read-Only
            FIFOLength1 = 0x25, // Read-Only
            FIFOData = 0x26, // Read-Only
            // 0x27 - 0x3F reserved
            AccConf = 0x40, // Read-Write
            AccRange = 0x41, // Read-Write
            // 0x42 - 0x44 reserved
            FIFODowns = 0x45, // Read-Write
            FIFOWTM0 = 0x46, // Read-Write
            FIFOWTM1 = 0x47, // Read-Write
            FIFOConfig0 = 0x48, // Read-Write
            FIFOConfig1 = 0x49, // Read-Write
            // 0x4A - 0x52
            Int1IOCtrl = 0x53, // Read-Write
            Int2IOCtrl = 0x54, // Read-Write
            // 0x55 - 0x57 reserved
            IntMapData = 0x58, // Read-Write
            // 0x59 - 0x6C reserved
            AccSelfTest = 0x6D, // Read-Write
            // 0x6E - 0x7B reserved
            AccPwrConf = 0x7C, // Read-Write
            AccPwrCtrl = 0x7D, // Read-Write
            AccSoftreset = 0x7E // Write-Only
        }
    }
}

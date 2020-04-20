//
// Copyright (c) 2010-2018 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using Antmicro.Renode.Storage;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Logging;
using System.IO;
using Antmicro.Renode.Exceptions;
using static Antmicro.Renode.Utilities.BitHelper;
using Antmicro.Renode.Peripherals.SPI;

using System.Collections.Generic;

namespace Antmicro.Renode.Peripherals.SD
{
    // Features NOT supported:
    // * Toggling selected state
    // * RCA (relative card address) filtering
    // As a result any SD controller with more than one SD card attached at the same time might not work properly.
    public class SDCard : ISPIPeripheral
    {
        public SDCard(string imageFile, long? size = null, bool persistent = false, bool spiMode = false)
        {
            this.spiMode = spiMode;
            spiContext = new SpiContext();
            isIdle = true;

            dataBackend = DataStorage.Create(imageFile, size, persistent);

            var sdCapacityParameters = SDCapacity.SeekForCapacityParametes(dataBackend.Length);
            dataBackend.SetLength(sdCapacityParameters.MemoryCapacity);

            cardStatusGenerator = new VariableLengthValue(32)
                .DefineFragment(5, 1, () => (treatNextCommandAsAppCommand ? 1 : 0u), name: "APP_CMD bit")
                .DefineFragment(8, 1, 1, name: "READY_FOR_DATA bit");

            operatingConditionsGenerator = new VariableLengthValue(32)
                .DefineFragment(31, 1, 1, name: "Card power up status bit (busy)")
            ;

            cardSpecificDataGenerator = new VariableLengthValue(128)
                .DefineFragment(47, 3, (uint)sdCapacityParameters.Multiplier, name: "device size multiplier")
                .DefineFragment(62, 12, (ulong)sdCapacityParameters.DeviceSize, name: "device size")
                .DefineFragment(80, 4, (uint)sdCapacityParameters.BlockSize, name: "max read data block length")
                .DefineFragment(84, 12, (uint)CardCommandClass.Class0, name: "card command classes")
                .DefineFragment(96, 3, (uint)TransferRate.Transfer10Mbit, name: "transfer rate unit")
                .DefineFragment(99, 4, (uint)TransferMultiplier.Multiplier2_5, name: "transfer multiplier")
            ;

            cardIdentificationGenerator = new VariableLengthValue(128)
                .DefineFragment(8, 4, 8, name: "manufacturer date code - month")
                .DefineFragment(12, 8, 18, name: "manufacturer date code - year")
                .DefineFragment(64, 8, (uint)'D', name: "product name 5")
                .DefineFragment(72, 8, (uint)'O', name: "product name 4")
                .DefineFragment(80, 8, (uint)'N', name: "product name 3")
                .DefineFragment(88, 8, (uint)'E', name: "product name 2")
                .DefineFragment(96, 8, (uint)'R', name: "product name 1")
                .DefineFragment(120, 8, 0xab, name: "manufacturer ID")
            ;
        }

        public void Reset()
        {
            readContext.Reset();
            writeContext.Reset();
            treatNextCommandAsAppCommand = false;

            isIdle = true;

            spiContext.Reset();
        }

        public void Dispose()
        {
            dataBackend.Dispose();
        }

        public BitStream HandleCommand(uint commandIndex, uint arg)
        {
            BitStream result;
            this.Log(LogLevel.Debug, "Command received: 0x{0:x} with arg 0x{1:x}", commandIndex, arg);
            var treatNextCommandAsAppCommandLocal = treatNextCommandAsAppCommand;
            treatNextCommandAsAppCommand = false;
            if(!treatNextCommandAsAppCommandLocal || !TryHandleApplicationSpecificCommand((SdCardApplicationSpecificCommand)commandIndex, arg, out result))
            {
                result = HandleStandardCommand((SdCardCommand)commandIndex, arg);
            }
            this.Log(LogLevel.Debug, "Sending command response: {0}", result.ToString());
            return result;
        }

        public void WriteData(byte[] data)
        {
            if(!writeContext.IsActive || writeContext.Data != null)
            {
                this.Log(LogLevel.Warning, "Trying to write data when the SD card is not expecting it");
                return;
            }
            if(!writeContext.CanAccept((uint)data.Length))
            {
                this.Log(LogLevel.Warning, "Trying to write more data ({0} bytes) than expected ({1} bytes). Ignoring the whole transfer", data.Length, writeContext.BytesLeft);
                return;
            }
            WriteDataToUnderlyingFile(writeContext.Offset, data.Length, data);
            writeContext.Move((uint)data.Length);
        }

        // TODO: this method should be removed and it should be controller's responsibility to control the number of bytes to read
        public void SetReadLimit(uint size)
        {
            this.Log(LogLevel.Debug, "Setting read limit to: {0}", size);
            readContext.BytesLeft = size;
        }

        public byte[] ReadData(uint size)
        {
            if(!readContext.IsActive)
            {
                this.Log(LogLevel.Warning, "Trying to read data when the SD card is not expecting it");
                return new byte[0];
            }
            if(!readContext.CanAccept(size))
            {
                this.Log(LogLevel.Warning, "Trying to read more data ({0} bytes) than expected ({1} bytes). Ignoring the whole transfer", size, readContext.BytesLeft);
                return new byte[0];
            }

            this.Log(LogLevel.Noisy, "Reading {0} bytes from offset 0x{1:X}", size, readContext.Offset);

            byte[] result;
            if(readContext.Data != null)
            {
                result = readContext.Data.AsByteArray(readContext.Offset, size);
                readContext.Move(size * 8);
            }
            else
            {
                result = ReadDataFromUnderlyingFile(readContext.Offset, checked((int)size));
                readContext.Move(size);
            }
            return result;
        }

        public byte Transmit(byte data)
        {
            if(!spiMode)
            {
                this.Log(LogLevel.Error, "Received data over SPI, but the SPI mode is disabled.");
                return 0;
            }

            this.Log(LogLevel.Noisy, "SPI: Received byte 0x{0:X} in state {1}", data, spiContext.State);

            switch(spiContext.State)
            {
                case SpiState.WaitingForCommand:
                {
                    if(data == DummyByte)
                    {
                        this.Log(LogLevel.Noisy, "Received a DUMMY byte, ignoring it");
                        return GenerateR1Response().AsByte();
                    }

                    // two MSB of the SPI command byte should be '01'
                    if(BitHelper.IsBitSet(data, 7) || !BitHelper.IsBitSet(data, 6))
                    {
                        this.Log(LogLevel.Warning, "Unexpected command number value 0x{0:X}, ignoring this - expect problems", data);
                        return GenerateR1Response(illegalCommand: true).AsByte();
                    }

                    // clear COMMAND bit, we don't need it anymore
                    BitHelper.ClearBits(ref data, 6);

                    spiContext.CommandNumber = (uint)data;

                    spiContext.ArgumentBytes = 0;
                    spiContext.State = SpiState.WaitingForArgBytes;
                    break;
                }

                case SpiState.WaitingForArgBytes:
                {
                    this.Log(LogLevel.Noisy, "Storing as arg byte #{0}", spiContext.ArgumentBytes);

                    spiContext.Argument <<= 8;
                    spiContext.Argument |= data;
                    spiContext.ArgumentBytes++;

                    if(spiContext.ArgumentBytes == 4)
                    {
                        spiContext.State = SpiState.WaitingForCRC;
                    }
                    break;
                }

                case SpiState.WaitingForCRC:
                {
                    // we don't check CRC

                    this.Log(LogLevel.Noisy, "Sending a command to the SD card");
                    var result = HandleCommand(spiContext.CommandNumber, spiContext.Argument).AsByteArray();
                    spiContext.ResponseBuffer.EnqueueRange(result);

                    if(spiContext.ResponseBuffer.Count == 0)
                    {
                        this.Log(LogLevel.Warning, "Received an empty response, this is strange and might cause problems!");
                        spiContext.State = SpiState.WaitingForCommand;
                        break;
                    }
                    else
                    {
                        spiContext.State = SpiState.SendingResponse;
                        return spiContext.ResponseBuffer.Dequeue();
                    }
                }

                case SpiState.SendingResponse:
                {
                    if(spiContext.ResponseBuffer.TryDequeue(out var value))
                    {
                        return value;
                    }

                    this.Log(LogLevel.Noisy, "This is the end of response buffer");
                    spiContext.State = SpiState.WaitingForCommand;
                    break;
                }

                default:
                {
                    throw new ArgumentException($"Received data 0x{data:X} in an unexpected state {spiContext.State}");
                }
            }

            return 0;
        }

        public void FinishTransmission()
        {
            if(!spiMode)
            {
                this.Log(LogLevel.Error, "Received SPI transmission finish signal, but the SPI mode is disabled.");
                return;
            }

            this.Log(LogLevel.Noisy, "Finishing transmission");
            spiContext.Reset();
        }

        public bool IsReadyForWritingData => writeContext.IsActive;

        public bool IsReadyForReadingData => readContext.IsActive;

        public ushort CardAddress { get; set; }

        public BitStream CardStatus => cardStatusGenerator.Bits;

        public BitStream OperatingConditions => operatingConditionsGenerator.Bits;

        public BitStream SDConfiguration => new VariableLengthValue(64).Bits;

        public BitStream SDStatus => new VariableLengthValue(512).Bits;

        public BitStream CardSpecificData => cardSpecificDataGenerator.GetBits(skip: 8);

        public BitStream CardIdentification => cardIdentificationGenerator.GetBits(skip: 8);

        private void WriteDataToUnderlyingFile(long offset, int size, byte[] data)
        {
            dataBackend.Position = offset;
            var actualSize = checked((int)Math.Min(size, dataBackend.Length - dataBackend.Position));
            if(actualSize < size)
            {
                this.Log(LogLevel.Warning, "Tried to write {0} bytes of data to offset {1}, but space for only {2} is available.", size, offset, actualSize);
            }
            dataBackend.Write(data, 0, actualSize);
        }

        private byte[] ReadDataFromUnderlyingFile(long offset, int size)
        {
            dataBackend.Position = offset;
            var actualSize = checked((int)Math.Min(size, dataBackend.Length - dataBackend.Position));
            if(actualSize < size)
            {
                this.Log(LogLevel.Warning, "Tried to read {0} bytes of data from offset {1}, but only {2} is available.", size, offset, actualSize);
            }
            var result = new byte[actualSize];
            var readSoFar = 0;
            while(readSoFar < actualSize)
            {
                var readThisTime = dataBackend.Read(result, readSoFar, actualSize - readSoFar);
                if(readThisTime == 0)
                {
                    // this should not happen as we calculated the available data size
                    throw new ArgumentException("Unexpected end of data in file stream");
                }
                readSoFar += readThisTime;
            }

            return result;
        }

        private bool spiMode;
        private bool isIdle;

        private BitStream GenerateR1Response(bool illegalCommand = false)
        {
            return new BitStream()
                .AppendBit(isIdle)
                .AppendBit(false) // Erase Reset
                .AppendBit(illegalCommand)
                .AppendBit(false) // Com CRC Error
                .AppendBit(false) // Erase Seq Error
                .AppendBit(false) // Address Error
                .AppendBit(false) // Parameter Error
                .AppendBit(false); // always 0
        }

        private BitStream GenerateR2Response()
        {
            return new BitStream()
                .Append(GenerateR1Response().AsByte())
                .Append((byte)0);
        }

        private BitStream GenerateR3Response()
        {
            return new BitStream()
                .Append(GenerateR1Response().AsByte())
                .Append((byte)0) // 4 bytes of the OCR register
                .Append((byte)0)
                .Append((byte)0)
                .Append((byte)0);
        }

        private BitStream GenerateR7Response()
        {
            return new BitStream()
                .Append(GenerateR1Response().AsByte())
                .Append((byte)0) // see: http://www.rjhcoding.com/avrc-sd-interface-2.php for reference
                .Append((byte)0)
                .Append((byte)0)
                .Append((byte)0);
        }

        private BitStream HandleStandardCommand(SdCardCommand command, uint arg)
        {
            this.Log(LogLevel.Debug, "Handling as a standard command: {0}", command);
            switch(command)
            {
                case SdCardCommand.GoIdleState_CMD0:
                    Reset();
                    return spiMode 
                        ? GenerateR1Response()
                        : BitStream.Empty; // no response in SD mode

                case SdCardCommand.SendCardIdentification_CMD2:
                {
                    if(spiMode)
                    {
                        // this command is not supported in SPI mode
                        break;
                    }

                    return CardIdentification;
                }

                case SdCardCommand.SendRelativeAddress_CMD3:
                {
                    if(spiMode)
                    {
                        // this command is not supported in SPI mode
                        break;
                    }

                    var status = CardStatus.AsUInt32();
                    return BitHelper.BitConcatenator.New()
                        .StackAbove(status, 13, 0)
                        .StackAbove(status, 1, 19)
                        .StackAbove(status, 2, 22)
                        .StackAbove(CardAddress, 16, 0)
                        .Bits;
                }

                case SdCardCommand.SelectDeselectCard_CMD7:
                {
                    if(spiMode)
                    {
                        // this command is not supported in SPI mode
                        break;
                    }

                    return CardStatus;
                }

                case SdCardCommand.SendInterfaceConditionCommand_CMD8:
                    return spiMode
                        ? GenerateR7Response()
                        : CardStatus;

                case SdCardCommand.SendCardSpecificData_CMD9:
                    return spiMode
                        ? GenerateR1Response()
                        : CardSpecificData;

                case SdCardCommand.StopTransmission_CMD12:
                    readContext.Reset();
                    writeContext.Reset();
                    return spiMode
                        ? GenerateR1Response()
                        : CardStatus;

                case SdCardCommand.SendStatus_CMD13:
                    return spiMode
                        ? GenerateR2Response()
                        : CardStatus;

                case SdCardCommand.SetBlockLength_CMD16:
                    blockLengthInBytes = arg;
                    return spiMode
                        ? GenerateR1Response()
                        : CardStatus;

                case SdCardCommand.ReadSingleBlock_CMD17:
                    readContext.Offset = arg * blockLengthInBytes;
                    readContext.BytesLeft = blockLengthInBytes;
                    return spiMode
                        ? GenerateR1Response()
                            .Append(BlockBeginIndicator)
                            .Append(ReadData(blockLengthInBytes)) // the actual data
                        : CardStatus;

                case SdCardCommand.ReadMultipleBlocks_CMD18:
                    if(spiMode)
                    {
                        this.Log(LogLevel.Warning, "Reading Multiple Blocks is currently not supported in the SPI mode by this model");
                        // TODO: implement it
                        break;
                    }

                    readContext.Offset = arg * blockLengthInBytes;
                    return CardStatus;

                case SdCardCommand.WriteSingleBlock_CMD24:
                    if(spiMode)
                    {
                        this.Log(LogLevel.Warning, "Writing is currently not supported in the SPI mode by this model");
                        // TODO: implement it
                        break;
                    }

                    writeContext.Offset = arg * blockLengthInBytes;
                    writeContext.BytesLeft = blockLengthInBytes;
                    return CardStatus;

                case SdCardCommand.AppCommand_CMD55:
                    treatNextCommandAsAppCommand = true;
                    return spiMode
                        ? GenerateR1Response()
                        : CardStatus;

                case SdCardCommand.ReadOperationConditionRegister_CMD58:
                    return spiMode
                        ? GenerateR3Response()
                        : BitStream.Empty;
            }

            this.Log(LogLevel.Warning, "Unsupported command: {0}. Ignoring it", command);
            return spiMode
                ? GenerateR1Response(illegalCommand: true)
                : BitStream.Empty;
        }

        private bool TryHandleApplicationSpecificCommand(SdCardApplicationSpecificCommand command, uint arg, out BitStream result)
        {
            this.Log(LogLevel.Debug, "Handling as an application specific command: {0}", command);
            switch(command)
            {
                case SdCardApplicationSpecificCommand.SendSDCardStatus_ACMD13:
                    readContext.Data = SDStatus;
                    result = spiMode
                        ? GenerateR2Response()
                        : CardStatus;
                    return true;

                case SdCardApplicationSpecificCommand.SendOperatingConditionRegister_ACMD41:
                    // activate the card
                    isIdle = false;

                    result = spiMode
                        ? GenerateR1Response()
                        : OperatingConditions;
                    return true;

                case SdCardApplicationSpecificCommand.SendSDConfigurationRegister_ACMD51:
                    readContext.Data = SDConfiguration;
                    result = spiMode
                        ? GenerateR1Response()
                        : CardStatus;
                    return true;

                default:
                    this.Log(LogLevel.Debug, "Command #{0} seems not to be any application specific command", command);
                    result = null;
                    return false;
            }
        }

        private bool treatNextCommandAsAppCommand;
        private uint blockLengthInBytes;
        private IoContext writeContext;
        private IoContext readContext;
        private readonly Stream dataBackend;
        private readonly VariableLengthValue cardStatusGenerator;
        private readonly VariableLengthValue operatingConditionsGenerator;
        private readonly VariableLengthValue cardSpecificDataGenerator;
        private readonly VariableLengthValue cardIdentificationGenerator;

        private readonly SpiContext spiContext;
        private const byte DummyByte = 0xFF;
        private const byte BlockBeginIndicator = 0xFE;

        private struct IoContext
        {
            public uint Offset
            {
                get { return offset; }
                set
                {
                    offset = value;
                    data = null;
                }
            }

            public BitStream Data
            {
                get { return data; }
                set
                {
                    data = value;
                    offset = 0;
                }
            }

            public uint BytesLeft
            {
                get
                {
                    if(data != null)
                    {
                        return (data.Length - offset) / 8;
                    }

                    return bytesLeft;
                }

                set
                {
                    if(data != null && BytesLeft > 0)
                    {
                        throw new ArgumentException("Setting bytes left in data mode is not supported");
                    }

                    bytesLeft = value;
                }
            }

            public bool IsActive => BytesLeft > 0;

            public bool CanAccept(uint size)
            {
                return BytesLeft >= size;
            }

            public void Move(uint offset)
            {
                this.offset += offset;
                if(data == null)
                {
                    bytesLeft -= offset;
                }
            }

            public void Reset()
            {
                offset = 0;
                bytesLeft = 0;
                data = null;
            }

            private uint bytesLeft;
            private uint offset;
            private BitStream data;
        }

        private enum SdCardCommand
        {
            GoIdleState_CMD0 = 0,
            SendCardIdentification_CMD2 = 2,
            SendRelativeAddress_CMD3 = 3,
            SelectDeselectCard_CMD7 = 7,
            // this command has been added in spec version 2.0 - we don't have to answer to it
            SendInterfaceConditionCommand_CMD8 = 8,
            SendCardSpecificData_CMD9 = 9,
            StopTransmission_CMD12 = 12,
            SendStatus_CMD13 = 13,
            SetBlockLength_CMD16 = 16,
            ReadSingleBlock_CMD17 = 17,
            ReadMultipleBlocks_CMD18 = 18,
            WriteSingleBlock_CMD24 = 24,
            AppCommand_CMD55 = 55,
            ReadOperationConditionRegister_CMD58 = 58
        }

        private enum SdCardApplicationSpecificCommand
        {
            SendSDCardStatus_ACMD13 = 13,
            SendOperatingConditionRegister_ACMD41 = 41,
            SendSDConfigurationRegister_ACMD51 = 51
        }

        [Flags]
        private enum CardCommandClass
        {
            Class0 = (1 << 0),
            Class1 = (1 << 1),
            Class2 = (1 << 2),
            Class3 = (1 << 3),
            Class4 = (1 << 4),
            Class5 = (1 << 5),
            Class6 = (1 << 6),
            Class7 = (1 << 7),
            Class8 = (1 << 8),
            Class9 = (1 << 9),
            Class10 = (1 << 10),
            Class11 = (1 << 11)
        }

        private enum TransferRate
        {
            Transfer100kbit = 0,
            Transfer1Mbit = 1,
            Transfer10Mbit = 2,
            Transfer100Mbit = 3,
            // the rest is reserved
        }

        private enum TransferMultiplier
        {
            Reserved = 0,
            Multiplier1 = 1,
            Multiplier1_2 = 2,
            Multiplier1_3 = 3,
            Multiplier1_5 = 4,
            Multiplier2 = 5,
            Multiplier2_5 = 6,
            Multiplier3 = 7,
            Multiplier3_5 = 8,
            Multiplier4 = 9,
            Multiplier4_5 = 10,
            Multiplier5 = 11,
            Multiplier5_5 = 12,
            Multiplier6 = 13,
            Multiplier7 = 14,
            Multiplier8 = 15
        }

        private enum SpiState
        {
            WaitingForCommand,
            WaitingForArgBytes,
            WaitingForCRC,
            SendingResponse
        }

        private class SpiContext
        {
            public void Reset()
            {
                ResponseBuffer.Clear();
                ArgumentBytes = 0;
                Argument = 0;
                CommandNumber = 0;
                State = SpiState.WaitingForCommand;
            }

            public Queue<byte> ResponseBuffer = new Queue<byte>();
            public int ArgumentBytes;
            public uint Argument;
            public uint CommandNumber;
            public SpiState State;
        }
    }
}

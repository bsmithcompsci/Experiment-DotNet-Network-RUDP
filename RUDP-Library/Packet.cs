using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using UnityEngine;
using UnityEngine.Assertions;

namespace NetworkLibrary
{
    [Flags]
    public enum PacketFlags : byte
    {
        None = 0,
        Reliable = 1 << 0,
        Acknowledge = 1 << 1,
        Bulk = 1 << 2,
    }

    public class PacketBulk : IDisposable
    {
        private List<Packet> packets = new List<Packet>();
        public IPEndPoint remoteEndpoint;

        /// <summary>
        /// Sets up Packet Bulk ready for Receiving.
        /// </summary>
        public PacketBulk(IPEndPoint _endpoint = null) 
        {
            remoteEndpoint = _endpoint;
        }
        /// <summary>
        /// Sets up packet bulk for reading what was recieved.
        /// </summary>
        /// <param name="_bytes"></param>
        /// <param name="_remoteEndpoint"></param>
        public PacketBulk(byte[] _bytes, IPEndPoint _remoteEndpoint = null)
        {
            remoteEndpoint = _remoteEndpoint;

            int totalSize = BitConverter.ToInt32(_bytes, 0);
            PacketFlags flags = (PacketFlags)_bytes[sizeof(int)];
            int readPtr = sizeof(int) + sizeof(byte);
            while(readPtr < totalSize)
            {
                // Header
                int packetSize = BitConverter.ToInt32(_bytes, readPtr);
                readPtr += sizeof(int);

                // Body
                byte[] segment = new byte[packetSize];
                Array.Copy(_bytes, readPtr, segment, 0, packetSize);

                Packet packet = new Packet(segment, _remoteEndpoint, true);
                packet.flags = flags;
                Assert.AreEqual(packet.Size(), packetSize, $"Packet Size is not the same length in bytes: {packet.Size()}/{packetSize}");
                packets.Add(packet);
                readPtr += packetSize - sizeof(uint);
            }

            Assert.AreEqual(readPtr, totalSize, $"Packet Received was a partial, unsupported feature! {readPtr}/{totalSize}");
        }

        public void AddPacket(Packet _packet)
        {
            packets.Add(_packet);
        }

        public Packet[] GetPackets()
        {
            return packets.ToArray();
        }

        public int Size()
        {
            int sum = 0;
            foreach (Packet _packet in packets)
                sum += _packet.Size();
            return sum;
        }

        public void Dispose()
        {
            packets = null;

            GC.SuppressFinalize(this);
        }
    }

    public class Packet : IDisposable
    {
        public bool Verbose { get; set; } = false;
        public uint hashedEventName { get; private set; }
        public PacketFlags flags { get; internal set; }
        public readonly IPEndPoint remoteEndPoint;

        private List<byte> buffer;

        private byte[] readableBuffer;
        private int readPtr;
        public uint? ACK { get; private set; }

        public Packet(string _eventName, IPEndPoint _endpoint, PacketFlags _flags = PacketFlags.None) : this(Crc32.hash(_eventName), _endpoint, _flags) { }
        public Packet(uint _eventCRC32, IPEndPoint _endpoint, PacketFlags _flags = PacketFlags.None)
        {
            VerboseLog($"Packet Construction [{Crc32.GetCRCToString(_eventCRC32)}] [Sending Action :: {_flags}]");
            buffer = new List<byte>();

            hashedEventName = _eventCRC32;
            remoteEndPoint = _endpoint;
            flags = _flags;
        }
        public Packet(PacketBulk _bulk, PacketFlags _flags = PacketFlags.Bulk)
        {
            VerboseLog($"Packet Bulk Construction [Sending Action :: {_flags}]");
            buffer = new List<byte>();

            remoteEndPoint = _bulk.remoteEndpoint;
            flags = _flags;

            foreach (Packet packet in _bulk.GetPackets())
            {
                Assert.AreEqual(packet.remoteEndPoint.ToString(), remoteEndPoint.ToString(), "Packet bulk has a packet that shouldn't be in here.");
                packet.flags = _flags;

                // Each Segment will have a size; and their body buffer.
                Write(sizeof(int) + sizeof(uint) + packet.Size());
                Write(packet.hashedEventName);
                VerboseLog("\t---");
                Write(packet.GetBuffer());
                VerboseLog($"\t### {packet.Size()} bytes");
            }
        }
        public Packet(byte[] _bytes, IPEndPoint _endpoint, bool _bulkedSegment = false)
        {
            VerboseLog("Packet Construction [Recv Action]");
            buffer = new List<byte>();

            readableBuffer = _bytes;
            remoteEndPoint = _endpoint;

            Write(_bytes);
            var size = ReadInt();                  //      : 4 byte int32
            Assert.AreEqual(_bytes.Length, size, "Packet expected size is not equal.");
            InitPacketFormat(_bulkedSegment);
        }
        public Packet(byte[] _bytes, ref int _size, IPEndPoint _endpoint)
        {
            VerboseLog("Packet Construction [Recv Action]");
            buffer = new List<byte>();

            readableBuffer = _bytes;
            remoteEndPoint = _endpoint;

            Write(_bytes);

            _size = ReadInt();                  //      : 4 byte int32
            InitPacketFormat();
        }

        private void VerboseLog(string _msg)
        {
            if (!Verbose)
                return;
            Debug.Log(_msg);
        }

        private void InitPacketFormat(bool _bulkedSegment = false)
        {
            if (_bulkedSegment)
            {
                hashedEventName = ReadUInt();       //      : 4 byte uint32
            }
            else
            {
                flags = (PacketFlags)ReadByte();    //      : 1 byte byte

                if ((flags & PacketFlags.Bulk) == 0)
                {
                    hashedEventName = ReadUInt();       //      : 4 byte uint32
                }

                if ((flags & (PacketFlags.Reliable | PacketFlags.Acknowledge)) != 0)
                {
                    ACK = ReadUInt();               //      : 4 byte uint32
                }
            }
        }
        internal void WriteEventCRC()
        {
            Assert.IsNotNull(buffer);

            VerboseLog($"\tInsert EventCRC = {hashedEventName} [{Crc32.GetCRCToString(hashedEventName)}]");
            buffer.InsertRange(0, BitConverter.GetBytes(hashedEventName));
        }
        internal void WriteFlags()
        {
            Assert.IsNotNull(buffer);

            VerboseLog($"\tInsert Flags = {flags} [{(byte)flags}]");
            buffer.Insert(0, (byte)flags);
        }
        internal void WriteACK(uint lastACK)
        {
            Assert.IsNotNull(buffer);

            ACK = lastACK + (uint)buffer.Count;
            
            VerboseLog($"\tInsert ACK = {ACK}");

            buffer.InsertRange(0, BitConverter.GetBytes(ACK.Value));
        }
        private void WriteLength()
        {
            VerboseLog($"\tInsert Length = {buffer.Count}");
            Assert.IsNotNull(buffer);

            buffer.InsertRange(0, BitConverter.GetBytes(buffer.Count));
        }

        public void Write(byte _data)
        {
            Assert.IsNotNull(buffer);

            VerboseLog($"\tWrite byte = {_data}");
            buffer.Add(_data);
        }
        public void Write(byte[] _data)
        {
            Assert.IsNotNull(buffer);

            VerboseLog($"\t\tWrite byte[{_data.Length}] = [{String.Join(",", _data)}]");
            buffer.AddRange(_data);
        }
        public void Write(short _data)
        {
            VerboseLog($"\tWrite short = {_data}");
            Write(BitConverter.GetBytes(_data));
        }
        public void Write(ushort _data)
        {
            VerboseLog($"\tWrite ushort = {_data}");
            Write(BitConverter.GetBytes(_data));
        }
        public void Write(int _data)
        {
            VerboseLog($"\tWrite int32 = {_data}");
            Write(BitConverter.GetBytes(_data));
        }
        public void Write(uint _data)
        {
            VerboseLog($"\tWrite uint32 = {_data}");
            Write(BitConverter.GetBytes(_data));
        }
        public void Write(long _data)
        {
            VerboseLog($"\tWrite int64 = {_data}");
            Write(BitConverter.GetBytes(_data));
        }
        public void Write(ulong _data)
        {
            VerboseLog($"\tWrite uint64 = {_data}");
            Write(BitConverter.GetBytes(_data));
        }
        public void Write(float _data)
        {
            VerboseLog($"\tWrite float = {_data}");
            Write(BitConverter.GetBytes(_data));
        }
        public void Write(string _data)
        {
            Write(_data.Length);
            VerboseLog($"\tWrite char[{_data.Length}] = {_data}");
            Write(Encoding.ASCII.GetBytes(_data));
        }
        public void Write(bool _data)
        {
            VerboseLog($"\tWrite bool = {_data}");
            Write(BitConverter.GetBytes(_data));
        }

        public void Clear()
        {
            Dispose();

            buffer = new List<byte>();
        }

        public int GetBytesLeft()
        {
            return readableBuffer.Length - readPtr;
        }

        public byte ReadByte(bool _movePtr = true)
        {
            if (readPtr < readableBuffer.Length)
            {
                byte result = buffer[readPtr];
                if (_movePtr)
                    readPtr += sizeof(byte);
                VerboseLog($"\tRead {sizeof(byte)} bytes = {result}");
                return result;
            }

            throw new Exception("Could not read value of type 'byte'!");
        }
        public byte[] ReadBytes(int _length, bool _movePtr = true)
        {
            if (_length == 0)
                return null;

            if (readPtr < readableBuffer.Length)
            {
                byte[] bytes = buffer.GetRange(readPtr, _length).ToArray();
                if (_movePtr)
                    readPtr += _length;
                VerboseLog($"\tRead {_length} bytes  = [{String.Join(",", bytes)}]");
                return bytes;
            }

            throw new Exception("Could not read value of type 'byte[]'!");
        }
        public short ReadShort(bool _movePtr = true)
        {
            if (readPtr < readableBuffer.Length)
            {
                short result = BitConverter.ToInt16(readableBuffer, readPtr);
                if (_movePtr)
                    readPtr += sizeof(short);
                VerboseLog($"\tRead {sizeof(short)} bytes = {result}");
                return result;
            }

            throw new Exception("Could not read value of type 'short'!");
        }
        public ushort ReadUShort(bool _movePtr = true)
        {
            if (readPtr < readableBuffer.Length)
            {
                ushort result = BitConverter.ToUInt16(readableBuffer, readPtr);
                if (_movePtr)
                    readPtr += sizeof(ushort);
                VerboseLog($"\tRead {sizeof(ushort)} bytes = {result}");
                return result;
            }

            throw new Exception("Could not read value of type 'ushort'!");
        }
        public int ReadInt(bool _movePtr = true)
        {
            if (readPtr < readableBuffer.Length)
            {
                int result = BitConverter.ToInt32(readableBuffer, readPtr);
                if (_movePtr)
                    readPtr += sizeof(int);
                VerboseLog($"\tRead {sizeof(int)} bytes = {result}");
                return result;
            }

            throw new Exception("Could not read value of type 'int32'!");
        }
        public uint ReadUInt(bool _movePtr = true)
        {

            if (readPtr < readableBuffer.Length)
            {
                uint result = BitConverter.ToUInt32(readableBuffer, readPtr);
                if (_movePtr)
                    readPtr += sizeof(uint);
                VerboseLog($"\tRead {sizeof(uint)} bytes = {result}");
                return result;
            }

            throw new Exception("Could not read value of type 'uint32'!");
        }
        public long ReadLong(bool _movePtr = true)
        {

            if (readPtr < readableBuffer.Length)
            {
                long result = BitConverter.ToInt64(readableBuffer, readPtr);
                if (_movePtr)
                    readPtr += sizeof(long);
                VerboseLog($"\tRead {sizeof(long)} bytes = {result}");
                return result;
            }

            throw new Exception("Could not read value of type 'long'!");
        }
        public ulong ReadULong(bool _movePtr = true)
        {
            if (readPtr < readableBuffer.Length)
            {
                ulong result = BitConverter.ToUInt64(readableBuffer, readPtr);
                if (_movePtr)
                    readPtr += sizeof(ulong);
                VerboseLog($"\tRead {sizeof(ulong)} bytes = {result}");
                return result;
            }

            throw new Exception("Could not read value of type 'ulong'!");
        }
        public float ReadFloat(bool _movePtr = true)
        {
            if (readPtr < readableBuffer.Length)
            {
                float result = BitConverter.ToSingle(readableBuffer, readPtr);
                if (_movePtr)
                    readPtr += sizeof(float);
                VerboseLog($"\tRead {sizeof(float)} bytes = {result}");
                return result;
            }

            throw new Exception("Could not read value of type 'float'!");
        }
        public string ReadString(bool _movePtr = true)
        {
            try
            {
                int length = ReadInt();
                string result = Encoding.ASCII.GetString(readableBuffer, readPtr, length);
                Assert.AreEqual(length, result.Length, "Mismatch String length from packet bytes.");
                if (_movePtr && result.Length > 0)
                    readPtr += result.Length;
                VerboseLog($"\tRead {result.Length} bytes = {result}");
                return result;
            }
            catch
            {
                throw new Exception("Could not read value of type 'string'!");
            }
        }
        public bool ReadBoolean(bool _movePtr = true)
        {
            if (readPtr < readableBuffer.Length)
            {
                bool result = BitConverter.ToBoolean(readableBuffer, readPtr);
                if (_movePtr)
                    readPtr += sizeof(bool);
                VerboseLog($"\tRead {sizeof(bool)} bytes = {result}");
                return result;
            }

            throw new Exception("Could not read value of type 'boolean'!");
        }

        /// <summary>
        /// Get the Size without affecting the buffer.
        /// </summary>
        /// <returns></returns>
        public int Size()
        {
            return buffer.Count;
        }

        /// <summary>
        /// Get Raw Buffer.
        /// </summary>
        /// <returns></returns>
        public byte[] GetBuffer()
        {
            return buffer?.ToArray() ?? null;
        }

        /// <summary>
        /// Packs the buffer ready for shipment over the network.
        /// 
        /// Warning: Call this only once!
        /// </summary>
        /// <param name="lastACK"></param>
        /// <returns></returns>
        public byte[] Pack(uint lastACK = 0)
        {
            // Packed Architecture:
            // flags    : 1-byte (byte)
            // size     : 4-bytes (uint32)
            // event    : 4-byte (uint32)
            // --
            // ACK      : 4-byte (uint32)
            // --
            // Body!

            VerboseLog($"\t---");
            if ((flags & (PacketFlags.Reliable | PacketFlags.Acknowledge)) != 0)
            {
                WriteACK(lastACK);
                VerboseLog($"\t---");
            }
            if ((flags & PacketFlags.Bulk) == 0)
            {
                WriteEventCRC();
            }
            WriteFlags();
            WriteLength();
            byte[] bytes = buffer.ToArray();
            VerboseLog($"\tPacked {bytes.Length} bytes");
            return bytes;
        }

        public void Dispose()
        {
            buffer = null;
            readableBuffer = null;
            readPtr = 0;

            GC.SuppressFinalize(this);
        }
    }
}
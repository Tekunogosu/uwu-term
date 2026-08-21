using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UwUTerm.Nvim
{
    /// <summary>
    /// The slice of MessagePack that msgpack-rpc uses.
    ///
    /// Neovim's embedded UI protocol is msgpack-rpc over the child's stdin and stdout, so
    /// speaking to it means speaking this. The format is small enough to implement outright -
    /// a tag byte, then a length or a value - and doing so keeps a dependency out of a plugin
    /// that has to load inside someone else's game.
    ///
    /// Decoded values are plain CLR types: null, bool, long, double, string, byte[], object[]
    /// and Dictionary. Neovim also sends ext-typed handles for buffers and windows, which are
    /// kept as <see cref="Handle"/> so they can be handed straight back.
    /// </summary>
    internal static class MsgPack
    {
        /// <summary>A buffer, window or tabpage as neovim refers to it. Opaque on purpose -
        /// its only job is to survive a round trip.</summary>
        internal struct Handle
        {
            internal byte Type;
            internal byte[] Data;
        }

        // ---- writing ---------------------------------------------------------------------

        internal static void Write(Stream to, object value)
        {
            switch (value)
            {
                case null: to.WriteByte(0xC0); return;
                case bool b: to.WriteByte((byte)(b ? 0xC3 : 0xC2)); return;
                case string s: WriteString(to, s); return;
                case byte[] raw: WriteBinary(to, raw); return;
                case int i: WriteInteger(to, i); return;
                case long l: WriteInteger(to, l); return;
                case double d: WriteDouble(to, d); return;
                case Handle h: WriteHandle(to, h); return;
                case object[] array: WriteArray(to, array); return;
                case IDictionary<string, object> map: WriteMap(to, map); return;
            }

            throw new NotSupportedException("msgpack: cannot write " + value.GetType().Name);
        }

        private static void WriteInteger(Stream to, long value)
        {
            if (value >= 0)
            {
                if (value < 128) { to.WriteByte((byte)value); return; }
                if (value <= byte.MaxValue) { to.WriteByte(0xCC); to.WriteByte((byte)value); return; }
                if (value <= ushort.MaxValue) { to.WriteByte(0xCD); WriteBig(to, (ulong)value, 2); return; }
                if (value <= uint.MaxValue) { to.WriteByte(0xCE); WriteBig(to, (ulong)value, 4); return; }

                to.WriteByte(0xCF); WriteBig(to, (ulong)value, 8);
                return;
            }

            if (value >= -32) { to.WriteByte((byte)(0xE0 | (value + 32))); return; }
            if (value >= sbyte.MinValue) { to.WriteByte(0xD0); to.WriteByte((byte)(sbyte)value); return; }
            if (value >= short.MinValue) { to.WriteByte(0xD1); WriteBig(to, (ulong)(ushort)(short)value, 2); return; }
            if (value >= int.MinValue) { to.WriteByte(0xD2); WriteBig(to, (uint)(int)value, 4); return; }

            to.WriteByte(0xD3); WriteBig(to, (ulong)value, 8);
        }

        private static void WriteDouble(Stream to, double value)
        {
            to.WriteByte(0xCB);
            WriteBig(to, (ulong)BitConverter.DoubleToInt64Bits(value), 8);
        }

        private static void WriteString(Stream to, string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);

            if (utf8.Length < 32) to.WriteByte((byte)(0xA0 | utf8.Length));
            else if (utf8.Length <= byte.MaxValue) { to.WriteByte(0xD9); to.WriteByte((byte)utf8.Length); }
            else if (utf8.Length <= ushort.MaxValue) { to.WriteByte(0xDA); WriteBig(to, (ulong)utf8.Length, 2); }
            else { to.WriteByte(0xDB); WriteBig(to, (ulong)utf8.Length, 4); }

            to.Write(utf8, 0, utf8.Length);
        }

        private static void WriteBinary(Stream to, byte[] value)
        {
            if (value.Length <= byte.MaxValue) { to.WriteByte(0xC4); to.WriteByte((byte)value.Length); }
            else if (value.Length <= ushort.MaxValue) { to.WriteByte(0xC5); WriteBig(to, (ulong)value.Length, 2); }
            else { to.WriteByte(0xC6); WriteBig(to, (ulong)value.Length, 4); }

            to.Write(value, 0, value.Length);
        }

        private static void WriteArray(Stream to, object[] value)
        {
            if (value.Length < 16) to.WriteByte((byte)(0x90 | value.Length));
            else if (value.Length <= ushort.MaxValue) { to.WriteByte(0xDC); WriteBig(to, (ulong)value.Length, 2); }
            else { to.WriteByte(0xDD); WriteBig(to, (ulong)value.Length, 4); }

            foreach (object item in value) Write(to, item);
        }

        private static void WriteMap(Stream to, IDictionary<string, object> value)
        {
            if (value.Count < 16) to.WriteByte((byte)(0x80 | value.Count));
            else if (value.Count <= ushort.MaxValue) { to.WriteByte(0xDE); WriteBig(to, (ulong)value.Count, 2); }
            else { to.WriteByte(0xDF); WriteBig(to, (ulong)value.Count, 4); }

            foreach (KeyValuePair<string, object> pair in value)
            {
                WriteString(to, pair.Key);
                Write(to, pair.Value);
            }
        }

        private static void WriteHandle(Stream to, Handle value)
        {
            int length = value.Data.Length;
            switch (length)
            {
                case 1: to.WriteByte(0xD4); break;
                case 2: to.WriteByte(0xD5); break;
                case 4: to.WriteByte(0xD6); break;
                case 8: to.WriteByte(0xD7); break;
                case 16: to.WriteByte(0xD8); break;
                default:
                    if (length <= byte.MaxValue) { to.WriteByte(0xC7); to.WriteByte((byte)length); }
                    else if (length <= ushort.MaxValue) { to.WriteByte(0xC8); WriteBig(to, (ulong)length, 2); }
                    else { to.WriteByte(0xC9); WriteBig(to, (ulong)length, 4); }
                    break;
            }

            to.WriteByte(value.Type);
            to.Write(value.Data, 0, length);
        }

        /// <summary>MessagePack is big-endian throughout, whatever the machine is.</summary>
        private static void WriteBig(Stream to, ulong value, int bytes)
        {
            for (int shift = (bytes - 1) * 8; shift >= 0; shift -= 8)
                to.WriteByte((byte)(value >> shift));
        }

        // ---- reading ---------------------------------------------------------------------

        /// <summary>
        /// Read one value. Returns false when the buffer holds only part of it, leaving the
        /// position untouched - a stream delivers whatever has arrived, not whole messages, so
        /// the caller waits for more and tries the same bytes again.
        /// </summary>
        internal static bool TryRead(byte[] buffer, int end, ref int at, out object value)
        {
            int start = at;
            if (!Read(buffer, end, ref at, out value)) { at = start; return false; }
            return true;
        }

        private static bool Read(byte[] buffer, int end, ref int at, out object value)
        {
            value = null;
            if (at >= end) return false;

            byte tag = buffer[at++];

            if (tag <= 0x7F) { value = (long)tag; return true; }
            if (tag >= 0xE0) { value = (long)(sbyte)tag; return true; }
            if ((tag & 0xE0) == 0xA0) return ReadString(buffer, end, ref at, tag & 0x1F, out value);
            if ((tag & 0xF0) == 0x90) return ReadArray(buffer, end, ref at, tag & 0x0F, out value);
            if ((tag & 0xF0) == 0x80) return ReadMap(buffer, end, ref at, tag & 0x0F, out value);

            switch (tag)
            {
                case 0xC0: value = null; return true;
                case 0xC2: value = false; return true;
                case 0xC3: value = true; return true;

                case 0xCC: return ReadUnsigned(buffer, end, ref at, 1, out value);
                case 0xCD: return ReadUnsigned(buffer, end, ref at, 2, out value);
                case 0xCE: return ReadUnsigned(buffer, end, ref at, 4, out value);
                case 0xCF: return ReadUnsigned(buffer, end, ref at, 8, out value);

                case 0xD0: return ReadSigned(buffer, end, ref at, 1, out value);
                case 0xD1: return ReadSigned(buffer, end, ref at, 2, out value);
                case 0xD2: return ReadSigned(buffer, end, ref at, 4, out value);
                case 0xD3: return ReadSigned(buffer, end, ref at, 8, out value);

                case 0xCA: return ReadFloat(buffer, end, ref at, out value);
                case 0xCB: return ReadDouble(buffer, end, ref at, out value);

                case 0xD9: return ReadCounted(buffer, end, ref at, 1, ReadString, out value);
                case 0xDA: return ReadCounted(buffer, end, ref at, 2, ReadString, out value);
                case 0xDB: return ReadCounted(buffer, end, ref at, 4, ReadString, out value);

                case 0xC4: return ReadCounted(buffer, end, ref at, 1, ReadBinary, out value);
                case 0xC5: return ReadCounted(buffer, end, ref at, 2, ReadBinary, out value);
                case 0xC6: return ReadCounted(buffer, end, ref at, 4, ReadBinary, out value);

                case 0xDC: return ReadCounted(buffer, end, ref at, 2, ReadArray, out value);
                case 0xDD: return ReadCounted(buffer, end, ref at, 4, ReadArray, out value);

                case 0xDE: return ReadCounted(buffer, end, ref at, 2, ReadMap, out value);
                case 0xDF: return ReadCounted(buffer, end, ref at, 4, ReadMap, out value);

                case 0xD4: return ReadHandle(buffer, end, ref at, 1, out value);
                case 0xD5: return ReadHandle(buffer, end, ref at, 2, out value);
                case 0xD6: return ReadHandle(buffer, end, ref at, 4, out value);
                case 0xD7: return ReadHandle(buffer, end, ref at, 8, out value);
                case 0xD8: return ReadHandle(buffer, end, ref at, 16, out value);

                case 0xC7: return ReadCountedHandle(buffer, end, ref at, 1, out value);
                case 0xC8: return ReadCountedHandle(buffer, end, ref at, 2, out value);
                case 0xC9: return ReadCountedHandle(buffer, end, ref at, 4, out value);
            }

            throw new NotSupportedException($"msgpack: unknown tag 0x{tag:X2}");
        }

        private delegate bool Counted(byte[] buffer, int end, ref int at, int count, out object value);

        private static bool ReadCounted(byte[] buffer, int end, ref int at, int width, Counted read, out object value)
        {
            value = null;
            if (!ReadUnsigned(buffer, end, ref at, width, out object count)) return false;

            return read(buffer, end, ref at, (int)(long)count, out value);
        }

        private static bool ReadCountedHandle(byte[] buffer, int end, ref int at, int width, out object value)
        {
            value = null;
            if (!ReadUnsigned(buffer, end, ref at, width, out object count)) return false;

            return ReadHandle(buffer, end, ref at, (int)(long)count, out value);
        }

        private static bool ReadUnsigned(byte[] buffer, int end, ref int at, int bytes, out object value)
        {
            value = null;
            if (at + bytes > end) return false;

            ulong result = 0;
            for (int i = 0; i < bytes; i++) result = (result << 8) | buffer[at++];

            value = (long)result;
            return true;
        }

        private static bool ReadSigned(byte[] buffer, int end, ref int at, int bytes, out object value)
        {
            value = null;
            if (at + bytes > end) return false;

            long result = (sbyte)buffer[at++];
            for (int i = 1; i < bytes; i++) result = (result << 8) | buffer[at++];

            value = result;
            return true;
        }

        private static bool ReadFloat(byte[] buffer, int end, ref int at, out object value)
        {
            value = null;
            if (at + 4 > end) return false;

            var bytes = new byte[4];
            for (int i = 0; i < 4; i++) bytes[3 - i] = buffer[at++];

            value = (double)BitConverter.ToSingle(bytes, 0);
            return true;
        }

        private static bool ReadDouble(byte[] buffer, int end, ref int at, out object value)
        {
            value = null;
            if (at + 8 > end) return false;

            var bytes = new byte[8];
            for (int i = 0; i < 8; i++) bytes[7 - i] = buffer[at++];

            value = BitConverter.ToDouble(bytes, 0);
            return true;
        }

        private static bool ReadString(byte[] buffer, int end, ref int at, int count, out object value)
        {
            value = null;
            if (at + count > end) return false;

            value = Encoding.UTF8.GetString(buffer, at, count);
            at += count;
            return true;
        }

        private static bool ReadBinary(byte[] buffer, int end, ref int at, int count, out object value)
        {
            value = null;
            if (at + count > end) return false;

            var bytes = new byte[count];
            Array.Copy(buffer, at, bytes, 0, count);
            at += count;

            value = bytes;
            return true;
        }

        private static bool ReadArray(byte[] buffer, int end, ref int at, int count, out object value)
        {
            value = null;
            var items = new object[count];

            for (int i = 0; i < count; i++)
                if (!Read(buffer, end, ref at, out items[i])) return false;

            value = items;
            return true;
        }

        private static bool ReadMap(byte[] buffer, int end, ref int at, int count, out object value)
        {
            value = null;
            var map = new Dictionary<string, object>(count);

            for (int i = 0; i < count; i++)
            {
                if (!Read(buffer, end, ref at, out object key)) return false;
                if (!Read(buffer, end, ref at, out object item)) return false;

                map[key as string ?? key?.ToString() ?? ""] = item;
            }

            value = map;
            return true;
        }

        private static bool ReadHandle(byte[] buffer, int end, ref int at, int count, out object value)
        {
            value = null;
            if (at + count + 1 > end) return false;

            var handle = new Handle { Type = buffer[at++], Data = new byte[count] };
            Array.Copy(buffer, at, handle.Data, 0, count);
            at += count;

            value = handle;
            return true;
        }
    }
}

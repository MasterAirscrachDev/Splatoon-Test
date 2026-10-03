using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// How network messages become bytes and back (SteamGlobal's wire format). Only the types listed
// in Types can be sent or received: each goes out as its one-byte tag, then its public fields in
// declaration order. Field types: bool, byte, short, ushort, int, uint, long, ulong, float, double, string, enums,
// 1-D arrays of those, and the listed classes nested (NVector2/3, NColor). A message names no type
// beyond its tag, so a received one can only ever become one of these, and anything malformed (an
// unknown tag, cut short, an absurd length, bytes left over) is rejected with an exception.
// Every client must run the same build: the layout is the code's field order.
public static class NetCodec
{
    // Tags are positions in this list: append new message types, never reorder.
    static readonly Type[] Types =
    {
        typeof(float),                // SteamGlobal internal: realtime delta
        typeof(byte),                 // SteamGlobal internal: client id
        InternalType("InternalSyncedEvent"),
        typeof(NVector2), typeof(NVector3), typeof(NColor),
        typeof(PlayerStateData), typeof(PlayerSpawnData), typeof(SplatData), typeof(SplatBatchData),
        typeof(TeleportData), typeof(ProjectileSpawnData), typeof(DamageData), typeof(SubData),
        typeof(SubDamageData), typeof(InkStrikeData), typeof(TeamAssignData), typeof(MatchEventData),
    };

    const byte NullTag = 0;               // tags start at 1
    const int MaxArrayLength = 1 << 16;
    const int MaxStringBytes = 1 << 12;

    static readonly Dictionary<Type, byte> tagOf = new Dictionary<Type, byte>();
    static readonly Dictionary<Type, FieldInfo[]> layouts = new Dictionary<Type, FieldInfo[]>();

    // Everything is worked out here, once: afterwards the tables are only read (messages are
    // encoded and decoded on the network threads as well as the main one).
    static NetCodec()
    {
        for (int i = 0; i < Types.Length; i++)
            if (Types[i] != null) tagOf[Types[i]] = (byte)(i + 1);
        foreach (Type t in Types)
            if (t != null && t.IsClass)
                layouts[t] = t.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.MetadataToken).ToArray();
    }

    // SteamGlobal's own payloads are private to it.
    static Type InternalType(string name) => typeof(SteamGlobal).GetNestedType(name, BindingFlags.NonPublic | BindingFlags.Public);

    public static bool CanSend(Type t) => t != null && tagOf.ContainsKey(t);

    public static byte[] Encode(object obj)
    {
        if (obj == null) return Array.Empty<byte>();
        if (!tagOf.TryGetValue(obj.GetType(), out byte tag))
            throw new ArgumentException($"NetCodec: {obj.GetType().FullName} isn't a network message type (add it to NetCodec.Types)");
        using var stream = new MemoryStream(64);
        using var w = new BinaryWriter(stream, Encoding.UTF8);
        w.Write(tag);
        WriteValue(w, obj.GetType(), obj, top: true);
        w.Flush();
        return stream.ToArray();
    }

    public static object Decode(byte[] data, int offset, int length)
    {
        if (length == 0) return null;
        using var stream = new MemoryStream(data, offset, length, writable: false);
        using var r = new BinaryReader(stream, Encoding.UTF8);
        byte tag = r.ReadByte();
        if (tag == NullTag || tag > Types.Length || Types[tag - 1] == null) throw new InvalidDataException($"NetCodec: unknown tag {tag}");
        object value = ReadValue(r, Types[tag - 1], top: true);
        if (stream.Position != stream.Length) throw new InvalidDataException($"NetCodec: {stream.Length - stream.Position} byte(s) left over");
        return value;
    }

    // ── writing ──
    static void WriteValue(BinaryWriter w, Type t, object v, bool top = false)
    {
        if (t == typeof(bool)) w.Write((bool)v);
        else if (t == typeof(byte)) w.Write((byte)v);
        else if (t == typeof(short)) w.Write((short)v);
        else if (t == typeof(ushort)) w.Write((ushort)v);
        else if (t == typeof(int)) w.Write((int)v);
        else if (t == typeof(uint)) w.Write((uint)v);
        else if (t == typeof(long)) w.Write((long)v);
        else if (t == typeof(ulong)) w.Write((ulong)v);
        else if (t == typeof(float)) w.Write((float)v);
        else if (t == typeof(double)) w.Write((double)v);
        else if (t.IsEnum) w.Write(Convert.ToInt32(v));
        else if (t == typeof(string))
        {
            if (v == null) { w.Write(-1); return; }
            byte[] bytes = Encoding.UTF8.GetBytes((string)v);
            if (bytes.Length > MaxStringBytes) throw new ArgumentException("NetCodec: string too long");
            w.Write(bytes.Length);
            w.Write(bytes);
        }
        else if (t.IsArray)
        {
            if (v == null) { w.Write(-1); return; }
            var a = (Array)v;
            if (a.Length > MaxArrayLength) throw new ArgumentException("NetCodec: array too long");
            w.Write(a.Length);
            Type e = t.GetElementType();
            for (int i = 0; i < a.Length; i++) WriteValue(w, e, a.GetValue(i));
        }
        else if (tagOf.ContainsKey(t))
        {
            if (!top) { w.Write(v != null); if (v == null) return; }
            foreach (FieldInfo f in Layout(t)) WriteValue(w, f.FieldType, f.GetValue(v));
        }
        else throw new ArgumentException($"NetCodec: can't send a {t.FullName}");
    }

    // ── reading ──
    static object ReadValue(BinaryReader r, Type t, bool top = false)
    {
        if (t == typeof(bool)) return r.ReadBoolean();
        if (t == typeof(byte)) return r.ReadByte();
        if (t == typeof(short)) return r.ReadInt16();
        if (t == typeof(ushort)) return r.ReadUInt16();
        if (t == typeof(int)) return r.ReadInt32();
        if (t == typeof(uint)) return r.ReadUInt32();
        if (t == typeof(long)) return r.ReadInt64();
        if (t == typeof(ulong)) return r.ReadUInt64();
        if (t == typeof(float)) return r.ReadSingle();
        if (t == typeof(double)) return r.ReadDouble();
        if (t.IsEnum) return Enum.ToObject(t, r.ReadInt32());
        if (t == typeof(string))
        {
            int n = ReadLength(r, MaxStringBytes, 1);
            return n < 0 ? null : Encoding.UTF8.GetString(r.ReadBytes(n));
        }
        if (t.IsArray)
        {
            Type e = t.GetElementType();
            int n = ReadLength(r, MaxArrayLength, MinSize(e));
            if (n < 0) return null;
            Array a = Array.CreateInstance(e, n);
            for (int i = 0; i < n; i++) a.SetValue(ReadValue(r, e), i);
            return a;
        }
        if (tagOf.ContainsKey(t))
        {
            if (!top && !r.ReadBoolean()) return null;
            object o = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
            foreach (FieldInfo f in Layout(t)) f.SetValue(o, ReadValue(r, f.FieldType));
            return o;
        }
        throw new InvalidDataException($"NetCodec: can't read a {t.FullName}");
    }

    // A length that must fit both the cap and what's actually left in the message.
    static int ReadLength(BinaryReader r, int max, int minElementSize)
    {
        int n = r.ReadInt32();
        if (n < -1 || n > max) throw new InvalidDataException($"NetCodec: bad length {n}");
        long left = r.BaseStream.Length - r.BaseStream.Position;
        if (n > 0 && (long)n * minElementSize > left) throw new InvalidDataException($"NetCodec: length {n} runs past the message");
        return n;
    }

    static int MinSize(Type t) =>
        t == typeof(bool) || t == typeof(byte) || tagOf.ContainsKey(t) ? 1 :
        t == typeof(short) || t == typeof(ushort) ? 2 :
        t == typeof(long) || t == typeof(ulong) || t == typeof(double) ? 8 : 4; // ints, floats, enums, string/array lengths

    static FieldInfo[] Layout(Type t) => layouts[t];
}

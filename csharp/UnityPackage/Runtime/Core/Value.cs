// Unofficial C# implementation of the AngelScript primitive language subset.
// This is not the original AngelCode Scripting Library. See LICENSE.md.
using System.Runtime.InteropServices;

namespace AngelScript.Burst
{
    public enum ScriptType : byte
    {
        Void, Bool, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double
    }

    /// <summary>A tagged, unmanaged primitive. Getters require the corresponding type.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct Value
    {
        [FieldOffset(0)] private ulong bits;
        [FieldOffset(0)] private float single;
        [FieldOffset(0)] private double real;
        [FieldOffset(8)] public ScriptType Type;

        public ulong Bits { get { return bits; } }
        public bool AsBool { get { return bits != 0; } }
        public sbyte AsInt8 { get { return unchecked((sbyte)bits); } }
        public byte AsUInt8 { get { return unchecked((byte)bits); } }
        public short AsInt16 { get { return unchecked((short)bits); } }
        public ushort AsUInt16 { get { return unchecked((ushort)bits); } }
        public int AsInt32 { get { return unchecked((int)bits); } }
        public uint AsUInt32 { get { return unchecked((uint)bits); } }
        public long AsInt64 { get { return unchecked((long)bits); } }
        public ulong AsUInt64 { get { return bits; } }
        public float AsFloat { get { return single; } }
        public double AsDouble { get { return real; } }

        public static Value Zero(ScriptType type) { return new Value { Type = type }; }
        public static Value FromBool(bool value) { return new Value { Type = ScriptType.Bool, bits = value ? 1UL : 0UL }; }
        public static Value FromInt8(sbyte value) { return new Value { Type = ScriptType.Int8, bits = unchecked((byte)value) }; }
        public static Value FromUInt8(byte value) { return new Value { Type = ScriptType.UInt8, bits = value }; }
        public static Value FromInt16(short value) { return new Value { Type = ScriptType.Int16, bits = unchecked((ushort)value) }; }
        public static Value FromUInt16(ushort value) { return new Value { Type = ScriptType.UInt16, bits = value }; }
        public static Value FromInt32(int value) { return new Value { Type = ScriptType.Int32, bits = unchecked((uint)value) }; }
        public static Value FromUInt32(uint value) { return new Value { Type = ScriptType.UInt32, bits = value }; }
        public static Value FromInt64(long value) { return new Value { Type = ScriptType.Int64, bits = unchecked((ulong)value) }; }
        public static Value FromUInt64(ulong value) { return new Value { Type = ScriptType.UInt64, bits = value }; }
        public static Value FromFloat(float value) { return new Value { Type = ScriptType.Float, single = value }; }
        public static Value FromDouble(double value) { return new Value { Type = ScriptType.Double, real = value }; }
    }

    /// <summary>Type predicates that may also be called from Burst code.</summary>
    public static class ScriptTypes
    {
        public static bool IsValid(ScriptType type) { return type >= ScriptType.Void && type <= ScriptType.Double; }
        public static bool IsInteger(ScriptType type) { return type >= ScriptType.Int8 && type <= ScriptType.UInt64; }
        public static bool IsNumeric(ScriptType type) { return IsInteger(type) || type == ScriptType.Float || type == ScriptType.Double; }
        public static bool IsSigned(ScriptType type)
        {
            return type == ScriptType.Int8 || type == ScriptType.Int16 || type == ScriptType.Int32 || type == ScriptType.Int64;
        }
        public static int BitWidth(ScriptType type)
        {
            if (type == ScriptType.Int8 || type == ScriptType.UInt8 || type == ScriptType.Bool) return 8;
            if (type == ScriptType.Int16 || type == ScriptType.UInt16) return 16;
            if (type == ScriptType.Int32 || type == ScriptType.UInt32 || type == ScriptType.Float) return 32;
            if (type == ScriptType.Int64 || type == ScriptType.UInt64 || type == ScriptType.Double) return 64;
            return 0;
        }
        public static ScriptType PromoteInteger(ScriptType type)
        {
            if (type == ScriptType.Int8 || type == ScriptType.Int16) return ScriptType.Int32;
            if (type == ScriptType.UInt8 || type == ScriptType.UInt16) return ScriptType.UInt32;
            return type;
        }
    }
}

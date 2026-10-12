// Unofficial C# AngelScript subset; not the original AngelCode library.
using System;

namespace AngelScript.Burst
{
    /// <summary>Allocation-free arithmetic shared by constant folding and the Burst VM.</summary>
    public static class NumericOperations
    {
        public static long ReadSigned(Value value)
        {
            switch (value.Type)
            {
                case ScriptType.Int8: return value.AsInt8;
                case ScriptType.Int16: return value.AsInt16;
                case ScriptType.Int32: return value.AsInt32;
                default: return value.AsInt64;
            }
        }

        public static ulong ReadUnsigned(Value value)
        {
            switch (value.Type)
            {
                case ScriptType.UInt8: return value.AsUInt8;
                case ScriptType.UInt16: return value.AsUInt16;
                case ScriptType.UInt32: return value.AsUInt32;
                default: return value.AsUInt64;
            }
        }

        public static double ReadReal(Value value)
        {
            if (value.Type == ScriptType.Float) return value.AsFloat;
            if (value.Type == ScriptType.Double) return value.AsDouble;
            if (ScriptTypes.IsSigned(value.Type)) return ReadSigned(value);
            if (value.Type == ScriptType.Bool) return value.AsBool ? 1.0 : 0.0;
            return ReadUnsigned(value);
        }

        public static bool TryConvert(Value source, ScriptType target, out Value result)
        {
            result = default(Value);
            if (!ScriptTypes.IsValid(source.Type) || !ScriptTypes.IsValid(target)) return false;
            if (source.Type == target) { result = source; return true; }
            if (source.Type == ScriptType.Void || target == ScriptType.Void) return false;
            if (target == ScriptType.Bool)
            {
                result = Value.FromBool(ReadReal(source) != 0.0);
                return true;
            }
            if (target == ScriptType.Float)
            {
                // Preserve a direct integer-to-float conversion (no intermediate double rounding).
                float f = ScriptTypes.IsSigned(source.Type) ? (float)ReadSigned(source) :
                    ScriptTypes.IsInteger(source.Type) ? (float)ReadUnsigned(source) : (float)ReadReal(source);
                result = Value.FromFloat(f);
                return true;
            }
            if (target == ScriptType.Double)
            {
                result = Value.FromDouble(ReadReal(source));
                return true;
            }

            ulong raw;
            if (ScriptTypes.IsInteger(source.Type))
                raw = ScriptTypes.IsSigned(source.Type) ? unchecked((ulong)ReadSigned(source)) : ReadUnsigned(source);
            else if (source.Type == ScriptType.Bool)
                raw = source.AsBool ? 1UL : 0UL;
            else
            {
                double real = Math.Truncate(ReadReal(source));
                // Define out-of-range / non-finite casts as a VM fault, rather than relying on
                // platform-specific CPU conversion behavior. Narrow casts use a 32-bit intermediate.
                if (double.IsNaN(real)) return false;
                // Both signed and unsigned narrow types use a signed int32 intermediate.
                // Thus uint8(-2.5) becomes uint8(int(-2.5)) == 254, as in AngelScript.
                bool signedIntermediate = ScriptTypes.IsSigned(target) || ScriptTypes.BitWidth(target) < 32;
                if (signedIntermediate)
                {
                    double min = target == ScriptType.Int64 ? -9223372036854775808.0 : -2147483648.0;
                    double end = target == ScriptType.Int64 ? 9223372036854775808.0 : 2147483648.0;
                    if (real < min || real >= end) return false;
                    raw = unchecked((ulong)(long)real);
                }
                else
                {
                    double end = target == ScriptType.UInt64 ? 18446744073709551616.0 : 4294967296.0;
                    if (real < 0.0 || real >= end) return false;
                    raw = (ulong)real;
                }
            }
            switch (target)
            {
                case ScriptType.Int8: result = Value.FromInt8(unchecked((sbyte)raw)); break;
                case ScriptType.UInt8: result = Value.FromUInt8(unchecked((byte)raw)); break;
                case ScriptType.Int16: result = Value.FromInt16(unchecked((short)raw)); break;
                case ScriptType.UInt16: result = Value.FromUInt16(unchecked((ushort)raw)); break;
                case ScriptType.Int32: result = Value.FromInt32(unchecked((int)raw)); break;
                case ScriptType.UInt32: result = Value.FromUInt32(unchecked((uint)raw)); break;
                case ScriptType.Int64: result = Value.FromInt64(unchecked((long)raw)); break;
                case ScriptType.UInt64: result = Value.FromUInt64(raw); break;
                default: return false;
            }
            return true;
        }

        public static bool TryUnary(OpCode code, Value value, out Value result, out VmError error)
        {
            result = default(Value);
            error = VmError.None;
            if (code == OpCode.LogicalNot && value.Type == ScriptType.Bool)
                result = Value.FromBool(!value.AsBool);
            else if (code == OpCode.Negate)
            {
                switch (value.Type)
                {
                    case ScriptType.Int32: result = Value.FromInt32(unchecked(-value.AsInt32)); break;
                    case ScriptType.Int64: result = Value.FromInt64(unchecked(-value.AsInt64)); break;
                    case ScriptType.Float: result = Value.FromFloat(-value.AsFloat); break;
                    case ScriptType.Double: result = Value.FromDouble(-value.AsDouble); break;
                    default: error = VmError.TypeMismatch; break;
                }
            }
            else if (code == OpCode.BitNot)
            {
                switch (value.Type)
                {
                    case ScriptType.Int32: result = Value.FromInt32(~value.AsInt32); break;
                    case ScriptType.UInt32: result = Value.FromUInt32(~value.AsUInt32); break;
                    case ScriptType.Int64: result = Value.FromInt64(~value.AsInt64); break;
                    case ScriptType.UInt64: result = Value.FromUInt64(~value.AsUInt64); break;
                    default: error = VmError.TypeMismatch; break;
                }
            }
            else error = VmError.TypeMismatch;
            return error == VmError.None;
        }

        public static bool TryBinary(OpCode code, Value left, Value right, out Value result, out VmError error)
        {
            result = default(Value);
            error = VmError.None;
            bool shift = code == OpCode.ShiftLeft || code == OpCode.ShiftRightLogical || code == OpCode.ShiftRightArithmetic;
            bool doubleIntegerPower = code == OpCode.Power && left.Type == ScriptType.Double && right.Type == ScriptType.Int32;
            if ((!shift && !doubleIntegerPower && left.Type != right.Type) || (shift && right.Type != ScriptType.UInt32))
            { error = VmError.TypeMismatch; return false; }

            if (left.Type == ScriptType.Bool)
            {
                if (code == OpCode.Equal) result = Value.FromBool(left.AsBool == right.AsBool);
                else if (code == OpCode.NotEqual || code == OpCode.LogicalXor) result = Value.FromBool(left.AsBool != right.AsBool);
                else error = VmError.TypeMismatch;
            }
            else if (left.Type == ScriptType.Float || left.Type == ScriptType.Double)
            {
                if (left.Type == ScriptType.Float)
                    FloatBinary(code, left.AsFloat, right.AsFloat, out result, out error);
                else
                    DoubleBinary(code, left.AsDouble, doubleIntegerPower ? right.AsInt32 : right.AsDouble, out result, out error);
            }
            else if (left.Type == ScriptType.Int32 || left.Type == ScriptType.Int64)
            {
                SignedBinary(code, left, right, out result, out error);
            }
            else if (left.Type == ScriptType.UInt32 || left.Type == ScriptType.UInt64)
            {
                UnsignedBinary(code, left, right, out result, out error);
            }
            else error = VmError.TypeMismatch;
            return error == VmError.None;
        }

        private static void SignedBinary(OpCode code, Value left, Value right, out Value result, out VmError error)
        {
            result = default(Value);
            error = VmError.None;
            bool wide = left.Type == ScriptType.Int64;
            long x = wide ? left.AsInt64 : left.AsInt32;
            long y = right.Type == ScriptType.UInt32 ? right.AsUInt32 : wide ? right.AsInt64 : right.AsInt32;
            long r = 0;
            switch (code)
            {
                case OpCode.Add: r = unchecked(x + y); break;
                case OpCode.Subtract: r = unchecked(x - y); break;
                case OpCode.Multiply: r = unchecked(x * y); break;
                case OpCode.Divide:
                case OpCode.Modulo:
                    if (y == 0) { error = VmError.DivisionByZero; return; }
                    if (y == -1 && x == (wide ? long.MinValue : int.MinValue))
                    { error = VmError.IntegerOverflow; return; }
                    r = code == OpCode.Divide ? x / y : x % y;
                    break;
                case OpCode.Power:
                    if (!SignedPower(x, y, wide ? long.MinValue : int.MinValue, wide ? long.MaxValue : int.MaxValue, out r))
                    { error = VmError.IntegerOverflow; return; }
                    break;
                case OpCode.BitAnd: r = x & y; break;
                case OpCode.BitOr: r = x | y; break;
                case OpCode.BitXor: r = x ^ y; break;
                case OpCode.ShiftLeft: r = wide ? x << ((int)y & 63) : unchecked((int)x << ((int)y & 31)); break;
                // AngelScript >> is LOGICAL; >>> is ARITHMETIC, unlike C#.
                case OpCode.ShiftRightLogical:
                    r = wide ? unchecked((long)((ulong)x >> ((int)y & 63))) : (long)(unchecked((uint)x) >> ((int)y & 31)); break;
                case OpCode.ShiftRightArithmetic:
                    r = wide ? x >> ((int)y & 63) : (int)x >> ((int)y & 31); break;
                case OpCode.Equal: result = Value.FromBool(x == y); return;
                case OpCode.NotEqual: result = Value.FromBool(x != y); return;
                case OpCode.Less: result = Value.FromBool(x < y); return;
                case OpCode.LessEqual: result = Value.FromBool(x <= y); return;
                case OpCode.Greater: result = Value.FromBool(x > y); return;
                case OpCode.GreaterEqual: result = Value.FromBool(x >= y); return;
                default: error = VmError.TypeMismatch; return;
            }
            result = wide ? Value.FromInt64(r) : Value.FromInt32(unchecked((int)r));
        }

        private static void UnsignedBinary(OpCode code, Value left, Value right, out Value result, out VmError error)
        {
            result = default(Value);
            error = VmError.None;
            bool wide = left.Type == ScriptType.UInt64;
            ulong x = wide ? left.AsUInt64 : left.AsUInt32;
            ulong y = right.Type == ScriptType.UInt32 ? right.AsUInt32 : wide ? right.AsUInt64 : right.AsUInt32;
            ulong r = 0;
            switch (code)
            {
                case OpCode.Add: r = unchecked(x + y); break;
                case OpCode.Subtract: r = unchecked(x - y); break;
                case OpCode.Multiply: r = unchecked(x * y); break;
                case OpCode.Divide:
                case OpCode.Modulo:
                    if (y == 0) { error = VmError.DivisionByZero; return; }
                    r = code == OpCode.Divide ? x / y : x % y; break;
                case OpCode.Power:
                    if (!UnsignedPower(x, y, wide ? ulong.MaxValue : uint.MaxValue, out r))
                    { error = VmError.IntegerOverflow; return; }
                    break;
                case OpCode.BitAnd: r = x & y; break;
                case OpCode.BitOr: r = x | y; break;
                case OpCode.BitXor: r = x ^ y; break;
                case OpCode.ShiftLeft: r = x << ((int)y & (wide ? 63 : 31)); break;
                case OpCode.ShiftRightLogical: r = x >> ((int)y & (wide ? 63 : 31)); break;
                case OpCode.ShiftRightArithmetic:
                    r = wide ? unchecked((ulong)((long)x >> ((int)y & 63))) : unchecked((uint)((int)x >> ((int)y & 31))); break;
                case OpCode.Equal: result = Value.FromBool(x == y); return;
                case OpCode.NotEqual: result = Value.FromBool(x != y); return;
                case OpCode.Less: result = Value.FromBool(x < y); return;
                case OpCode.LessEqual: result = Value.FromBool(x <= y); return;
                case OpCode.Greater: result = Value.FromBool(x > y); return;
                case OpCode.GreaterEqual: result = Value.FromBool(x >= y); return;
                default: error = VmError.TypeMismatch; return;
            }
            result = wide ? Value.FromUInt64(r) : Value.FromUInt32(unchecked((uint)r));
        }

        private static void FloatBinary(OpCode code, float x, float y, out Value result, out VmError error)
        {
            error = VmError.None;
            result = default(Value);
            float r;
            switch (code)
            {
                case OpCode.Add: r = x + y; break;
                case OpCode.Subtract: r = x - y; break;
                case OpCode.Multiply: r = x * y; break;
                case OpCode.Divide:
                case OpCode.Modulo:
                    if (y == 0.0f) { error = VmError.DivisionByZero; return; }
                    r = code == OpCode.Divide ? x / y : x % y; break;
                case OpCode.Power:
                    r = (float)Math.Pow(x, y);
                    if (r == float.PositiveInfinity || r == float.NegativeInfinity)
                    { error = VmError.IntegerOverflow; return; }
                    break;
                case OpCode.Equal: result = Value.FromBool(x == y); return;
                case OpCode.NotEqual: result = Value.FromBool(x != y); return;
                case OpCode.Less: result = Value.FromBool(x < y); return;
                case OpCode.LessEqual: result = Value.FromBool(x <= y); return;
                case OpCode.Greater: result = Value.FromBool(x > y); return;
                case OpCode.GreaterEqual: result = Value.FromBool(x >= y); return;
                default: error = VmError.TypeMismatch; return;
            }
            result = Value.FromFloat(r);
        }

        private static void DoubleBinary(OpCode code, double x, double y, out Value result, out VmError error)
        {
            error = VmError.None;
            result = default(Value);
            double r;
            switch (code)
            {
                case OpCode.Add: r = x + y; break;
                case OpCode.Subtract: r = x - y; break;
                case OpCode.Multiply: r = x * y; break;
                case OpCode.Divide:
                case OpCode.Modulo:
                    if (y == 0.0) { error = VmError.DivisionByZero; return; }
                    r = code == OpCode.Divide ? x / y : x % y; break;
                case OpCode.Power:
                    r = Math.Pow(x, y);
                    if (r == double.PositiveInfinity || r == double.NegativeInfinity)
                    { error = VmError.IntegerOverflow; return; }
                    break;
                case OpCode.Equal: result = Value.FromBool(x == y); return;
                case OpCode.NotEqual: result = Value.FromBool(x != y); return;
                case OpCode.Less: result = Value.FromBool(x < y); return;
                case OpCode.LessEqual: result = Value.FromBool(x <= y); return;
                case OpCode.Greater: result = Value.FromBool(x > y); return;
                case OpCode.GreaterEqual: result = Value.FromBool(x >= y); return;
                default: error = VmError.TypeMismatch; return;
            }
            result = Value.FromDouble(r);
        }

        private static bool SignedMultiply(long a, long b, long min, long max, out long result)
        {
            result = 0;
            if (a == 0 || b == 0) return true;
            if (a > 0)
            {
                if (b > 0 ? a > max / b : b < min / a) return false;
            }
            else
            {
                if (b > 0 ? a < min / b : a < max / b) return false;
            }
            result = unchecked(a * b);
            return true;
        }

        private static bool SignedPower(long basis, long exponent, long min, long max, out long result)
        {
            result = 1;
            if (basis == 0 && exponent == 0) return false;
            if (exponent < 0) { result = 0; return basis != 0; }
            // Upstream power bounds use the positive signed maximum even for a
            // negative result. They reject (-2)**31 / (-8)**21 despite MIN fitting.
            bool rejectMinimum = exponent > 1;
            while (exponent != 0)
            {
                if ((exponent & 1) != 0 && !SignedMultiply(result, basis, min, max, out result)) return false;
                exponent >>= 1;
                if (exponent != 0 && !SignedMultiply(basis, basis, min, max, out basis)) return false;
            }
            return !rejectMinimum || result != min;
        }

        private static bool UnsignedPower(ulong basis, ulong exponent, ulong max, out ulong result)
        {
            result = 1;
            if (basis == 0 && exponent == 0) return false;
            while (exponent != 0)
            {
                if ((exponent & 1) != 0)
                {
                    if (basis != 0 && result > max / basis) return false;
                    result = unchecked(result * basis);
                }
                exponent >>= 1;
                if (exponent != 0)
                {
                    if (basis != 0 && basis > max / basis) return false;
                    basis = unchecked(basis * basis);
                }
            }
            return true;
        }

        public static unsafe bool TryIntrinsic(IntrinsicId id, Value* arguments, int count, ScriptType type, out Value result, out VmError error)
        {
            result = default(Value);
            error = VmError.None;
            int expected = id == IntrinsicId.Clamp ? 3 : id == IntrinsicId.Min || id == IntrinsicId.Max || id == IntrinsicId.Pow ? 2 : 1;
            if (id > IntrinsicId.Clamp || count != expected || arguments == null)
            { error = VmError.InvalidInstruction; return false; }
            for (int i = 0; i < count; i++)
                if (arguments[i].Type != type) { error = VmError.TypeMismatch; return false; }

            if (id == IntrinsicId.Min || id == IntrinsicId.Max || id == IntrinsicId.Clamp)
            {
                Value comparison;
                if (!TryBinary(OpCode.Less, arguments[0], arguments[1], out comparison, out error)) return false;
                result = id == IntrinsicId.Max ? (comparison.AsBool ? arguments[1] : arguments[0]) :
                    (comparison.AsBool ? arguments[0] : arguments[1]);
                if (id == IntrinsicId.Clamp)
                {
                    result = comparison.AsBool ? arguments[1] : arguments[0];
                    if (!TryBinary(OpCode.Greater, result, arguments[2], out comparison, out error)) return false;
                    if (comparison.AsBool) result = arguments[2];
                }
                return true;
            }
            if (type != ScriptType.Float && type != ScriptType.Double)
            { error = VmError.TypeMismatch; return false; }
            double x = ReadReal(arguments[0]);
            double r;
            switch (id)
            {
                case IntrinsicId.Sin: r = Math.Sin(x); break;
                case IntrinsicId.Cos: r = Math.Cos(x); break;
                case IntrinsicId.Tan: r = Math.Tan(x); break;
                case IntrinsicId.Sqrt: r = Math.Sqrt(x); break;
                case IntrinsicId.Abs: r = Math.Abs(x); break;
                case IntrinsicId.Floor: r = Math.Floor(x); break;
                case IntrinsicId.Ceil: r = Math.Ceiling(x); break;
                case IntrinsicId.Exp: r = Math.Exp(x); break;
                case IntrinsicId.Log: r = Math.Log(x); break;
                case IntrinsicId.Pow: r = Math.Pow(x, ReadReal(arguments[1])); break;
                default: error = VmError.InvalidInstruction; return false;
            }
            result = type == ScriptType.Float ? Value.FromFloat((float)r) : Value.FromDouble(r);
            return true;
        }
    }
}

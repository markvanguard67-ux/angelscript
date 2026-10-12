// Unofficial C# AngelScript subset; not the original AngelCode library.
using System;
using System.Globalization;

namespace AngelScript.Burst.Compilation
{
    internal static class LiteralParser
    {
        internal static Value Parse(LiteralSyntax literal)
        {
            string text = literal.Text;
            if (text == "true" || text == "false") return Value.FromBool(text == "true");
            bool prefixed = text.Length >= 2 && text[0] == '0' && "xXbBoOdD".IndexOf(text[1]) >= 0;
            int radix = 10;
            if (prefixed)
            {
                char c = char.ToLowerInvariant(text[1]);
                radix = c == 'x' ? 16 : c == 'b' ? 2 : c == 'o' ? 8 : 10;
            }
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\'' && (i == 0 || i == text.Length - 1 ||
                    (prefixed && i <= 2) || Digit(text[i - 1]) < 0 || Digit(text[i - 1]) >= radix || Digit(text[i + 1]) < 0 || Digit(text[i + 1]) >= radix))
                    throw Invalid(literal, "A number separator must occur between digits.");
            text = text.Replace("'", "");
            bool single = !prefixed && (text.EndsWith("f") || text.EndsWith("F"));
            if (single && text.IndexOf('.') < 0 && text.IndexOf('e') < 0 && text.IndexOf('E') < 0)
                throw Invalid(literal, "A real literal with an f suffix requires a decimal point or exponent (e.g. 1.0f).");
            if (!prefixed && (single || text.IndexOf('.') >= 0 || text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0))
            {
                if (single) text = text.Substring(0, text.Length - 1);
                double value;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsInfinity(value))
                    throw Invalid(literal, "Malformed or overflowing real literal.");
                // Match the upstream scanner's decimal accumulation rather than CLR
                // correctly-rounded parsing (e.g. upstream 0.75 is one ULP above 0.75).
                value = ScanRealLikeAngelScript(text, literal);
                if (double.IsInfinity(value) || double.IsNaN(value)) throw Invalid(literal, "Malformed or overflowing real literal.");
                float f = (float)value;
                if (single && float.IsInfinity(f)) throw Invalid(literal, "The literal does not fit in float.");
                return single ? Value.FromFloat(f) : Value.FromDouble(value);
            }
            int start = prefixed ? 2 : 0;
            if (text.Length == start) throw Invalid(literal, "An integer literal requires digits.");
            ulong bits = 0;
            for (int i = start; i < text.Length; i++)
            {
                int digit = Digit(text[i]);
                if (digit < 0 || digit >= radix || bits > (ulong.MaxValue - (uint)digit) / (uint)radix)
                    throw Invalid(literal, "Malformed integer literal or value exceeding uint64.");
                bits = bits * (uint)radix + (uint)digit;
            }
            if (prefixed) return bits <= uint.MaxValue ? Value.FromUInt32((uint)bits) : Value.FromUInt64(bits);
            if (bits <= int.MaxValue) return Value.FromInt32((int)bits);
            return bits <= long.MaxValue ? Value.FromInt64((long)bits) : Value.FromUInt64(bits);
        }

        private static double ScanRealLikeAngelScript(string text, LiteralSyntax literal)
        {
            int position = 0;
            double value = 0.0;
            while (position < text.Length && text[position] >= '0' && text[position] <= '9')
                value = value * 10.0 + (text[position++] - '0');
            if (position < text.Length && text[position] == '.')
            {
                position++;
                double fraction = 0.1;
                while (position < text.Length && text[position] >= '0' && text[position] <= '9')
                {
                    value += fraction * (text[position++] - '0');
                    fraction *= 0.1;
                }
            }
            if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
            {
                position++;
                bool negative = position < text.Length && text[position] == '-';
                if (position < text.Length && (text[position] == '+' || text[position] == '-')) position++;
                int exponent = 0;
                while (position < text.Length)
                {
                    int digit = text[position++] - '0';
                    if (exponent > (int.MaxValue - digit) / 10) throw Invalid(literal, "The literal exponent is too large.");
                    exponent = exponent * 10 + digit;
                }
                if (exponent != 0) value *= Math.Pow(10.0, negative ? -exponent : exponent);
            }
            return value;
        }

        private static int Digit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
        private static CompilationFailure Invalid(LiteralSyntax literal, string message)
        { return new CompilationFailure(literal.Location, DiagnosticCode.InvalidLiteral, message); }
    }

    internal static class ConstantFolder
    {
        internal static bool TryEvaluate(BoundExpression expression, out Value value, out VmError error)
        {
            value = default(Value);
            error = VmError.None;
            var literal = expression as BoundLiteral;
            if (literal != null) { value = literal.Value; return true; }
            var copy = expression as BoundCopy;
            if (copy != null) return TryEvaluate(copy.Expression, out value, out error);
            var conversion = expression as BoundConversion;
            if (conversion != null)
            {
                Value operand;
                if (!TryEvaluate(conversion.Expression, out operand, out error)) return false;
                if (!NumericOperations.TryConvert(operand, conversion.Type, out value)) { error = VmError.InvalidConversion; return false; }
                return true;
            }
            var unary = expression as BoundUnary;
            if (unary != null)
            {
                Value operand;
                return TryEvaluate(unary.Operand, out operand, out error) && NumericOperations.TryUnary(unary.Code, operand, out value, out error);
            }
            var binary = expression as BoundBinary;
            if (binary != null)
            {
                Value left, right;
                if (!TryEvaluate(binary.Left, out left, out error)) return false;
                if (binary.ShortCircuit != ShortCircuit.None)
                {
                    if (left.AsBool == (binary.ShortCircuit == ShortCircuit.Or)) { value = left; return true; }
                    return TryEvaluate(binary.Right, out value, out error);
                }
                return TryEvaluate(binary.Right, out right, out error) && NumericOperations.TryBinary(binary.Code, left, right, out value, out error);
            }
            var conditional = expression as BoundConditional;
            if (conditional != null)
            {
                Value condition;
                return TryEvaluate(conditional.Condition, out condition, out error) &&
                    TryEvaluate(condition.AsBool ? conditional.Then : conditional.Else, out value, out error);
            }
            return false;
        }

        internal static BoundExpression Fold(BoundExpression expression)
        {
            if (!expression.IsConstant) return expression;
            Value value;
            VmError error;
            if (TryEvaluate(expression, out value, out error)) return new BoundLiteral(value, expression.Location);
            var binary = expression as BoundBinary;
            if (binary != null && binary.Code == OpCode.Power && error == VmError.IntegerOverflow)
                throw new CompilationFailure(expression.Location, DiagnosticCode.InvalidConstant, "Overflow or domain error in a constant exponent operation.");
            return expression;
        }
    }
}

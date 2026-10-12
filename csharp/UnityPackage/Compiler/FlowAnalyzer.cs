// Unofficial C# AngelScript subset; not the original AngelCode library.
using System;

namespace AngelScript.Burst.Compilation
{
    [Flags]
    internal enum Flow { None = 0, FallThrough = 1, Return = 2, Break = 4, Continue = 8 }

    internal static class FlowAnalyzer
    {
        internal static Flow Analyze(BoundStatement statement)
        {
            if (statement == null) return Flow.FallThrough;
            var block = statement as BoundBlock;
            if (block != null)
            {
                Flow result = Flow.FallThrough;
                foreach (BoundStatement item in block.Statements)
                    if ((result & Flow.FallThrough) != 0) result = (result & ~Flow.FallThrough) | Analyze(item);
                return result;
            }
            if (statement is BoundReturn) return Flow.Return;
            var control = statement as BoundControl;
            if (control != null) return control.IsContinue ? Flow.Continue : Flow.Break;
            var condition = statement as BoundIf;
            if (condition != null)
            {
                bool constant;
                if (TryBool(condition.Condition, out constant)) return Analyze(constant ? condition.Then : condition.Else);
                return Analyze(condition.Then) | Analyze(condition.Else);
            }
            var loop = statement as BoundWhile;
            if (loop != null) return AnalyzeLoop(loop.Condition, loop.Body, loop.IsDoWhile);
            var forLoop = statement as BoundFor;
            if (forLoop != null)
            {
                Flow initializer = Analyze(forLoop.Initializer);
                return (initializer & ~Flow.FallThrough) | ((initializer & Flow.FallThrough) != 0 ? AnalyzeLoop(forLoop.Condition, forLoop.Body, false) : Flow.None);
            }
            var switchStatement = statement as BoundSwitch;
            if (switchStatement != null)
            {
                Flow result = Flow.None;
                Flow next = Flow.FallThrough;
                bool hasDefault = false;
                for (int i = switchStatement.Cases.Count - 1; i >= 0; i--)
                {
                    BoundSwitchCase item = switchStatement.Cases[i];
                    hasDefault |= item.IsDefault;
                    Flow body = Analyze(item.Body);
                    next = (body & ~Flow.FallThrough) | ((body & Flow.FallThrough) != 0 ? next : Flow.None);
                    result |= next;
                }
                if ((result & Flow.Break) != 0) result = (result & ~Flow.Break) | Flow.FallThrough;
                if (!hasDefault) result |= Flow.FallThrough;
                return result;
            }
            return Flow.FallThrough;
        }

        private static Flow AnalyzeLoop(BoundExpression condition, BoundStatement bodyStatement, bool doWhile)
        {
            bool value;
            bool known = TryBool(condition, out value);
            if (known && !value && !doWhile) return Flow.FallThrough;
            Flow body = Analyze(bodyStatement);
            Flow result = body & Flow.Return;
            bool reachesTest = (body & (Flow.FallThrough | Flow.Continue)) != 0;
            if ((body & Flow.Break) != 0 || ((!known || !value) && (!doWhile || reachesTest))) result |= Flow.FallThrough;
            return result;
        }

        private static bool TryBool(BoundExpression expression, out bool value)
        {
            Value constant = default(Value);
            VmError error;
            bool success = expression.IsConstant && ConstantFolder.TryEvaluate(expression, out constant, out error);
            value = success && constant.AsBool;
            return success;
        }
    }
}

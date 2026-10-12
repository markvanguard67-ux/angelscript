using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AngelScript.Burst;
using AngelScript.Burst.Compilation;

namespace AngelScript.Tests
{
    // Unlike LanguageCases, this matrix intentionally includes known compatibility gaps.
    // --conformance must return failure until those gaps are resolved, not bless a subset.
    internal static class ConformanceAudit
    {
        private sealed class Case
        {
            internal readonly string Name, Source, Declaration;
            internal Case(string name, string source, string type = "int")
            { Name = name; Source = source; Declaration = type + " test()"; }
        }

        internal static int Run(string referenceRunner, string directory)
        {
            int matched = 0, mismatched = 0;
            foreach (Case item in Cases())
            {
                string actual = Execute(item.Source);
                string expected = ExecuteReference(referenceRunner, directory, item);
                if (actual == expected) { matched++; continue; }
                mismatched++;
                Console.Error.WriteLine("CONFORMANCE MISMATCH " + item.Name + ": C++=" + expected + ", C#=" + actual);
                Console.Error.WriteLine("  " + item.Source);
            }
            Console.WriteLine("Conformance audit: " + matched + " matched; " + mismatched + " mismatched. This includes known unsupported compatibility rules.");
            return mismatched;
        }

        private static string Execute(string source)
        {
            CompilationResult result = new ScriptCompiler().TryCompile(source, "conformance.as");
            if (!result.Success) return "compile-rejected";
            int entry = result.Module.FindFunction("test");
            using (var vm = new VmFixture(result.Module))
            {
                vm.Prepare(entry);
                ExecutionStatus status = vm.Run(1000000);
                if (status == ExecutionStatus.Faulted) return "runtime-fault";
                if (status != ExecutionStatus.Finished) return "unexpected-" + status;
                return "finished:" + vm.State.ReturnValue.Bits;
            }
        }

        private static string ExecuteReference(string runner, string directory, Case item)
        {
            string path = Path.Combine(directory, "conformance.as");
            File.WriteAllText(path, item.Source);
            var start = new ProcessStartInfo(runner, "\"" + path + "\" \"" + item.Declaration + "\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process process = Process.Start(start))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000)) { process.Kill(); throw new InvalidOperationException("C++ conformance oracle timed out."); }
                if (process.ExitCode == 3) return "compile-rejected";
                if (process.ExitCode == 5) return "runtime-fault";
                ulong bits;
                if (process.ExitCode != 0 || !ulong.TryParse(output.Result.Trim(), out bits))
                    throw new InvalidOperationException("C++ oracle failed, not a conformance result: " + process.ExitCode + " " + errors.Result);
                return "finished:" + bits;
            }
        }

        private static IEnumerable<Case> Cases()
        {
            string[] types = { "int8", "uint8", "int16", "uint16", "int", "uint", "int64", "uint64", "float", "double" };
            string[] arguments = { "1", "1.0", "1.0f", "int8(1)", "uint8(1)", "uint16(1)", "0xffffffff" };
            for (int i = 0; i < types.Length; i++)
                for (int j = i + 1; j < types.Length; j++)
                    foreach (string argument in arguments)
                        yield return new Case("overload " + types[i] + "/" + types[j] + " " + argument,
                            "int choose(" + types[i] + " x){return 1;}int choose(" + types[j] + " x){return 2;}int test(){return choose(" + argument + ");}");
            string[] values = { "-3", "250", "-32760", "65530", "-2000000000", "0xfffffffe", "-9007199254740993", "0xfffffffffffffff0", "-3.5f", "0.75" };
            for (int i = 0; i < types.Length; i++)
                for (int j = 0; j < types.Length; j++)
                    foreach (string op in new[] { "+", "-", "<", "==" })
                    {
                        string type = op == "<" || op == "==" ? "bool" : "double";
                        yield return new Case("promotion " + types[i] + op + types[j],
                            type + " test(){" + types[i] + " a=" + values[i] + ";" + types[j] + " b=" + values[j] + ";return a" + op + "b;}", type);
                    }
            foreach (string type in types)
            {
                yield return new Case("increment capture " + type, "double test(){" + type + " x=1;return x+x++;}", "double");
                yield return new Case("unary-plus capture " + type, "double test(){" + type + " x=1;return +x+x++;}", "double");
            }
            foreach (string exponent in new[] { "0", "1", "-1", "int8(0)", "uint8(0)", "int64(9007199254740993)", "uint64(4294967297)" })
                yield return new Case("double power " + exponent, "double test(){double x=-2.0;return x**" + exponent + ";}", "double");
            yield return new Case("cast for initializer", "int test(){int i=0;for(int(1.2);i<1;i++){}return i;}");
            yield return new Case("floating shift count", "int test(){int n=3;float c=1.9f;return n<<c;}");
            yield return new Case("double compound power", "double test(){double x=-2.0;uint64 y=4294967297;x**=y;return x;}", "double");
            yield return new Case("wide switch zero", "int test(){uint64 n=4294967296;switch(n){case 0:return 1;default:return 2;}}");
            yield return new Case("wide switch case", "int test(){uint64 n=4294967296;switch(n){case 4294967296:return 1;default:return 2;}}");
            yield return new Case("wide duplicate cases", "int test(){uint64 n=4294967296;switch(n){case 0:return 1;case 4294967296:return 2;default:return 3;}}");
            yield return new Case("floating switch rejection", "int test(){double n=1.2;switch(n){case 1:return 1;default:return 2;}}");
            yield return new Case("void cast rejection", "void f(){}void test(){void(f());}", "void");
            yield return new Case("controlled declaration rejection", "int test(){int n=1;if(true)int n=2;return n;}");
            yield return new Case("void ternary", "void f(){}void test(){true?f():f();}", "void");
            yield return new Case("constant uint64 to double", "double test(){return double(uint64(0xffffffffffffffff));}", "double");
            yield return new Case("constant uint64 to float", "float test(){return float(uint64(0xffffffffffffffff));}", "float");

            // Second audit: compare captured globals with deferred local references.
            // Keep whitespace around operators to avoid accidentally creating postfix ++/--.
            string[] operands = { "a", "(a=2)", "(a+=2)", "(a*=2)", "++a", "a++", "+a", "-a", "TYPE(a)", "(true?a:b)", "change()" };
            foreach (string scope in new[] { "global", "local" })
                foreach (string operandType in types)
                    foreach (string left in operands)
                        foreach (string right in operands)
                            foreach (string op in new[] { "+", "-", "==" })
                            {
                                string returnType = op == "==" ? "bool" : "double";
                                string locals = scope == "local" ? operandType + " a=1,b=3;" : "";
                                string source = operandType + " a=1,b=3;" + operandType + " change(){a+=2;return a;}" +
                                    returnType + " test(){" + locals + "return " + left.Replace("TYPE", operandType) + " " + op + " " + right.Replace("TYPE", operandType) + ";}";
                                yield return new Case("side-effects " + scope + " " + operandType + " " + left + op + right, source, returnType);
                            }
            string[] boundaryValues = { "127", "255", "32767", "65535", "2147483647", "0xffffffff", "9223372036854775807", "0xffffffffffffffff", "0.25f", "0.75" };
            for (int i = 0; i < types.Length; i++)
                foreach (string constantType in types)
                    foreach (string constant in new[] { "1", "0", "-1", "0xffffffff" })
                    {
                        string returnType = constant == "0xffffffff" ? "bool" : "double";
                        string op = returnType == "bool" ? "<" : "+";
                        yield return new Case("typed-constant " + types[i] + op + constantType + "(" + constant + ")",
                            returnType + " test(){" + types[i] + " a=" + boundaryValues[i] + ";return a " + op + " " + constantType + "(" + constant + ");}", returnType);
                    }
            yield return new Case("constant power rejection", "int test(){return 2**31;}");
            yield return new Case("constant local power rejection", "int test(){const int n=2**31;return n;}");
            yield return new Case("constant zero power rejection", "int test(){return 0**0;}");
            yield return new Case("constant floating power rejection", "float test(){return 0.0f**-1.0f;}", "float");
            yield return new Case("constant double power rejection", "double test(){return 2.0**10000;}", "double");
            yield return new Case("case declaration rejection", "int test(){switch(0){case 0:int n=1;return n;default:return 0;}}");
            yield return new Case("braced case declaration", "int test(){switch(0){case 0:{int n=7;return n;}default:return 0;}}");
            // Include documented differences instead of silently excluding them from equivalence checks.
            yield return new Case("uninitialized const local", "int test(){const int n;return n;}");
            yield return new Case("constant signed division boundary", "int64 test(){return int64(-9223372036854775808)/int64(-1);}", "int64");
            yield return new Case("out-of-range cast policy", "int test(){const int n=int(1e30);return n;}");
            yield return new Case("assignment lvalue postfix", "int test(){int n=1;return (n=2)++;}");
            yield return new Case("assignment lvalue prefix", "int test(){int n=1;return ++(n=2);}");
            yield return new Case("compound lvalue postfix", "int test(){int n=1;return (n+=2)++;}");
            yield return new Case("conditional lvalue postfix", "int test(){int n=1;return (true?n:n)++;}");
            yield return new Case("assignment lvalue write", "int test(){int n=1;(n=2)=3;return n;}");
            yield return new Case("constant if return analysis", "int test(){if(true)return 1;}");
            yield return new Case("do-while return analysis", "int test(){do{return 1;}while(false);}");
            yield return new Case("while return analysis", "int test(){while(true)return 1;}");
            yield return new Case("for return analysis", "int test(){for(;;)return 1;}");

        }
    }
}

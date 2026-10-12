using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AngelScript.Burst;
using AngelScript.Burst.Compilation;

namespace AngelScript.Tests
{
    internal static unsafe class Program
    {
        private static int passed, failed, referencePassed;
        private static string referenceRunner;
        private static string temporaryDirectory;
        private static int seedCount = 200;
        private static bool conformance;
        private static readonly Value[] NoArguments = new Value[0];

        private static int Main(string[] arguments)
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                if (arguments[i] == "--reference" && i + 1 < arguments.Length) referenceRunner = Path.GetFullPath(arguments[++i]);
                else if (arguments[i] == "--seeds" && i + 1 < arguments.Length && int.TryParse(arguments[++i], out seedCount) && seedCount >= 0) { }
                else if (arguments[i] == "--conformance") conformance = true;
                else { Console.Error.WriteLine("Usage: AngelScript.Tests [--reference path-to-reference-runner] [--seeds count] [--conformance]"); return 2; }
            }
            if (conformance && referenceRunner == null) { Console.Error.WriteLine("--conformance requires --reference to the original C++ runner."); return 2; }
            if (referenceRunner != null && !File.Exists(referenceRunner)) { Console.Error.WriteLine("Reference runner not found: " + referenceRunner); return 2; }
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "angelscript-csharp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                foreach (LanguageCase test in LanguageCases.All) Test(test.Name, delegate { RunLanguageCase(test); });
                Test("Compiler diagnostics", CompilerDiagnostics);
                Test("Compiler resource limits", CompilerLimits);
                Test("Malformed source fuzz reports diagnostics, not CLR exceptions", MalformedSourceFuzz);
                Test("Compilation is reusable and immutable", ImmutableCompilation);
                Test("Host ABI, argument order and resume", HostAbi);
                Test("Host failure and return type mismatch", HostErrors);
                Test("Void and zero-argument host calls", VoidHosts);
                Test("Budget exhaustion and resumption", BudgetResume);
                Test("Infinite script can be bounded", InfiniteBudget);
                Test("Globals persist across Prepare and reset independently", PersistentGlobals);
                Test("Independent VM lanes execute in parallel", ParallelInstances);
                Test("Preparation validates signatures and buffers", PreparationErrors);
                Test("Bounded call/local/operand stacks", StackLimits);
                Test("Runtime arithmetic faults and source positions", RuntimeErrors);
                Test("Malformed bytecode is checked", MalformedBytecode);
                Test("Primitive and VM data are unmanaged", UnmanagedLayout);
                Test("Execute and Prepare allocate zero managed bytes", ZeroAllocations);
                Test("Culture-independent numeric literals", InvariantLiterals);
                Test("Seeded arithmetic/property cases (" + seedCount + ")", RandomCases);
                if (conformance) Test("Expanded primitive conformance matrix (includes known gaps)", delegate
                { Check(ConformanceAudit.Run(referenceRunner, temporaryDirectory) == 0, "The primitive subset is not fully C++ conformant; see the mismatches and csharp/AUDIT.md."); });
                Console.WriteLine("\n" + passed + " groups passed; " + failed + " failed; " + referencePassed + " C++ differential cases passed.");
                Console.WriteLine("Unity/Burst integration is tested separately in the Unity package; this runner tests the real core and compiler, not a Burst emulation.");
                return failed == 0 ? 0 : 1;
            }
            finally { Directory.Delete(temporaryDirectory, true); }
        }

        private static void Test(string name, Action body)
        {
            try { body(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + exception); }
        }
        private static void Check(bool condition, string message = "Assertion failed")
        { if (!condition) throw new InvalidOperationException(message); }
        private static void Equal<T>(T expected, T actual, string message = "")
        { Check(EqualityComparer<T>.Default.Equals(expected, actual), message + " expected=" + expected + " actual=" + actual); }
        private static void EqualValue(Value expected, Value actual)
        { Equal(expected.Type, actual.Type, "Value type"); Equal(expected.Bits, actual.Bits, "Value bits"); }

        private static void RunLanguageCase(LanguageCase test)
        {
            var compiler = new ScriptCompiler();
            if (test.Math) compiler.RegisterMathFunctions();
            CompiledModule module = compiler.Compile(test.Source, test.Name + ".as");
            int function = module.FindFunction("test"); Check(function >= 0, "Missing test entry.");
            using (var vm = new VmFixture(module))
            {
                Equal(ExecutionStatus.Ready, vm.Prepare(function, NoArguments));
                Equal(ExecutionStatus.Finished, vm.Run(), test.Source);
                EqualValue(test.Expected, vm.State.ReturnValue);
                if (referenceRunner != null && test.ReferenceCompatible) CompareReference(test.Source, test.Declaration, vm.State.ReturnValue);
            }
        }
        private static void CompareReference(string source, string declaration, Value actual)
        {
            string path = Path.Combine(temporaryDirectory, "oracle.as"); File.WriteAllText(path, source);
            var start = new ProcessStartInfo(referenceRunner, "\"" + path + "\" \"" + declaration + "\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process process = Process.Start(start))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000)) { process.Kill(); throw new InvalidOperationException("C++ oracle timed out."); }
                string text = output.Result.Trim();
                Check(process.ExitCode == 0, "C++ oracle failed (" + process.ExitCode + "): " + errors.Result + " source=" + source);
                ulong expected; Check(ulong.TryParse(text, out expected), "Invalid oracle output: " + text);
                Equal(expected, actual.Bits, "C++ differential mismatch for " + source);
                referencePassed++;
            }
        }

        private static void ExpectDiagnostic(string source, DiagnosticCode code)
        {
            CompilationResult result = new ScriptCompiler().TryCompile(source, "invalid.as");
            Check(!result.Success && result.Module == null && result.Diagnostics.Count == 1, "Expected one diagnostic, no partial module.");
            Equal(code, result.Diagnostics[0].Code, source + ": " + result.Diagnostics[0]);
            Check(result.Diagnostics[0].Location.Line > 0 && result.Diagnostics[0].Location.Column > 0, "Diagnostic has no location.");
        }
        private static void CompilerDiagnostics()
        {
            ExpectDiagnostic("class Actor {}", DiagnosticCode.UnsupportedFeature);
            ExpectDiagnostic("enum Flags {A,B}", DiagnosticCode.UnsupportedFeature);
            ExpectDiagnostic("namespace N {}", DiagnosticCode.UnsupportedFeature);
            ExpectDiagnostic("void test(){ string s; }", DiagnosticCode.UnsupportedFeature);
            ExpectDiagnostic("void test(){ \"text\"; }", DiagnosticCode.UnsupportedFeature);
            ExpectDiagnostic("void test(int &out n){n=1;}", DiagnosticCode.UnsupportedFeature);
            ExpectDiagnostic("void test(){int[] values;}", DiagnosticCode.UnsupportedFeature);
            ExpectDiagnostic("int test(int n=1){return n;}", DiagnosticCode.UnsupportedFeature);
            ExpectDiagnostic("int test(){return missing;}", DiagnosticCode.UnknownName);
            ExpectDiagnostic("int test(){return missing();}", DiagnosticCode.UnknownName);
            ExpectDiagnostic("int test(){int x;int x;return 1;}", DiagnosticCode.DuplicateName);
            ExpectDiagnostic("int test(int x){int x=0;return x;}", DiagnosticCode.DuplicateName);
            ExpectDiagnostic("int test(){const int x=1;x=2;return x;}", DiagnosticCode.InvalidOperation);
            ExpectDiagnostic("int test(const int x){x++;return x;}", DiagnosticCode.InvalidOperation);
            ExpectDiagnostic("int test(){break;return 1;}", DiagnosticCode.InvalidOperation);
            ExpectDiagnostic("int test(){continue;return 1;}", DiagnosticCode.InvalidOperation);
            ExpectDiagnostic("int test(){1=2;return 1;}", DiagnosticCode.InvalidOperation);
            ExpectDiagnostic("int test(){if(true){int n=2;}return n;}", DiagnosticCode.UnknownName);
            ExpectDiagnostic("int test(){for(int i=0;i<2;i++){}return i;}", DiagnosticCode.UnknownName);
            ExpectDiagnostic("int test(bool n){if(n)return 1;}", DiagnosticCode.MissingReturn);
            ExpectDiagnostic("int test(){while(false){return 1;}}", DiagnosticCode.MissingReturn);
            ExpectDiagnostic("int test(){do{break;}while(true);}", DiagnosticCode.MissingReturn);
            ExpectDiagnostic("int test(int n){switch(n){case 1:return 1;}}", DiagnosticCode.MissingReturn);
            ExpectDiagnostic("void test(int n){switch(n){case 1:break;case 1:break;}}", DiagnosticCode.DuplicateName);
            ExpectDiagnostic("void test(int n){switch(n){default:break;default:break;}}", DiagnosticCode.DuplicateName);
            ExpectDiagnostic("int test(){return true;}", DiagnosticCode.TypeMismatch);
            ExpectDiagnostic("bool test(){return bool(1);}", DiagnosticCode.TypeMismatch);
            ExpectDiagnostic("void test(){if(1){} }", DiagnosticCode.TypeMismatch);
            ExpectDiagnostic("int test(){return 3.0f & 1;}", DiagnosticCode.InvalidOperation);
            ExpectDiagnostic("int test(){return 1/0;}", DiagnosticCode.InvalidConstant);
            ExpectDiagnostic("int test(){return 2**31;}", DiagnosticCode.InvalidConstant);
            ExpectDiagnostic("int test(){const int n=2**31;return n;}", DiagnosticCode.InvalidConstant);
            ExpectDiagnostic("int test(){int n=2**31;return n;}", DiagnosticCode.InvalidConstant);
            ExpectDiagnostic("int test(){return 0**0;}", DiagnosticCode.InvalidConstant);
            ExpectDiagnostic("uint test(){return uint(0)**uint(0);}", DiagnosticCode.InvalidConstant);
            ExpectDiagnostic("float test(){return 0.0f**-1.0f;}", DiagnosticCode.InvalidConstant);
            ExpectDiagnostic("double test(){return 2.0**10000;}", DiagnosticCode.InvalidConstant);
            ExpectDiagnostic("int test(){switch(0){case 0:int n=1;return n;default:return 0;}}", DiagnosticCode.InvalidOperation);
            ExpectDiagnostic("int test(){switch(0){default:const int n=1;return n;}}", DiagnosticCode.InvalidOperation);
            ExpectDiagnostic("int x=1;int y=x;int test(){return y;}", DiagnosticCode.InvalidConstant);
            ExpectDiagnostic("const int a=b;const int b=a;", DiagnosticCode.InvalidConstant);
            ExpectDiagnostic("int test(){uint64 n=0;switch(n){case 0:return 1;case 4294967296:return 2;default:return 3;}}", DiagnosticCode.DuplicateName);
            ExpectDiagnostic("void f(){}void test(){void(f());}", DiagnosticCode.TypeMismatch);
            ExpectDiagnostic("int test(){if(true)int n=2;return 1;}", DiagnosticCode.InvalidOperation);
            ExpectDiagnostic("void test(){while(false)int n=2;}", DiagnosticCode.InvalidOperation);
            ExpectDiagnostic("int test(){switch(1){}return 1;}", DiagnosticCode.InvalidOperation);
            ExpectDiagnostic("void f(){}void test(){return f();}", DiagnosticCode.TypeMismatch);
            ExpectDiagnostic("float test(){return 1f;}", DiagnosticCode.InvalidLiteral);
            ExpectDiagnostic("double test(){return 1e999999999999999999;}", DiagnosticCode.InvalidLiteral);
            ExpectDiagnostic("int test(){return 0b102;}", DiagnosticCode.InvalidLiteral);
            ExpectDiagnostic("int test(){return 0x;}", DiagnosticCode.InvalidLiteral);
            ExpectDiagnostic("int test(){return 1''000;}", DiagnosticCode.InvalidLiteral);
            ExpectDiagnostic("int test(){return 18446744073709551616;}", DiagnosticCode.InvalidLiteral);
            ExpectDiagnostic("float test(){return 1e+f;}", DiagnosticCode.InvalidLiteral);
            ExpectDiagnostic("/* unterminated", DiagnosticCode.UnexpectedToken);
            ExpectDiagnostic("int f(float x,int y){return 1;}int f(int x,float y){return 2;}int test(){return f(1,1);}", DiagnosticCode.AmbiguousCall);
            var result = new ScriptCompiler().TryCompile("int test(){\n return unknown;\n}", "position.as");
            Equal(2, result.Diagnostics[0].Location.Line); Equal(9, result.Diagnostics[0].Location.Column);
            Equal("position.as", result.Diagnostics[0].Location.SourceName);
        }
        private static void CompilerLimits()
        {
            Equal(DiagnosticCode.ResourceLimit, new ScriptCompiler(new CompilerOptions { MaxSourceLength = 5 }).TryCompile("int test(){}").Diagnostics[0].Code);
            Equal(DiagnosticCode.ResourceLimit, new ScriptCompiler(new CompilerOptions { MaxTokens = 2 }).TryCompile("void test(){}").Diagnostics[0].Code);
            Equal(DiagnosticCode.ResourceLimit, new ScriptCompiler(new CompilerOptions { MaxInstructions = 1 }).TryCompile("int test(){return 1;}").Diagnostics[0].Code);
            Equal(DiagnosticCode.ResourceLimit, new ScriptCompiler(new CompilerOptions { MaxLocalsPerFunction = 1 }).TryCompile("void test(){int a,b;}").Diagnostics[0].Code);
            Equal(DiagnosticCode.ResourceLimit, new ScriptCompiler(new CompilerOptions { MaxParameters = 1 }).TryCompile("void test(int a,int b){}").Diagnostics[0].Code);
            Equal(DiagnosticCode.ResourceLimit, new ScriptCompiler(new CompilerOptions { MaxFunctions = 1 }).TryCompile("void a(){}void b(){}").Diagnostics[0].Code);
            Equal(DiagnosticCode.ResourceLimit, new ScriptCompiler(new CompilerOptions { MaxGlobals = 0 }).TryCompile("int a;").Diagnostics[0].Code);
            string deep = "int test(){return " + new string('(', 200) + "1" + new string(')', 200) + ";}";
            Equal(DiagnosticCode.ResourceLimit, new ScriptCompiler().TryCompile(deep).Diagnostics[0].Code);
            string chain = "int test(int x){return x";
            for (int i = 0; i < 500; i++) chain += "+x";
            Equal(DiagnosticCode.ResourceLimit, new ScriptCompiler().TryCompile(chain + ";}").Diagnostics[0].Code);
        }
        private static void MalformedSourceFuzz()
        {
            var random = new Random(723671);
            var compiler = new ScriptCompiler();
            string[] tokens = { "int", "void", "bool", "float", "double", "const", "if", "else", "return", "switch", "case", "default",
                "while", "for", "break", "continue", "(", ")", "{", "}", "[", "]", ";", ",", ":", "?", "=", "+", "-", "++", "--", "**", "/", "%", "==",
                "&&", "xor", "true", "false", "0", "1", "1.0f", "0x", "1e", "a", "b", "test", "@", "class", "namespace" };
            for (int i = 0; i < 10000; i++)
            {
                var source = new System.Text.StringBuilder();
                if ((i & 1) != 0) source.Append("int test(){");
                int count = random.Next(1, 70);
                for (int j = 0; j < count; j++) source.Append(tokens[random.Next(tokens.Length)]).Append(' ');
                if ((i & 1) != 0) source.Append('}');
                // The outer test harness fails on any escaped CLR exception. Some generated
                // sources are valid empty/global-only modules; don't require every input to fail.
                CompilationResult result = compiler.TryCompile(source.ToString(), "fuzz.as");
                Check(result.Success ? result.Module != null && result.Diagnostics.Count == 0 :
                    result.Module == null && result.Diagnostics.Count == 1, "Malformed compilation result for " + source);
            }
        }

        private static void ImmutableCompilation()
        {
            var compiler = new ScriptCompiler();
            Check(!compiler.TryCompile("class C{}").Success);
            CompiledModule first = compiler.Compile("int test(){return 7;}");
            compiler.Compile("int test(){return 12;}");
            Instruction[] copy = first.CopyInstructions(); copy[0].Code = (OpCode)255;
            using (var vm = new VmFixture(first)) { vm.Prepare(first.FindFunction("test")); Equal(ExecutionStatus.Finished, vm.Run()); Equal(7, vm.State.ReturnValue.AsInt32); }
            Equal(-1, first.FindFunction("test", ScriptType.Int32));
            compiler.RegisterHostFunction("int sample(int)", 2);
            bool duplicate = false;
            try { compiler.RegisterHostFunction("float sample(int)", 3); } catch (ScriptCompilationException) { duplicate = true; }
            Check(duplicate, "Return type cannot create an overload.");
            var mathCompiler = new ScriptCompiler();
            mathCompiler.RegisterHostFunction("float sqrt(float)", 99);
            bool mathConflict = false;
            try { mathCompiler.RegisterMathFunctions(); } catch (ScriptCompilationException) { mathConflict = true; }
            Check(mathConflict);
            // A failed registration must not leave preceding intrinsic signatures behind.
            mathCompiler.RegisterHostFunction("float sin(float)", 100);
        }

        private static ScriptCompiler HostCompiler()
        { var compiler = new ScriptCompiler(); compiler.RegisterHostFunction("int combine(int first,int second)", 17); return compiler; }
        private static void HostAbi()
        {
            var module = HostCompiler().Compile("int count=0;int test(){return combine(count++,count++)+3;}");
            using (var vm = new VmFixture(module))
            {
                vm.Prepare(module.FindFunction("test")); Equal(ExecutionStatus.HostCallPending, vm.Run());
                Equal(17, vm.State.HostFunctionId); Equal(2, vm.State.HostArgumentCount);
                Value* args = vm.HostArguments(); Equal(1, args[0].AsInt32); Equal(0, args[1].AsInt32);
                long before = vm.State.InstructionsExecuted;
                Equal(ExecutionStatus.HostCallPending, vm.Run()); Equal(before, vm.State.InstructionsExecuted);
                Equal(ExecutionStatus.Ready, vm.ReturnHost(Value.FromInt32(args[0].AsInt32 * 10 + args[1].AsInt32)));
                Equal(ExecutionStatus.Finished, vm.Run()); Equal(13, vm.State.ReturnValue.AsInt32);
            }
        }
        private static void HostErrors()
        {
            CompiledModule module = HostCompiler().Compile("int test(){return combine(1,2);}");
            using (var vm = new VmFixture(module))
            {
                int f = module.FindFunction("test"); vm.Prepare(f); vm.Run();
                Equal(ExecutionStatus.Faulted, vm.ReturnHost(Value.FromFloat(1))); Equal(VmError.HostReturnTypeMismatch, vm.State.Error);
                vm.Prepare(f); vm.Run(); Equal(ExecutionStatus.Faulted, vm.ReturnHost(default(Value), false)); Equal(VmError.HostFunctionFailed, vm.State.Error);
                vm.Prepare(f); Equal(ExecutionStatus.Faulted, vm.ReturnHost(Value.FromInt32(1))); Equal(VmError.InvalidState, vm.State.Error);
                vm.Prepare(f); vm.Run(); vm.Memory.Stack = null;
                Equal(ExecutionStatus.Faulted, vm.ReturnHost(Value.FromInt32(1))); Equal(VmError.InvalidState, vm.State.Error);
            }
        }
        private static void VoidHosts()
        {
            var compiler = new ScriptCompiler(); compiler.RegisterHostFunction("void signal()", 1); compiler.RegisterHostFunction("int value()", 2);
            CompiledModule module = compiler.Compile("int test(){signal();return value();}");
            using (var vm = new VmFixture(module))
            {
                vm.Prepare(module.FindFunction("test")); Equal(ExecutionStatus.HostCallPending, vm.Run()); Equal(0, vm.State.HostArgumentCount);
                vm.ReturnHost(default(Value)); Equal(ExecutionStatus.HostCallPending, vm.Run()); Equal(2, vm.State.HostFunctionId);
                vm.ReturnHost(Value.FromInt32(42)); Equal(ExecutionStatus.Finished, vm.Run()); Equal(42, vm.State.ReturnValue.AsInt32);
            }
        }
        private static void BudgetResume()
        {
            CompiledModule module = new ScriptCompiler().Compile("int test(){int n=0;for(int i=0;i<20;i++)n+=i;return n;}");
            using (var vm = new VmFixture(module))
            {
                vm.Prepare(module.FindFunction("test")); Equal(ExecutionStatus.Suspended, vm.Run(0)); Equal(0L, vm.State.InstructionsExecuted);
                int slices = 0;
                while (vm.State.Status != ExecutionStatus.Finished)
                {
                    long before = vm.State.InstructionsExecuted; vm.Run(3);
                    Check(vm.State.InstructionsExecuted - before <= 3, "Instruction budget exceeded.");
                    Check(vm.State.Status == ExecutionStatus.Suspended || vm.State.Status == ExecutionStatus.Finished);
                    Check(++slices < 1000, "Did not finish.");
                }
                Equal(190, vm.State.ReturnValue.AsInt32); Check(slices > 1);
                long total = vm.State.InstructionsExecuted; Equal(ExecutionStatus.Finished, vm.Run()); Equal(total, vm.State.InstructionsExecuted);
            }
        }
        private static void InfiniteBudget()
        {
            CompiledModule module = new ScriptCompiler().Compile("void test(){while(true){} }");
            using (var vm = new VmFixture(module))
            {
                vm.Prepare(module.FindFunction("test")); Equal(ExecutionStatus.Suspended, vm.Run(1000)); Equal(1000L, vm.State.InstructionsExecuted);
                Equal(ExecutionStatus.Suspended, vm.Run(25)); Equal(1025L, vm.State.InstructionsExecuted);
            }
        }
        private static void PersistentGlobals()
        {
            CompiledModule module = new ScriptCompiler().Compile("int n=3;int test(){return ++n;}");
            using (var a = new VmFixture(module)) using (var b = new VmFixture(module))
            {
                int f = module.FindFunction("test");
                a.Prepare(f); a.Run(); Equal(4, a.State.ReturnValue.AsInt32);
                a.Prepare(f); a.Run(); Equal(5, a.State.ReturnValue.AsInt32);
                b.Prepare(f); b.Run(); Equal(4, b.State.ReturnValue.AsInt32);
                Check(VirtualMachine.InitializeGlobals(a.Program, a.Memory)); a.Prepare(f); a.Run(); Equal(4, a.State.ReturnValue.AsInt32);
            }
        }
        private static void ParallelInstances()
        {
            CompiledModule module = new ScriptCompiler().Compile("int total=1;int test(int x){for(int i=0;i<100;i++)total+=x;return total;}");
            int function = module.FindFunction("test", ScriptType.Int32);
            Parallel.For(0, 64, delegate(int lane)
            {
                using (var vm = new VmFixture(module))
                {
                    vm.Prepare(function, Value.FromInt32(lane)); Equal(ExecutionStatus.Finished, vm.Run()); Equal(1 + 100 * lane, vm.State.ReturnValue.AsInt32);
                }
            });
        }
        private static void PreparationErrors()
        {
            CompiledModule module = new ScriptCompiler().Compile("int test(float x){return int(x);}");
            using (var vm = new VmFixture(module))
            {
                int f = module.FindFunction("test", ScriptType.Float);
                Equal(ExecutionStatus.Faulted, vm.Prepare(-1)); Equal(VmError.InvalidFunction, vm.State.Error);
                vm.Prepare(f); Equal(VmError.ArgumentCountMismatch, vm.State.Error);
                vm.Prepare(f, Value.FromInt32(2)); Equal(VmError.ArgumentTypeMismatch, vm.State.Error);
                Equal(ExecutionStatus.Ready, vm.Prepare(f, Value.FromFloat(2))); Equal(ExecutionStatus.Finished, vm.Run()); Equal(2, vm.State.ReturnValue.AsInt32);
                vm.Memory.Stack = null; vm.Prepare(f, Value.FromFloat(2)); Equal(VmError.InvalidMemory, vm.State.Error);
            }
            using (var vm = new VmFixture(module, frames: 0)) { vm.Prepare(0, Value.FromFloat(1)); Equal(VmError.CallStackOverflow, vm.State.Error); }
            using (var vm = new VmFixture(module, locals: 0)) { vm.Prepare(0, Value.FromFloat(1)); Equal(VmError.LocalStackOverflow, vm.State.Error); }
        }
        private static void StackLimits()
        {
            CompiledModule recursive = new ScriptCompiler().Compile("int test(int n){return test(n+1);}");
            using (var vm = new VmFixture(recursive, frames: 3))
            { vm.Prepare(0, Value.FromInt32(0)); Equal(ExecutionStatus.Faulted, vm.Run()); Equal(VmError.CallStackOverflow, vm.State.Error); }
            using (var vm = new VmFixture(recursive, locals: 2))
            { vm.Prepare(0, Value.FromInt32(0)); Equal(ExecutionStatus.Faulted, vm.Run()); Equal(VmError.LocalStackOverflow, vm.State.Error); }
            CompiledModule operand = new ScriptCompiler().Compile("int id(int n){return n;}int test(){return id(1)+id(2);}");
            using (var vm = new VmFixture(operand, stack: 1))
            { vm.Prepare(operand.FindFunction("test")); Equal(ExecutionStatus.Faulted, vm.Run()); Equal(VmError.StackOverflow, vm.State.Error); }
        }
        private static void RuntimeErrors()
        {
            CompiledModule divide = new ScriptCompiler().Compile("int test(int x){\n int y=12;\n return y/x;\n}", "divide.as");
            using (var vm = new VmFixture(divide))
            {
                vm.Prepare(0, Value.FromInt32(0)); Equal(ExecutionStatus.Faulted, vm.Run()); Equal(VmError.DivisionByZero, vm.State.Error);
                Equal(3, divide.GetInstructionLocation(vm.State.FaultInstruction).Line);
            }
            CheckRuntimeError("int test(int n){int x=-2147483648;return x/n;}", Value.FromInt32(-1), VmError.IntegerOverflow);
            CheckRuntimeError("int64 test(int64 n){int64 x=-9223372036854775808;return x%n;}", Value.FromInt64(-1), VmError.IntegerOverflow);
            CheckRuntimeError("float test(float n){return 1.0f/n;}", Value.FromFloat(0), VmError.DivisionByZero);
            CheckRuntimeError("double test(double n){return 1.0%n;}", Value.FromDouble(0), VmError.DivisionByZero);
            CheckRuntimeError("int test(int n){return 2**n;}", Value.FromInt32(31), VmError.IntegerOverflow);
            CheckRuntimeError("int test(int n){return (-2)**n;}", Value.FromInt32(31), VmError.IntegerOverflow);
            CheckRuntimeError("int64 test(int64 n){return int64(-8)**n;}", Value.FromInt64(21), VmError.IntegerOverflow);
            CheckRuntimeError("int test(double n){return int(n);}", Value.FromDouble(double.NaN), VmError.InvalidConversion);
            CheckRuntimeError("int test(double n){return int(n);}", Value.FromDouble(double.PositiveInfinity), VmError.InvalidConversion);
            CheckRuntimeError("int test(double n){return int(n);}", Value.FromDouble(2147483648.0), VmError.InvalidConversion);
            CheckRuntimeError("int test(int n){return 0**n;}", Value.FromInt32(0), VmError.IntegerOverflow);
            CheckRuntimeError("uint test(uint n){return uint(0)**n;}", Value.FromUInt32(0), VmError.IntegerOverflow);
        }
        private static void CheckRuntimeError(string source, Value argument, VmError expected)
        {
            CompiledModule module = new ScriptCompiler().Compile(source);
            using (var vm = new VmFixture(module)) { vm.Prepare(0, argument); Equal(ExecutionStatus.Faulted, vm.Run()); Equal(expected, vm.State.Error); }
        }
        private static void MalformedBytecode()
        {
            CompiledModule module = new ScriptCompiler().Compile("int test(){return 1;}");
            using (var vm = new VmFixture(module))
            {
                vm.Prepare(0); vm.Instructions[0] = new Instruction { Code = (OpCode)255 }; vm.Run(); Equal(VmError.InvalidInstruction, vm.State.Error);
                vm.Prepare(0); vm.Instructions[0] = new Instruction { Code = OpCode.Pop }; vm.Run(); Equal(VmError.StackUnderflow, vm.State.Error);
                vm.Prepare(0); vm.Instructions[0] = new Instruction { Code = OpCode.LoadLocal, A = -1 }; vm.Run(); Equal(VmError.InvalidInstruction, vm.State.Error);
                vm.Prepare(0); vm.Instructions[0] = new Instruction { Code = OpCode.Jump, A = int.MaxValue }; vm.Run(); Equal(VmError.InvalidInstruction, vm.State.Error);
                vm.Prepare(0); vm.State.FrameCount = int.MaxValue; vm.Run(); Equal(VmError.InvalidState, vm.State.Error);
                vm.Functions[0].CodeLength = int.MaxValue; vm.Prepare(0); Equal(VmError.InvalidProgram, vm.State.Error);
            }
        }
        private static void UnmanagedLayout()
        {
            foreach (Type type in new[] { typeof(Value), typeof(Instruction), typeof(FunctionInfo), typeof(ProgramView), typeof(VmMemory), typeof(CallFrame), typeof(ExecutionState) })
                Check(IsUnmanaged(type), "Managed field found in " + type);
            Equal(16, sizeof(Value));
        }
        private static bool IsUnmanaged(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type.IsPointer) return true;
            if (!type.IsValueType) return false;
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) if (!IsUnmanaged(field.FieldType)) return false;
            return true;
        }
        private static void ZeroAllocations()
        {
            CompiledModule module = new ScriptCompiler().Compile("int test(int n){int v=0;for(int i=0;i<10;i++)v+=n;return v;}");
            Value[] arguments = { Value.FromInt32(2) };
            using (var vm = new VmFixture(module))
            {
                for (int i = 0; i < 100; i++) { vm.Prepare(0, arguments); vm.Run(); }
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) { vm.Prepare(0, arguments); vm.Run(); }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Equal(0L, allocated, "VM allocation count"); Equal(20, vm.State.ReturnValue.AsInt32);
            }
        }
        private static void InvariantLiterals()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                try { CultureInfo.CurrentCulture = new CultureInfo("fr-FR"); }
                catch (CultureNotFoundException) { Console.WriteLine("NOTE: invariant-only test runtime; non-invariant locale unavailable."); }
                RunLanguageCase(new LanguageCase("Invariant literals", "double test(){return 1.5e2+0.25;}", Value.FromDouble(150.25)));
            }
            finally { CultureInfo.CurrentCulture = original; }
        }
        private static void RandomCases()
        {
            var random = new Random(23900);
            string[] operators = { "+", "-", "*", "/", "%", "&", "|", "^", "<<", ">>", ">>>" };
            for (int i = 0; i < seedCount; i++)
            {
                int a = random.Next(-100000, 100001), b = random.Next(-100000, 100001);
                int op = random.Next(operators.Length);
                if (op == 3 || op == 4) { if (b == 0) b = 1; }
                if (op >= 8) b = random.Next(0, 31);
                int expected;
                switch (op)
                {
                    case 0: expected = unchecked(a + b); break;
                    case 1: expected = unchecked(a - b); break;
                    case 2: expected = unchecked(a * b); break;
                    case 3: expected = a / b; break;
                    case 4: expected = a % b; break;
                    case 5: expected = a & b; break;
                    case 6: expected = a | b; break;
                    case 7: expected = a ^ b; break;
                    case 8: expected = a << b; break;
                    case 9: expected = unchecked((int)((uint)a >> b)); break;
                    default: expected = a >> b; break;
                }
                string source = "int test(){int a=" + a.ToString(CultureInfo.InvariantCulture) + ",b=" + b.ToString(CultureInfo.InvariantCulture) + ";return a" + operators[op] + "b;}";
                RunLanguageCase(new LanguageCase("Seed " + i, source, Value.FromInt32(expected)));
            }
        }
    }
}

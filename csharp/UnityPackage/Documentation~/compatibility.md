# Compatibility contract (0.1.0)

This package implements an **initial primitive AngelScript subset in C#**. It does not replace the full 2.39 C++ engine. Source acceptance, engine configuration properties, add-ons, APIs, bytecode, object lifetimes and advanced overload resolution are not fully compatible.

## Supported syntax

| Area | Support |
| --- | --- |
| Types | `void` returns, `bool`, `int8/16/32/64`, `uint8/16/32/64`, `int`/`uint`, `float`, `double` |
| Declarations | Primitive globals/locals, `const`, multiple declarators, scoped shadowing |
| Functions | Bodies required; primitive by-value parameters, forward calls, recursion, numeric overloads |
| Operators | `+ - * / % **`, unary `+ - ~ ! not`, bitwise `& | ^ << >> >>>`, comparisons, assignments, increments |
| Logic | Lazy `&&/and`, `||/or`; eager `^^/xor`; lazy ternary |
| Flow | `if/else`, `while`, `do/while`, `for`, `switch`, `break`, `continue`, `return` |
| Literals | Decimal and `0b/0o/0d/0x` integers, digit separators, real/exponent/`f` literals, booleans |
| Comments | `//` and `/* ... */`; ASCII identifiers; CRLF/LF positions |
| Host API | Primitive signatures registered as IDs; unmanaged host dispatch from jobs |
| Optional math | float/double `sin cos tan sqrt abs floor ceil exp log pow`; 32/64-bit numeric `min max clamp` |

Only variable lvalues are implemented. Reference-producing/index/member lvalues are not. Functions must have a body; forward calls do not require prototypes. There are no default/named arguments or reference parameters. `for` accepts a declaration or single expression initializer, and comma-separated iteration expressions. Like AngelScript, `default` must be the last switch label, the switch must contain a label, and every switch expression/label is converted to int32. Controlled variable declarations and declarations within switch cases require a braced body. Void functions use bare `return;`, not `return someVoidCall();`, and void is not a cast target. An `f` suffix requires a decimal point or exponent (`1.0f`, not `1f`).

## Semantic details retained from the original implementation

- Decimal literals choose `int`, then `int64`, then `uint64` by magnitude. Radix literals choose `uint`/`uint64`.
- Integer arithmetic promotes narrow operands to 32-bit. A non-constant signed operand takes priority in mixed signed/unsigned arithmetic/comparison. A double literal paired with a non-constant float expression is narrowed to float. This is intentionally not C#'s promotion policy.
- Unary +/- converts unsigned values to their same-width signed type. Complement returns the corresponding unsigned type.
- `>>` is **logical** right shift; `>>>` is **arithmetic** right shift. C# uses different spellings.
- Power is left-associative. Unary operators bind more tightly than power. For `double ** integer`, the exponent is narrowed to int32, including compound assignment.
- Decimal real literals use the original engine's accumulation algorithm, not CLR correctly-rounded parsing. For example, native `0.75` is one ULP above the exact dyadic value.
- Shift counts may be numeric expressions; they are converted to uint32 before shifting.
- Function arguments are evaluated/captured **last to first**. Assignment/compound-assignment RHS runs before the target is read.
- Numeric **local** variable/assignment operands retain reference behavior until consumed by a binary operation. **Global** operands and global assignment results are captured before later side effects. For example, `i + i++` with `i = 1` produces 3; `+i + i++` produces 2. Prefix/postfix results and boolean operands are captured values. Numeric comparisons use the same local/global operand rules; `g + g++` for a global `g = 1` produces 2, whereas a local produces 3. The regression corpus compares these cases against the native engine; it does not guarantee every combination of side-effect-producing lvalues.
- Integer division truncates toward zero. Remainders preserve the numerator's sign. All division/modulo by zero, including floating zero, faults. Constant-zero divisors are compiler errors.
- Signed minimum divided/modulo by -1 faults without raising a CLR exception. Ordinary integer addition/subtraction/multiplication/narrowing wraps at the value's width.
- Boolean/numeric language conversions are rejected even when written as explicit casts; use comparisons to make conditions.
- Parameters and host returns must have the declared types at the VM boundary. Language numeric conversions are inserted into bytecode by the compiler.

## Deliberate differences and incomplete conformance

- Primitive locals are zero-initialized instead of exposing uninitialized stack contents. Const locals require an initializer here; the native compiler may accept uninitialized const locals with a warning. Declaration execution also initializes a local on each loop iteration.
- Global initializers must be constant primitive expressions (including references to const globals). Script/host calls and mutable global dependencies are rejected. No global initialization function/GC/heap is run.
- Globals belong to a **VM instance/lane**, not shared module state. They persist across `Prepare`; `InitializeGlobals` / `ResetGlobals` restores defaults. Const uses may be inlined, so const globals must not be externally changed.
- Float-to-integer casts truncate before range validation, then narrow through a signed int32 intermediate for both signed and unsigned small destinations. Non-finite and out-of-range 32/64-bit casts fault rather than using platform-specific CPU results. Floating literal overflow is rejected.
- Numeric overload ranking uses a small exact/widen/narrow/conversion cost model, not AngelScript's complete rules. This affects ordinary literals, not just boundary values: 67 of 315 two-overload audit cases mismatch the repository's engine. Prefer explicit casts, but do not treat this as language parity.
- Constant uint64-to-floating casts retain the positive unsigned magnitude instead of reproducing this native checkout's inconsistent signed treatment for constants with bit 63 set. The native variable path also returns the positive magnitude. Both constant examples are recorded as conformance mismatches.
- Return-flow analysis is conservative for nonconstant loops/switches, but can prove some infinite-loop paths that the original compiler rejects. Prototypes and all original engine options are absent.
- Large integer powers use bounded overflow-checked repeated squaring; some extreme signed boundary results can differ from the original engine's lookup-table implementation. Integer `0 ** 0` faults. Constant power overflow/domain failures are compiler errors rather than emitted runtime faults.
- Floating values use IEEE operators and scalar `System.Math` intrinsics. NaN handling, float transcendental rounding, constant folding on the managed compiler versus Burst, and out-of-range conversions can differ from the original VM and between platforms. Strict Burst float mode is used, but cross-platform bit determinism is **not** promised.
- Shift counts are masked to 5/6 bits. Do not rely on original C++ undefined behavior for out-of-range shift counts.
- Diagnostics report the first error and include a source location; there is no warning or error-recovery parity.
- Bytecode is private to this package, in-memory and versioned conceptually as `CompiledModule.BytecodeVersion`; no native AngelScript bytecode loading/saving, serialized Unity asset importer, C++ API facade or add-on ABI is implemented.

## Explicitly unsupported

Script classes, interfaces, mixins, object/value-type registration, inheritance, virtual dispatch, object handles/null/identity, references/in/out/inout, arrays/indexing, strings/characters, GC/refcounting/weakref, funcdefs/delegates/lambdas, templates, namespaces/using/imports, enums/typedefs/auto, exceptions, foreach, named/default/variadic arguments, native calling conventions, preprocessing and the SDK add-ons.

These require additional language, heap/layout, ownership and Burst/AOT design work. They are not mapped to managed C# objects or quietly passed through to a C++ plugin.

## Verification boundary

The standalone compiler/VM tests and result-bit comparisons with the repository's C++ engine have been run. The second-audit regular suite passes 110 groups / 5,088 comparisons at 5,000 seeds and includes 10,000 malformed-source fuzz inputs. All 7,260 local/global side-effect cases match, but the expanded 8,433-case matrix still reports 83 mismatches and exits with failure; see `csharp/AUDIT.md` in the repository. Passing the regular corpus does not imply conformance. Actual Unity Editor/Burst tests are included but cannot be run in a sandbox without Unity. Execute those tests and target-platform IL2CPP/AOT builds before treating Burst compatibility as verified. The initial subset must not be presented as a fully conforming AngelScript implementation.

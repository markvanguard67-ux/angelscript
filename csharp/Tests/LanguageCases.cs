using AngelScript.Burst;

namespace AngelScript.Tests
{
    internal sealed class LanguageCase
    {
        internal readonly string Name, Source, Declaration;
        internal readonly Value Expected;
        internal readonly bool ReferenceCompatible;
        internal readonly bool Math;
        internal LanguageCase(string name, string source, Value expected, bool referenceCompatible = true, bool math = false)
        {
            Name = name; Source = source; Expected = expected; ReferenceCompatible = referenceCompatible; Math = math;
            Declaration = TypeName(expected.Type) + " test()";
        }
        private static string TypeName(ScriptType type)
        {
            switch (type)
            {
                case ScriptType.Void: return "void";
                case ScriptType.Bool: return "bool";
                case ScriptType.Int8: return "int8";
                case ScriptType.UInt8: return "uint8";
                case ScriptType.Int16: return "int16";
                case ScriptType.UInt16: return "uint16";
                case ScriptType.Int32: return "int";
                case ScriptType.UInt32: return "uint";
                case ScriptType.Int64: return "int64";
                case ScriptType.UInt64: return "uint64";
                case ScriptType.Float: return "float";
                default: return "double";
            }
        }
    }

    internal static class LanguageCases
    {
        internal static readonly LanguageCase[] All = {
            new LanguageCase("Arithmetic", "int test(){int a=7,b=3;return a+b*2-a/b+a%b;}", Value.FromInt32(12)),
            new LanguageCase("Precedence", "int test(){return 1+2 << 2 | 1;}", Value.FromInt32(13)),
            new LanguageCase("Left-associative power", "int test(){return 2 ** 3 ** 2;}", Value.FromInt32(64)),
            new LanguageCase("Unsigned wrap", "uint test(){uint x=0xffffffff; x++;return x;}", Value.FromUInt32(0)),
            new LanguageCase("Signed wrap", "int test(){int x=2147483647;x+=1;return x;}", Value.FromInt32(int.MinValue)),
            new LanguageCase("Signed narrow wrap", "int test(){int8 x=127;x++;int16 y=32767;y++;return int(x)+int(y);}", Value.FromInt32(-32896)),
            new LanguageCase("Unsigned narrow wrap", "int test(){uint8 x=255;uint16 y=65535;return int(++x)+int(++y);}", Value.FromInt32(0)),
            new LanguageCase("Exact uint64", "uint64 test(){uint64 x=9007199254740993;return x+2;}", Value.FromUInt64(9007199254740995UL)),
            new LanguageCase("Exact int64", "int64 test(){int64 x=-9007199254740993;return x-2;}", Value.FromInt64(-9007199254740995L)),
            new LanguageCase("Radices and separators", "int test(){return 0b1010+0o10+0d10+0x10+1'000;}", Value.FromInt32(1044)),
            new LanguageCase("Float rounding", "float test(){float f=16777216.0f;return (f+1.0f)-f;}", Value.FromFloat(0)),
            new LanguageCase("Double rounding", "double test(){double f=9007199254740992.0;return (f+1.0)-f;}", Value.FromDouble(0)),
            new LanguageCase("Double literal with float variable", "float test(){float a=0.25f;return (a+0.1)-a;}", Value.FromFloat((0.25f + 0.1f) - 0.25f)),
            new LanguageCase("Primitive casts as expression statements", "int test(){int(3.5);float(2);bool(true);return 7;}", Value.FromInt32(7)),
            new LanguageCase("Cast in for initializer", "int test(){int i=0;for(int(1.2);i<1;i++){}return i;}", Value.FromInt32(1)),
            new LanguageCase("Signed cast truncates before range check", "int test(){double x=-2147483648.5;return int(x);}", Value.FromInt32(int.MinValue)),
            new LanguageCase("Unsigned narrow floating cast uses signed intermediate", "uint8 test(){double x=-2.5;return uint8(x);}", Value.FromUInt8(254)),
            new LanguageCase("Unsigned 16-bit floating cast uses signed intermediate", "uint16 test(){float x=-2.5f;return uint16(x);}", Value.FromUInt16(65534)),
            new LanguageCase("Floating shift count", "int test(){int n=3;float c=1.9f;return n<<c;}", Value.FromInt32(6)),
            new LanguageCase("Double power integer exponent is narrowed to int32", "double test(){double x=-1.0;int64 n=9007199254740993;return x**n;}", Value.FromDouble(-1)),
            new LanguageCase("Double compound power retains integer exponent", "double test(){double x=-2.0;uint64 n=4294967297;x**=n;return x;}", Value.FromDouble(-2)),
            new LanguageCase("Native decimal accumulation rounding", "double test(){double a=0.75,b=0.75;return a+b;}", Value.FromDouble(1.5000000000000002)),
            new LanguageCase("Boolean xor captures values", "bool test(){bool a=true;return a xor (a=false);}", Value.FromBool(true)),
            new LanguageCase("Numeric comparisons preserve reference reads", "bool test(){int a=1;return a==(a=2);}", Value.FromBool(true)),
            new LanguageCase("Unary plus comparison captures value", "bool test(){int a=1;return +a==(a=2);}", Value.FromBool(false)),
            new LanguageCase("Conditional result captures value", "int test(){int a=1,b=2;return (true?a:b)+(a=3);}", Value.FromInt32(4)),
            new LanguageCase("Truncating cast", "int test(){float f=-3.9f;return int(f);}", Value.FromInt32(-3)),
            new LanguageCase("Negative division/modulo", "int test(){int a=-7,b=3;return (a/b)*10+a%b;}", Value.FromInt32(-21)),
            new LanguageCase("Signed variable promotion", "bool test(){int s=-1;uint u=1;return s<u;}", Value.FromBool(true)),
            new LanguageCase("Logical shift", "int test(){int n=-8;return n>>1;}", Value.FromInt32(2147483644)),
            new LanguageCase("Arithmetic shift", "int test(){int n=-8;return n>>>1;}", Value.FromInt32(-4)),
            new LanguageCase("Unsigned arithmetic shift", "uint test(){uint n=0x80000000;return n>>>1;}", Value.FromUInt32(0xc0000000)),
            new LanguageCase("Small complement", "uint test(){int8 x=1;return ~x;}", Value.FromUInt32(254)),
            new LanguageCase("Unsigned unary minus", "int test(){uint x=0xffffffff;return -x;}", Value.FromInt32(1)),
            new LanguageCase("Unary plus forces value copy", "int test(){int i=1;return +i+i++;}", Value.FromInt32(2)),
            new LanguageCase("Global numeric operand is captured", "int g=1;int test(){return g+g++;}", Value.FromInt32(2)),
            new LanguageCase("Global assignment operands are captured", "int g=1;int test(){return (g=2)+(g=3);}", Value.FromInt32(5)),
            new LanguageCase("Global compound operands are captured", "int g=1;int test(){return (g+=1)+(g+=2);}", Value.FromInt32(6)),
            new LanguageCase("Global comparison captures the left value", "int g=1;bool test(){return g==(g=2);}", Value.FromBool(false)),
            new LanguageCase("Global value survives mutating call", "int g=1;int change(){g+=2;return g;}int test(){return g+change();}", Value.FromInt32(4)),
            new LanguageCase("Global uint64 captures exact bits", "uint64 g=9007199254740993;uint64 test(){return g+g++;}", Value.FromUInt64(18014398509481986UL)),
            new LanguageCase("Global float operands are captured", "float g=1.0f;float test(){return (g=2.0f)+(g=3.0f);}", Value.FromFloat(5)),
            new LanguageCase("Global double operands are captured", "double g=1.0;double test(){return g+g++;}", Value.FromDouble(2)),
            new LanguageCase("Global postfix comparison captures values", "int g=1;bool test(){return g==g++;}", Value.FromBool(true)),
            new LanguageCase("Same-type global cast captures value", "int g=1;int test(){return int(g)+g++;}", Value.FromInt32(2)),
            new LanguageCase("Global left versus local right assignment", "int g=1;int test(){int i=0;return g+(i=(g=3));}", Value.FromInt32(4)),
            new LanguageCase("Local left remains reference with global RHS", "int g=1;int test(){int i=1;return i+(g=(i=3));}", Value.FromInt32(6)),
            new LanguageCase("Variable operand remains reference", "int test(){int i=1;return i+i++;}", Value.FromInt32(3)),
            new LanguageCase("Prefix increment captured", "int test(){int i=1;return ++i+i++;}", Value.FromInt32(4)),
            new LanguageCase("Postfix increment captured", "int test(){int i=1;return i+++i;}", Value.FromInt32(3)),
            new LanguageCase("Assignment operand remains reference", "int test(){int i=1;return (i=2)+(i=3);}", Value.FromInt32(6)),
            new LanguageCase("Compound assignment operand remains reference", "int test(){int i=1;return (i+=1)+(i+=2);}", Value.FromInt32(8)),
            new LanguageCase("Compound RHS evaluated first", "int test(){int i=1;i+=i++;return i;}", Value.FromInt32(3)),
            new LanguageCase("Call arguments last first", "int seq=0;int combine(int a,int b){return a*10+b;}int test(){return combine(seq++,seq++);}", Value.FromInt32(10)),
            new LanguageCase("Arguments captured separately", "int combine(int a,int b){return a*10+b;}int test(){int i=1;return combine(i++,i);}", Value.FromInt32(11)),
            new LanguageCase("Short circuit and", "int counter=0;bool touch(){counter++;return true;}bool test(){bool b=false && touch();return b || counter==0;}", Value.FromBool(true)),
            new LanguageCase("Short circuit or", "bool test(){int n=0;return true || ++n>0;}", Value.FromBool(true)),
            new LanguageCase("Logical aliases and xor", "bool test(){return not false xor false and false or true;}", Value.FromBool(true)),
            new LanguageCase("Boolean comparison captures values", "bool test(){bool b=true;return b==(b=false);}", Value.FromBool(false)),
            new LanguageCase("Conditional is lazy", "int n=0;int test(){int a=true ? ++n : (n+=10);return a*10+n;}", Value.FromInt32(11)),
            new LanguageCase("Recursion Fibonacci", "int fib(int n){if(n<2)return n;return fib(n-1)+fib(n-2);}int test(){return fib(10);}", Value.FromInt32(55)),
            new LanguageCase("Mutual recursion/forward calls", "bool even(int n){if(n==0)return true;return odd(n-1);}bool odd(int n){if(n==0)return false;return even(n-1);}bool test(){return even(8);}", Value.FromBool(true)),
            new LanguageCase("Exact overloads", "int choose(int x){return 10;}int choose(float x){return 20;}int test(){return choose(1)+choose(1.0f);}", Value.FromInt32(30)),
            new LanguageCase("While break/continue", "int test(){int i=0,total=0;while(i<10){i++;if(i==3)continue;if(i==7)break;total+=i;}return total;}", Value.FromInt32(18)),
            new LanguageCase("For continue executes increment", "int test(){int total=0;for(int i=0;i<10;i++){if(i%2!=0)continue;total+=i;}return total;}", Value.FromInt32(20)),
            new LanguageCase("For multiple initializers/increments", "int test(){int sum=0;for(int i=0,j=10;i<3;i++,j--){sum+=i+j;}return sum;}", Value.FromInt32(30)),
            new LanguageCase("Do while continue checks condition", "int test(){int i=0,total=0;do{i++;if(i<3)continue;total+=i;}while(i<5);return total;}", Value.FromInt32(12)),
            new LanguageCase("Infinite for with return", "int test(){for(;;){return 7;}return 0;}", Value.FromInt32(7)),
            new LanguageCase("Infinite for with break", "int test(){int n=0;for(;;){n++;break;}return n;}", Value.FromInt32(1)),
            new LanguageCase("Nested loops", "int test(){int n=0;for(int i=0;i<3;i++)for(int j=0;j<4;j++){if(j==2)break;n++;}return n;}", Value.FromInt32(6)),
            new LanguageCase("Wide switch expression narrows to int32", "int test(){uint64 n=4294967296;switch(n){case 0:return 1;default:return 2;}}", Value.FromInt32(1)),
            new LanguageCase("Wide switch label narrows to int32", "int test(){uint64 n=4294967296;switch(n){case 4294967296:return 1;default:return 2;}}", Value.FromInt32(1)),
            new LanguageCase("Braced switch case may declare locals", "int test(){int n=0;switch(n){case 0:{int x=7;return x;}default:return 0;}}", Value.FromInt32(7)),
            new LanguageCase("Switch fallthrough", "int test(){int x=2,n=0;switch(x){case 1:n=1;break;case 2:n=2;case 3:n+=3;break;default:n=-1;}return n;}", Value.FromInt32(5)),
            new LanguageCase("Switch fallthrough into default", "int test(){int x=2,n=0;switch(x){case 2:n=3;default:n+=4;break;}return n;}", Value.FromInt32(7)),
            new LanguageCase("Continue crosses switch", "int test(){int n=0;for(int i=0;i<4;i++){switch(i){case 1:continue;case 2:n+=20;break;default:n++;}}return n;}", Value.FromInt32(22)),
            new LanguageCase("Switch all paths return", "int choose(int n){switch(n){case 1:return 11;default:return 22;}}int test(){return choose(1)+choose(9);}", Value.FromInt32(33)),
            new LanguageCase("Block shadowing", "int test(){int x=3;{int x=7;x++;}return x;}", Value.FromInt32(3)),
            new LanguageCase("Zero-initialized primitive locals (port guarantee)", "int test(){int x;float y;return x+int(y);}", Value.FromInt32(0), false),
            new LanguageCase("Forward const initializer", "const int n=base+2;const int base=3;int total=n;int test(){total+=2;return total;}", Value.FromInt32(7)),
            new LanguageCase("Real syntax and comments", "/*a*/float test(){//b\r\nfloat f=.5f;return f+1.25e2F;}", Value.FromFloat(125.5f)),
            new LanguageCase("Narrowing exact uint64", "uint test(){uint64 n=0xffffffffffffffff;return uint(n);}", Value.FromUInt32(uint.MaxValue)),
            new LanguageCase("Signed narrow return", "int8 test(){int n=130;return int8(n);}", Value.FromInt8(-126)),
            new LanguageCase("Unsigned narrow return", "uint16 test(){int n=60000;return uint16(n);}", Value.FromUInt16(60000)),
            new LanguageCase("Negative floating zero", "float test(){float f=0.0f;return -f;}", Value.FromFloat(-0.0f)),
            new LanguageCase("Int32/UInt32 type aliases", "int32 test(){uint32 n=3;return int32(n);}", Value.FromInt32(3)),
            new LanguageCase("Void and nested call", "int n=0;void step(){n++;}void test(){step();return;}", default(Value)),
            new LanguageCase("Const by-value parameters", "int id(const int n){return n;}int test(){return id(12);}", Value.FromInt32(12)),
            new LanguageCase("Scalar math intrinsics", "double test(){return sqrt(9.0)+sin(0.0)+pow(2.0,3.0);}", Value.FromDouble(11), false, true),
            new LanguageCase("Integer min/max/clamp", "int test(){return clamp(max(1,2)+min(6,7),0,5);}", Value.FromInt32(5), false, true),
            new LanguageCase("UInt64 min stays exact", "uint64 test(){return min(uint64(9007199254740993),uint64(9007199254740994));}", Value.FromUInt64(9007199254740993UL), false, true)
        };
    }
}

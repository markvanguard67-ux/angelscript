// Test-only original-engine oracle. Never linked or loaded by Unity.
#include "angelscript.h"
#include "scriptarray.h"
#include "scriptstdstring.h"
#include "scriptdictionary.h"
#include <cstdint>
#include <cstring>
#include <cmath>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>

static void Message(const asSMessageInfo *m, void *) { std::cerr << m->row << ':' << m->col << ' ' << m->message << '\n'; }
static std::string Read(const char *path) { std::ifstream f(path); std::ostringstream s; s << f.rdbuf(); return s.str(); }
// Each signature gets stable auxiliary data; use generic calls to avoid platform ABI assumptions.
static void Math(asIScriptGeneric *g)
{
    int kind = *static_cast<int *>(g->GetAuxiliary());
    const bool single = g->GetReturnTypeId() == asTYPEID_FLOAT;
    const double x = single ? g->GetArgFloat(0) : g->GetArgDouble(0);
    const double y = g->GetArgCount() > 1 ? (single ? g->GetArgFloat(1) : g->GetArgDouble(1)) : 0;
    double value = 0;
    switch (kind) {
        case 0: value = std::sin(x); break; case 1: value = std::cos(x); break;
        case 2: value = std::tan(x); break; case 3: value = std::sqrt(x); break;
        case 4: value = std::fabs(x); break; case 5: value = std::floor(x); break;
        case 6: value = std::ceil(x); break; case 7: value = std::exp(x); break;
        case 8: value = std::log(x); break; default: value = std::pow(x, y); break;
    }
    if (single) g->SetReturnFloat(static_cast<float>(value)); else g->SetReturnDouble(value);
}
static int kinds[10] = {0,1,2,3,4,5,6,7,8,9};
static void RegisterMath(asIScriptEngine *engine)
{
    const char *names[] = {"sin","cos","tan","sqrt","abs","floor","ceil","exp","log","pow"};
    const char *types[] = {"float","double"};
    for (int t = 0; t < 2; ++t) for (int i = 0; i < 10; ++i) {
        const std::string type(types[t]);
        const std::string decl = type + " " + names[i] + "(" + type + (i == 9 ? "," + type : "") + ")";
        if (engine->RegisterGlobalFunction(decl.c_str(), asFUNCTION(Math), asCALL_GENERIC, &kinds[i]) < 0) throw 1;
    }
}
int main(int argc, char **argv)
{
    if (argc != 4) { std::cerr << "Usage: validation-reference source.as 'int test()' core|math|addons\n"; return 2; }
    asIScriptEngine *engine = asCreateScriptEngine();
    engine->SetMessageCallback(asFUNCTION(Message), nullptr, asCALL_CDECL);
    const std::string profile(argv[3]);
    if (profile == "math") RegisterMath(engine);
    if (profile == "unsafe-references") engine->SetEngineProperty(asEP_ALLOW_UNSAFE_REFERENCES, true);
    if (profile == "addons") { RegisterStdString(engine); RegisterScriptArray(engine, true); RegisterScriptDictionary(engine); }
    asIScriptModule *module = engine->GetModule("validation", asGM_ALWAYS_CREATE);
    const std::string code = Read(argv[1]);
    if (code.empty()) { engine->ShutDownAndRelease(); return 2; }
    module->AddScriptSection("fixture", code.c_str(), code.size());
    if (module->Build() < 0) { engine->ShutDownAndRelease(); return 3; }
    asIScriptFunction *fn = module->GetFunctionByDecl(argv[2]);
    if (!fn || fn->GetParamCount() != 0) { engine->ShutDownAndRelease(); return 4; }
    asIScriptContext *ctx = engine->CreateContext(); ctx->Prepare(fn);
    int status = ctx->Execute(), result = 0;
    if (status != asEXECUTION_FINISHED) { std::cout << "fault " << ctx->GetExceptionString() << '\n'; result = 5; }
    else {
        uint64_t bits = 0;
        switch (fn->GetReturnTypeId()) {
            case asTYPEID_VOID: break;
            case asTYPEID_BOOL: case asTYPEID_INT8: case asTYPEID_UINT8: bits = ctx->GetReturnByte(); break;
            case asTYPEID_INT16: case asTYPEID_UINT16: bits = ctx->GetReturnWord(); break;
            case asTYPEID_INT32: case asTYPEID_UINT32: bits = ctx->GetReturnDWord(); break;
            case asTYPEID_INT64: case asTYPEID_UINT64: bits = ctx->GetReturnQWord(); break;
            case asTYPEID_FLOAT: { float v = ctx->GetReturnFloat(); uint32_t raw; std::memcpy(&raw,&v,4); bits = raw; break; }
            case asTYPEID_DOUBLE: { double v = ctx->GetReturnDouble(); std::memcpy(&bits,&v,8); break; }
            default: result = 6;
        }
        if (!result) std::cout << bits << '\n';
    }
    ctx->Release(); engine->GarbageCollect(asGC_FULL_CYCLE | asGC_DESTROY_GARBAGE);
    engine->ShutDownAndRelease(); return result;
}

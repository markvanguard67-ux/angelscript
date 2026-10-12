// Test-only oracle using the original C++ engine. Not part of the C# runtime.
#include "angelscript.h"
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>

static void Message(const asSMessageInfo *message, void *)
{
    std::cerr << message->section << '(' << message->row << ',' << message->col << "): " << message->message << '\n';
}

int main(int argc, char **argv)
{
    if (argc != 3) { std::cerr << "Usage: reference-runner source.as 'int test()'\n"; return 2; }
    std::ifstream file(argv[1]);
    if (!file) return 2;
    std::ostringstream source;
    source << file.rdbuf();
    asIScriptEngine *engine = asCreateScriptEngine();
    engine->SetMessageCallback(asFUNCTION(Message), nullptr, asCALL_CDECL);
    asIScriptModule *module = engine->GetModule("test", asGM_ALWAYS_CREATE);
    const std::string code = source.str();
    module->AddScriptSection(argv[1], code.c_str(), code.size());
    if (module->Build() < 0) { engine->ShutDownAndRelease(); return 3; }
    asIScriptFunction *function = module->GetFunctionByDecl(argv[2]);
    if (!function || function->GetParamCount() != 0) { engine->ShutDownAndRelease(); return 4; }
    asIScriptContext *context = engine->CreateContext();
    context->Prepare(function);
    const int status = context->Execute();
    int result = 0;
    if (status != asEXECUTION_FINISHED)
    {
        std::cout << "fault " << (context->GetExceptionString() ? context->GetExceptionString() : "unknown") << '\n';
        result = 5;
    }
    else
    {
        uint64_t bits = 0;
        switch (function->GetReturnTypeId())
        {
            case asTYPEID_VOID: break;
            case asTYPEID_BOOL: case asTYPEID_INT8: case asTYPEID_UINT8: bits = context->GetReturnByte(); break;
            case asTYPEID_INT16: case asTYPEID_UINT16: bits = context->GetReturnWord(); break;
            case asTYPEID_INT32: case asTYPEID_UINT32: bits = context->GetReturnDWord(); break;
            case asTYPEID_INT64: case asTYPEID_UINT64: bits = context->GetReturnQWord(); break;
            case asTYPEID_FLOAT: { const float value = context->GetReturnFloat(); uint32_t raw; std::memcpy(&raw, &value, sizeof(raw)); bits = raw; break; }
            case asTYPEID_DOUBLE: { const double value = context->GetReturnDouble(); std::memcpy(&bits, &value, sizeof(bits)); break; }
            default: result = 6; break;
        }
        if (result == 0) std::cout << bits << '\n';
    }
    context->Release();
    engine->ShutDownAndRelease();
    return result;
}

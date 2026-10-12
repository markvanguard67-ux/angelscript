#!/usr/bin/env python3
"""Generate checked-in portable expectations from the ORIGINAL engine, not the C# port.
No Unity or native plugin is needed to consume the resulting small TextAsset.
"""
import argparse
import hashlib
import json
import re
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PACKAGE = ROOT / 'csharp/UnityValidation'
OUT = PACKAGE / 'Resources/AngelScriptValidation/Corpus.json'


def recipes():
    cases = []
    text = (ROOT / 'csharp/Tests/LanguageCases.cs').read_text()
    pattern = r'new LanguageCase\("([^"\\]*(?:\\.[^"\\]*)*)",\s*"([^"\\]*(?:\\.[^"\\]*)*)",\s*([^\n]+)'
    for name, raw, tail in re.findall(pattern, text):
        # The standalone suite explicitly excludes platform/profile differences and custom math.
        if re.search(r',\s*false\s*[,)]', tail):
            continue
        source = json.loads('"' + raw + '"')
        name = json.loads('"' + name + '"')
        match = re.search(r'\b(void|bool|int8|uint8|int16|uint16|int32|uint32|int64|uint64|int|uint|float|double)\s+test\s*\(', source)
        if not match:
            raise ValueError('No test entry: ' + name)
        cases.append(dict(id='language-' + re.sub(r'[^a-z0-9]+', '-', name.lower()).strip('-'), title=name,
                          category='Language / original engine', phase='Current', feature='primitive-language',
                          source=source, declaration=match[1] + ' test()', profile='core', scenario='Source'))

    def add(id, title, source, type='int', phase='Current', feature='primitive-language', profile='core', scenario='Source'):
        cases.append(dict(id=id, title=title, category='Language / original engine' if phase=='Current' else 'Full-port / ' + feature,
                          phase=phase, feature=feature, source=source, declaration=type+' test()', profile=profile, scenario=scenario))
    # Sample every numeric type in both storage scopes; native results capture narrow promotion too.
    types = ['int8','uint8','int16','uint16','int','uint','int64','uint64','float','double']
    for type in types:
        for scope in ['local','global']:
            for index, expression in enumerate(['a+a++','(a=2)+(a=3)','a==(a=2)','a+(true?++a:b)']):
                result_type = 'bool' if '==' in expression else 'double'
                source = (type+' a=1,b=3;' if scope=='global' else '') + result_type+' test(){' + (type+' a=1,b=3;' if scope=='local' else '')+'return '+expression+';}'
                add('storage-'+type+'-'+scope+'-'+str(index), 'Storage / '+type+' '+scope+' '+expression, source, result_type)
    import random
    rng = random.Random(64137)
    for index in range(32):
        a,b,c = [rng.randrange(-10000,10001) for _ in range(3)]
        source='int test(){int a='+str(a)+',b='+str(b)+',c='+str(c)+';for(int i=0;i<12;i++)a=(a*3+b)^c;return a;}'
        add('seeded-'+str(index), 'Seeded arithmetic / '+str(index), source)
    add('math-double', 'Scalar double math against native generic bindings',
        'double test(){double x=9.0;return sqrt(x)+sin(0.0)+pow(2.0,3.0);}', 'double', profile='math')
    add('math-float', 'Scalar float math against native generic bindings',
        'float test(){float x=9.0f;return sqrt(x)+sin(0.0f)+pow(2.0f,3.0f);}', 'float', profile='math')
    for id, src in [
        ('bad-empty-switch','int test(){switch(0){}return 1;}'),
        ('bad-constant-power','int test(){return 2**31;}'),
        ('bad-zero-power','int test(){return 0**0;}'),
        ('bad-case-declaration','int test(){switch(0){case 0:int n=1;return n;default:return 0;}}'),
        ('bad-const-write','int test(){const int n=1;n++;return n;}'),
        ('bad-return-type','int test(){return true;}'),
        ('bad-void-return','void f(){}void test(){return f();}'),
        ('bad-literal','float test(){return 1f;}'),
        ('bad-unknown-name','int test(){return missing;}'),
        ('bad-bool-conversion','bool test(){return bool(1);}'),
    ]: add(id, 'Compiler rejection: '+id, src, 'void' if id=='bad-void-return' else 'bool' if id=='bad-bool-conversion' else 'float' if id=='bad-literal' else 'int')
    add('runtime-div-zero','Runtime division-by-zero fault','int test(){int zero=0;return 2/zero;}')
    add('runtime-power-overflow','Runtime power overflow fault','int test(){int exponent=31;return 2**exponent;}')
    add('runtime-min-divide','Runtime signed division overflow','int test(){int n=-1;int x=-2147483648;return x/n;}')
    add('gap-overload-valid','Valid native overload must not be rejected',
        'int choose(int8 x){return 1;}int choose(float x){return 2;}int test(){return choose(1);}', phase='Conformance',feature='overload-ranking')
    add('gap-overload-ambiguous','Native ambiguous overload must be rejected',
        'int choose(float x){return 1;}int choose(double x){return 2;}int test(){return choose(1);}', phase='Conformance',feature='overload-ranking')
    add('gap-constant-u64','Native constant uint64 conversion policy',
        'double test(){return double(uint64(0xffffffffffffffff));}', 'double', phase='Conformance',feature='constant-folding')
    add('gap-lvalue','Assignment-produced primitive lvalue',
        'int test(){int n=1;return (n=2)++;}',phase='Conformance',feature='reference-lvalues')
    add('gap-return-flow','Native return-flow acceptance',
        'int test(){if(true)return 1;}',phase='Conformance',feature='return-flow')

    future = [
        ('objects', 'Constructors, fields and methods', 'class Counter{int n;Counter(int value){n=value;}int tick(){return ++n;}}int test(){Counter c(5);return c.tick();}'),
        ('destructors', 'Deterministic local object destruction', 'int destroyed=0;class C{~C(){destroyed++;}}int test(){{C c;}return destroyed;}'),
        ('interfaces', 'Interface handle dispatch', 'interface Tick{int tick();}class C:Tick{int tick(){return 7;}}int test(){C c;Tick@ value=c;return value.tick();}'),
        ('inheritance', 'Virtual base-handle dispatch', 'class Base{int read(){return 1;}}class Child:Base{int read() override{return 2;}}int test(){Child c;Base@ b=c;return b.read();}'),
        ('handles', 'Handle identity and null', 'class C{}int test(){C@ a=C();C@ b=a;return a is b && a !is null ? 1:0;}'),
        ('gc-cycles', 'Cyclic handle graph (behavior + future heap instrumentation)', 'class Node{Node@ next;}int test(){Node@ a=Node();Node@ b=Node();@a.next=b;@b.next=a;return a.next is b?1:0;}'),
        ('references', 'in/out/inout reference parameters', 'void change(const int &in x,int &out y,int &inout z){y=x+1;z+=2;}int test(){int a=3,b=0,c=5;change(a,b,c);return b+c;}'),
        ('arrays', 'Array template, indexing and growth', 'int test(){array<int> xs={1,2,3};xs.insertLast(4);return int(xs.length())+xs[3];}'),
        ('array-bounds', 'Array bounds exception', 'int test(){array<int> xs={1};return xs[2];}'),
        ('strings', 'String value and length', 'int test(){string value="unity";return int(value.length());}'),
        ('unicode-strings', 'UTF-8 string byte behavior', 'int test(){string value="żółw";return int(value.length());}'),
        ('dictionary', 'Registered dictionary template-like API', 'int test(){dictionary data;data.set("score",int64(9));int64 value=0;data.get("score",value);return int(value);}'),
        ('enums', 'Enum declarations and conversions', 'enum State{Idle=1,Running=2}int test(){State state=Running;return int(state);}'),
        ('namespaces', 'Namespace-qualified call', 'namespace N{int twice(int n){return n*2;}}int test(){return N::twice(4);}'),
        ('typedefs', 'Primitive typedef', 'typedef uint64 Key;int test(){Key k=9;return int(k);}'),
        ('auto', 'Inferred local type', 'int test(){auto value=3;return value+2;}'),
        ('funcdefs', 'Function handle call', 'funcdef int Map(int);int add(int x){return x+1;}int test(){Map@ f=@add;return f(4);}'),
        ('default-args', 'Default arguments', 'int add(int x,int y=3){return x+y;}int test(){return add(2);}'),
        ('named-args', 'Named argument binding', 'int add(int x,int y){return x*10+y;}int test(){return add(y:3,x:2);}'),
    ]
    for id,title,source in future: add('future-'+id,title,source,phase='Future',feature=id,profile='addons' if id in {'arrays','array-bounds','strings','unicode-strings','dictionary'} else 'unsafe-references' if id=='references' else 'core')
    for id,title in [
        ('module-imports','Module imports / binding / shared declarations'),
        ('bytecode-serialization','Versioned bytecode save/load and rejection'),
        ('registered-value-types','Host value-type layout/alignment and marshalling'),
        ('registered-objects','Host object ownership, calling conventions and methods'),
        ('gc-accounting','Cycle collection, generations and zero leaked native heap allocations'),
        ('preprocessing','Include/conditional build pipeline and source positions'),
        ('debug-api','Context callbacks, stack traces and debugging API'),
        ('addon-parity','Remaining SDK add-ons and registration API'),
    ]: add('capability-'+id,title,'',phase='Future',feature=id,scenario='Capability')
    ids=[c['id'] for c in cases]
    if len(ids)!=len(set(ids)): raise ValueError('Duplicate case IDs')
    return cases


def canonical(cases):
    return hashlib.sha256(json.dumps(cases,sort_keys=True,separators=(',',':'),ensure_ascii=False).encode()).hexdigest()


def native_hash():
    digest=hashlib.sha256()
    for directory in (ROOT/'sdk/angelscript/include',ROOT/'sdk/angelscript/source',ROOT/'sdk/add_on/scriptarray',ROOT/'sdk/add_on/scriptstdstring',ROOT/'sdk/add_on/scriptdictionary'):
        for file in sorted(directory.glob('*')):
            if file.suffix in {'.cpp','.h','.S'}:digest.update(str(file.relative_to(ROOT)).encode());digest.update(file.read_bytes())
    digest.update((PACKAGE/'Tools~/reference_validation.cpp').read_bytes())
    return digest.hexdigest()


def generate(runner):
    specs=recipes();cases=[]
    with tempfile.TemporaryDirectory() as directory:
        path=Path(directory)/'case.as'
        for spec in specs:
            row=dict(spec)
            if spec['scenario']=='Capability':row.update(expectedOutcome='Capability',expectedBits='',nativeDetail='Needs a C# API driver and native-observer comparison; not a passed source test.')
            else:
                path.write_text(spec['source'],encoding='utf-8')
                result=subprocess.run([str(runner),str(path),spec['declaration'],spec['profile']],capture_output=True,text=True,timeout=15)
                if result.returncode==0:
                    bits=result.stdout.strip()
                    if not bits.isdigit():raise ValueError('Invalid native bits: '+spec['id'])
                    row.update(expectedOutcome='Finished',expectedBits=bits,nativeDetail='')
                elif result.returncode==3:row.update(expectedOutcome='CompileRejected',expectedBits='',nativeDetail=result.stderr.strip())
                elif result.returncode==5:row.update(expectedOutcome='RuntimeFault',expectedBits='',nativeDetail=result.stdout.strip())
                else:raise RuntimeError('Native oracle failure (not a script result): '+spec['id']+' '+str(result.returncode)+' '+result.stderr)
                if spec['phase']=='Future' and row['expectedOutcome']=='CompileRejected':raise RuntimeError('Future fixture must be valid native syntax: '+spec['id']+' '+result.stderr)
            cases.append(row)
    return dict(schemaVersion=1,oracle='Original repository C++ AngelScript engine; generic math bindings; SDK array/string/dictionary add-ons',
                nativeSourceSha256=native_hash(),recipeSha256=canonical(specs),caseCount=len(cases),cases=cases)


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--runner',type=Path,default=PACKAGE/'bin/validation-reference');parser.add_argument('--check',action='store_true');args=parser.parse_args()
    corpus=generate(args.runner.resolve())
    if args.check:
        if not OUT.exists() or json.loads(OUT.read_text())!=corpus:raise SystemExit('Native corpus is stale. Regenerate it and review the changed expectations.')
        print('Native corpus verified:',len(corpus['cases']),'cases; exact schema, provenance and outcomes match.')
    else:
        OUT.write_text(json.dumps(corpus,indent=2,ensure_ascii=False)+'\n');print('Generated',len(corpus['cases']),'native cases at',OUT)

if __name__=='__main__':main()

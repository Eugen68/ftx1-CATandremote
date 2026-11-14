"""Generate CAT constants/parsers and independent test vectors from the Python source.
Usage: python tools/generate_reference.py /path/to/ftx1-pult
Do not run against a different revision without reviewing the resulting changes.
"""
import ast, json, pathlib, re, sys, subprocess
source = pathlib.Path(sys.argv[1]).resolve()
sys.path[:0] = [str(source/'server')]
import cat
root = pathlib.Path(__file__).resolve().parents[1]
js = lambda v: json.dumps(v, ensure_ascii=False)
node = ast.parse((source/'server/cat.py').read_text())
parsers = next(n.value for n in node.body if isinstance(n, ast.Assign) and any(isinstance(t,ast.Name) and t.id=='PARSERS' for t in n.targets))
def expr(n):
 if isinstance(n,ast.Constant): return js(n.value)
 if isinstance(n,ast.Call):
  if isinstance(n.func,ast.Attribute) and n.func.attr=='group': return f'g[{n.args[0].value}]'
  if isinstance(n.func,ast.Name) and n.func.id=='int': return f'I({expr(n.args[0])})'
  if isinstance(n.func,ast.Attribute) and n.func.attr=='get': return n.func.value.id.title()+'.GetValueOrDefault('+', '.join(map(expr,n.args))+')'
 if isinstance(n,ast.Compare): return '('+expr(n.left)+(' != ' if isinstance(n.ops[0],ast.NotEq) else ' == ')+expr(n.comparators[0])+')'
 if isinstance(n,ast.IfExp): return '('+(expr(n.test) if isinstance(n.test,ast.Compare) else '('+expr(n.test)+' != 0)')+' ? '+expr(n.body)+' : '+expr(n.orelse)+')'
 if isinstance(n,ast.UnaryOp): return '-'+expr(n.operand)
 if isinstance(n,ast.BinOp):
  if isinstance(n.op,ast.Mult): return '('+expr(n.left)+' * '+expr(n.right)+')'
  if isinstance(n.op,ast.Mod):
   fmt=n.left.value
   if fmt=='%d.%s': return '$"{I(g[2])}.{g[3]}"'
   return js(fmt.replace('%s',''))+' + '+expr(n.right)
 if isinstance(n,ast.Dict): return 'new Dictionary<string, object> { '+', '.join('['+expr(k)+'] = '+expr(v) for k,v in zip(n.keys,n.values))+' }'
 raise ValueError(ast.dump(n))
lines=['// Ported from ftx1-pult/server/cat.py; preserve rule order.','using System.Globalization;','using System.Text.RegularExpressions;','namespace Ftx1.Core.Cat;','public static class CatProtocol {','static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);']
for name,value,kt,vt in [('Modes',cat.MODES,'string','string'),('Meters',cat.METERS,'int','string')]:
 lines.append(f'public static readonly IReadOnlyDictionary<{kt},{vt}> {name} = new Dictionary<{kt},{vt}> {{ '+', '.join('['+js(k)+'] = '+js(v) for k,v in value.items())+' };')
lines+=['public static readonly IReadOnlyDictionary<string,string> ModeCodes = Modes.ToDictionary(x=>x.Value,x=>x.Key);', 'public static readonly double[] Ctcss = ['+', '.join(map(str,cat.CTCSS))+'];','public static int BandType(long freq) => freq >= 420_000_000 ? 2 : freq >= 144_000_000 ? 1 : 0;', 'public sealed record Parser(string Pattern, Func<string[], Dictionary<string,object>> Read);', 'public static readonly Parser[] Parsers = [']
for rx,fn in [n.elts for n in parsers.elts]:
 pattern=ast.literal_eval(rx)
 if isinstance(fn,ast.Lambda): result=expr(fn.body)
 elif fn.id=='_parse_pc': result='new Dictionary<string,object> { ["pwr_head"] = I(g[1]), ["power"] = double.Parse(g[2], CultureInfo.InvariantCulture) }'
 else: result='new Dictionary<string,object> { ["tuner"] = g[0][2..], ["tuning"] = g[3] != "0" }'
 lines.append('new('+js('^'+pattern+'$')+', g => '+result+'),')
lines+= ['];','static readonly (Regex Rx, Parser Parser)[] Compiled = Parsers.Select(p => (new Regex(p.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), p)).ToArray();', 'public static IReadOnlyDictionary<string,object> ParseFrame(string frame) { try { foreach(var (rx,p) in Compiled) { var m=rx.Match(frame); if(m.Success) return p.Read(m.Groups.Cast<Group>().Select(g=>g.Value).ToArray()); } } catch (Exception e) when(e is FormatException or OverflowException or RegexMatchTimeoutException) {} return new Dictionary<string,object>(); }']
# Query formats are extracted instead of inferred from response regexes.
query=next(n for n in ast.walk(node) if isinstance(n,ast.FunctionDef) and n.name=='_is_query')
noargs=ast.literal_eval(next(n.comparators[0] for n in ast.walk(query) if isinstance(n,ast.Compare) and isinstance(n.ops[0],ast.In)))
lengths=ast.literal_eval(next(n.value for n in query.body if isinstance(n,ast.Assign) and isinstance(n.value,ast.Dict)))
lines+=['static readonly HashSet<string> Queries = ['+', '.join(js(v) for v in noargs)+'];','static readonly Dictionary<string,int> QueryLengths = new() { '+', '.join('['+js(k)+'] = '+str(v) for k,v in lengths.items())+' };','public static bool IsQuery(string command) { var c=command.TrimEnd(\';\'); return Queries.Contains(c) || (c.Length>=2 && QueryLengths.TryGetValue(c[..2],out var n) && c.Length==n); }']
for name in ['FAST_RX','FAST_TX','MEDIUM','SLOW']:
 lines.append('public static readonly string[] '+name.title().replace('_','')+' = ['+', '.join(js(v) for v in getattr(cat,name))+'];')
lines+=['}']
(root/'src/Ftx1.Core/Cat/CatProtocol.cs').write_text('\n'.join(lines)+'\n')
# Each rule gets a valid example, checked by the original parser.
from re import _parser as sre_parse

def sample(tokens):
 out=''
 for op,v in tokens:
  if op==sre_parse.LITERAL: out+=chr(v)
  elif op==sre_parse.SUBPATTERN: out+=sample(v[-1])
  elif op in (sre_parse.MAX_REPEAT,sre_parse.MIN_REPEAT): out+=sample(v[2])*max(1,v[0])
  elif op==sre_parse.IN:
   typ,val=v[0];out+=chr(val) if typ==sre_parse.LITERAL else chr(val[0]) if typ==sre_parse.RANGE else '1'
  elif op==sre_parse.ANY: out+='2'
  elif op==sre_parse.CATEGORY: out+='1'
  elif op==sre_parse.BRANCH: out+=sample(v[1][0])
 return out
cases=[]
for i,(rx,fn) in enumerate(cat.PARSERS):
 frame=sample(sre_parse.parse(rx.pattern))
 if rx.pattern==r'^VE(\d)(.+)$': frame='VE5dev' # fallback version rule, not the preceding numeric version rule
 expected=cat.parse_frame(frame)
 assert expected,(i,frame)
 cases.append({'frame':frame,'expected':expected,'rule':i})
for frame in ['FA014074000','IS01-0100','RI00000001','VE00110','PC15.5','PC2bad','VE101-07','MD0Z','?','FA123','SM0123junk','garbage']:
 cases.append({'frame':frame,'expected':cat.parse_frame(frame),'rule':-1})
(root/'tests/data/parsers.json').write_text(json.dumps(cases,indent=2)+'\n')
class Queue:
 def __init__(self): self.items=[]
 def put_nowait(self,v): self.items.append(v)
r=cat.Rig(None,lambda d:None);r._q=Queue()
params=[]
keys=re.findall(r"(?:el)?if key == '([^']+)'",(source/'server/cat.py').read_text())
for key in dict.fromkeys(['freq_a','freq_b','mode','mode_b']+keys):
 for v in (['USB','CW-L','bad'] if key in ['mode','mode_b'] else [-10000,0,0.5,1,5.5,12.5,128,999999999]):
  for head in ([1,2] if key=='power' else [2]):
   r._q.items=[]
   r.state={'pwr_head':head,'mode':'USB','freq_a':14074000,'clar_tx':1,'clar_rx':0};r._freq_pend={}
   params.append({'key':key,'value':v,'head':head,'commands':r.set_param(key,v),'queued':list(r._q.items)})
(root/'tests/data/parameters.json').write_text(json.dumps(params,indent=2)+'\n')
commit=subprocess.check_output(['git','-C',str(source),'rev-parse','HEAD'],text=True).strip()
(root/'tests/data/source.json').write_text(json.dumps({'repository':'Eugen68/ftx1-pult','commit':commit,'parserRules':len(cat.PARSERS),'parameterCases':len(params)},indent=2)+'\n')
print(len(cat.PARSERS),'parser rules;',len(params),'parameter cases;',commit)
# Exact simulator seed and Morse duration lookup (audio synthesis is Stage 2).
import sim, cw
radio=sim.SimRadio()
seed={'state':radio.s,'memories':radio.mem,'morse':cw.ENCODE}
(root/'src/Ftx1.Sim/seed.json').write_text(json.dumps(seed,indent=2)+'\n')
trace=[]
for command in ['ID','VE0','VE5','FA','FB','MD0','PC','AG0128','AG0','PC15.5','PC','BS013','FA','AB','FB','SV','FA','FB','EX010101','EX010101+0012','EX010101','MR00001','MT00001','MZ00001','MC000002','MC0','VM0','CN00','AM','MZ00002','MT00002','MW00009014074000-01001021002','MR00009','MT00009HELLO','MT00009','MZ000091145600000','MZ00009','TX1','TX','KY00','TX','VM000','VM0','BOGUS']:
 trace.append({'command':command,'reply':radio.handle(command)})
(root/'tests/data/simulator.json').write_text(json.dumps(trace,indent=2)+'\n')

actions=[]
for name,arg in [('a_to_b',None),('b_to_a',None),('swap',None),('band_up',None),('band_down',None),('up',None),('down',None),('tune',None),('tune_ext',None),('tune_stop',None),('atas_tune',None),('atas_up',None),('atas_down',None),('atas_stop',None),('zero_in',None),('spot_on',None),('spot_off',None),('scan_up',None),('scan_down',None),('scan_stop',None),('vfo_mode',None),('mem_mode',None),('qmb_store',None),('qmb_recall',None),('clar_clear',None),('band',13),('cw_play',5),('cw_store',[5,'CQ de yo9abc;!?'] ),('voice_play',2),('mem_ch',100),('power_off',None),('unknown',None)]:
 r.state={'mem_ch':2};actions.append({'name':name,'arg':arg,'commands':r.action(name,arg)})
(root/'tests/data/actions.json').write_text(json.dumps(actions,indent=2)+'\n')
queries=[{'command':c+';','query':True} for c in noargs]+[{'command':prefix+'0'*(n-2)+';','query':True} for prefix,n in lengths.items()]
queries += [{'command':item['command'][:-1]+'9;','query':cat.Rig._is_query(item['command'][:-1]+'9;')} for item in list(queries)]
(root/'tests/data/queries.json').write_text(json.dumps(queries,indent=2)+'\n')

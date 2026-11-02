"""Original Covenant diagram artwork. Generates editable Draw.io XML and matching SVG.
Standard library only. Edit coordinates/content here, then run this script.
"""
from pathlib import Path
from xml.etree.ElementTree import Element, SubElement, tostring
from html import escape

OUT=Path(__file__).parent
P={'blue':('#EFF6FF','#2563EB'),'teal':('#ECFDF5','#078568'),'purple':('#F5F3FF','#7653C7'),'amber':('#FFFBEB','#B77912'),'red':('#FFF1F2','#C0394D'),'slate':('#F1F5F9','#64748B')}
class Diagram:
 def __init__(self,name,w,h,title,subtitle):
  self.name,self.w,self.h=name,w,h
  self.xml=Element('mxfile',host='app.diagrams.net',version='26.0.0')
  dia=SubElement(self.xml,'diagram',id=name,name=title)
  model=SubElement(dia,'mxGraphModel',dx=str(w),dy=str(h),grid='1',gridSize='10',page='1',pageScale='1',pageWidth=str(w),pageHeight=str(h),background='#FFFFFF')
  self.root=SubElement(model,'root');SubElement(self.root,'mxCell',id='0');SubElement(self.root,'mxCell',id='1',parent='0');self.n=0
  self.svg=[f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" role="img" aria-label="{escape(title)}"><defs><marker id="arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0 0 L8 4 L0 8 Z" fill="#64748B"/></marker></defs><rect width="100%" height="100%" fill="white"/>']
  self.text(40,32,w-80,42,title,30,'#14233B',True)
  self.text(40,80,w-80,28,subtitle,17,'#54647B')
 def cell(self,value,style,x,y,w,h):
  self.n+=1;c=SubElement(self.root,'mxCell',id=str(self.n+1),value=value,style=style,vertex='1',parent='1');SubElement(c,'mxGeometry',x=str(x),y=str(y),width=str(w),height=str(h),attrib={'as':'geometry'});return str(self.n+1)
 def text(self,x,y,w,h,s,size=16,color='#23354D',bold=False,center=False):
  self.cell(s,f'text;html=0;whiteSpace=wrap;align={"center" if center else "left"};verticalAlign=middle;fontFamily=Arial;fontSize={size};fontColor={color};fontStyle={1 if bold else 0};spacing=0;',x,y,w,h)
  self.svg.append(f'<text x="{x+w/2 if center else x}" y="{y+h/2}" dominant-baseline="central" text-anchor="{"middle" if center else "start"}" font-family="Arial,Helvetica,sans-serif" font-size="{size}" font-weight="{700 if bold else 400}" fill="{color}">{escape(s)}</text>')
 def rect(self,x,y,w,h,fill,stroke,rounded=True,dash=False):
  self.cell('',f'rounded={1 if rounded else 0};arcSize=10;fillColor={fill};strokeColor={stroke};strokeWidth=1.5;dashed={1 if dash else 0};',x,y,w,h)
  self.svg.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{12 if rounded else 0}" fill="{fill}" stroke="{stroke}" stroke-width="1.5"'+(' stroke-dasharray="6 5"' if dash else '')+'/>')
 def card(self,x,y,w,h,title,lines=(),tone='blue',num=None):
  fill,color=P[tone];self.rect(x,y,w,h,fill,color)
  off=20
  if num:
   self.rect(x+16,y+16,32,32,color,color);self.text(x+16,y+16,32,32,str(num),18,'#FFFFFF',True,True);off=60
  self.text(x+off,y+15,w-off-16,30,title,21,color,True)
  for i,s in enumerate(lines):self.text(x+20,y+54+24*i,w-40,24,s,16)
 def edge(self,points,label=None,lx=0,ly=0,lw=200,dash=False):
  self.n+=1;c=SubElement(self.root,'mxCell',id=str(self.n+1),edge='1',parent='1',style=f'endArrow=block;endFill=1;rounded=0;strokeColor=#64748B;strokeWidth=2;dashed={1 if dash else 0};')
  g=SubElement(c,'mxGeometry',relative='1',attrib={'as':'geometry'})
  SubElement(g,'mxPoint',x=str(points[0][0]),y=str(points[0][1]),attrib={'as':'sourcePoint'});SubElement(g,'mxPoint',x=str(points[-1][0]),y=str(points[-1][1]),attrib={'as':'targetPoint'})
  if len(points)>2:
   a=SubElement(g,'Array',attrib={'as':'points'})
   for x,y in points[1:-1]:SubElement(a,'mxPoint',x=str(x),y=str(y))
  self.svg.append('<polyline points="'+' '.join(f'{x},{y}' for x,y in points)+'" fill="none" stroke="#64748B" stroke-width="2" marker-end="url(#arrow)"'+(' stroke-dasharray="6 5"' if dash else '')+'/>')
  if label:self.text(lx,ly,lw,24,label,14,'#54647B',False,True)
 def save(self):
  (OUT/(self.name+'.drawio')).write_bytes(tostring(self.xml,encoding='utf-8',xml_declaration=True))
  (OUT/(self.name+'.svg')).write_text('\n'.join(self.svg+['</svg>']))

# Overview: a small number of purposeful blocks, with a visible trust boundary.
d=Diagram('covenant-overview',1440,760,'Covenant at a glance','One governed entry point between your applications and approved AI services.')
d.rect(32,128,900,490,'#FBFCFE','#CCD6E3',dash=True);d.text(52,140,820,28,'YOUR ORGANIZATION',15,'#54647B',True)
d.rect(964,128,444,490,'#FCFAFF','#D9D0EE',dash=True);d.text(984,140,400,28,'APPROVED EXTERNAL SERVICES',15,'#7653C7',True)
d.card(64,236,220,126,'Applications',['App identity','Owning team'], 'blue')
d.card(340,224,290,150,'Covenant',['Inspect data and apply rules','Choose an approved route','Check spend and record proof'],'amber')
d.card(692,250,208,102,'Local models',['Ollama / Qwen'],'teal')
d.card(1000,236,372,126,'Cloud models',['Anthropic / OpenAI','Only when policy permits'],'purple')
d.card(64,468,220,102,'Admin console',['Rules, routes and budgets'],'blue')
d.card(340,468,290,102,'Evidence and costs',['Every outcome, including refusals'],'slate')
d.card(1000,468,372,102,'Web search',['Tavily, for approved Public work'],'purple')
d.edge([(284,299),(340,299)])
d.edge([(630,299),(692,299)])
d.edge([(610,224),(610,195),(1186,195),(1186,236)],'Approved cloud route',800,169,235)
d.edge([(630,344),(658,344),(658,408),(1186,408),(1186,468)],'Optional external search',810,380,235)
d.edge([(174,468),(174,415),(410,415),(410,374)],dash=True)
d.edge([(518,374),(518,468)],'All outcomes',530,414,130)
d.text(48,646,1344,28,'LOCAL  Data stays inside     |     EXTERNAL  Data leaves only through an approved route     |     EVIDENCE  Who, what, where and cost',16,'#23354D',True)
d.text(48,694,1344,24,'Direct access to a chatbot bypasses Covenant. Use network and endpoint controls to require this path.',16,'#54647B')
d.save()

# Detail: one downward spine, branches kept outside the reading path.
d=Diagram('covenant-architecture',1440,1240,'How a request is governed','Read top to bottom. A refusal stops further work and still creates an audit record.')
d.text(48,126,320,28,'INPUT AND CONTROL',15,'#54647B',True)
d.text(480,126,480,28,'REQUEST PIPELINE',15,'#54647B',True)
d.text(1060,126,330,28,'DESTINATIONS AND RESULTS',15,'#54647B',True)
steps=[('Identify the caller',['Authenticate the application; bind its team.','Validate the request format.'],'blue'),('Inspect the content',['Combine the workload label with detection.','Incomplete inspection means refuse.'],'amber'),('Apply the policy',['Enforce sensitive-data handling and routes.','Sanitize only an approved structured record.'],'amber'),('Check the exact cache',['Reuse only an eligible, matching response.','A cache hit avoids a new model call.'],'blue'),('Check limits and retrieve',['Check rate, pause and budget reservation.','Optional Public web search is governed too.'],'amber'),('Call an approved model',['Use the selected local or external adapter.','Fallback stays within the approved routes.'],'teal'),('Finalize and record',['Settle usage and attribute cost to the team.','Record the outcome and return the response.'],'slate')]
for i,(title,lines,tone) in enumerate(steps):
 y=172+i*134;d.card(480,y,480,108,title,lines,tone,str(i+1))
 if i<6:d.edge([(720,y+108),(720,y+134)])
d.card(48,172,340,108,'Application request',['OpenAI / Anthropic compatible API','Application key identifies the caller'],'blue')
d.edge([(388,226),(480,226)])
d.card(48,332,340,156,'Sensitive data rules',['Internal: approved local processing','Public demo: reject detected PII/PHI','Detection can raise the data label.','No match is not proof of safe data.'],'red')
d.card(48,550,340,132,'Configuration',['Profiles, routes and model allow-list','Budgets and permission settings','Provider secrets stay on the server.'],'slate')
d.edge([(388,603),(426,603),(426,494),(480,494)],dash=True)
d.card(48,758,340,156,'Refused or failed',['Record the reason and any usage.','Do not continue to later stages.','Earlier tool or model calls may','already have incurred a charge.'],'red')
d.card(1060,574,332,108,'Exact cache hit',['Skip retrieval and model execution.','Still record and attribute the request.'],'blue')
d.edge([(960,628),(1060,628)])
d.edge([(1392,628),(1420,628),(1420,1118),(1000,1118),(1000,1030),(960,1030)],dash=True)
d.card(1060,730,332,108,'Tavily web search',['Public only, explicitly enabled','Sources and cost recorded separately'],'purple')
d.edge([(960,762),(1010,762),(1010,784),(1060,784)])
d.card(1060,870,332,108,'Approved models',['Local: Ollama / Qwen','External: Anthropic / OpenAI'],'teal')
d.edge([(960,896),(1010,896),(1010,924),(1060,924)])
d.card(48,1010,340,108,'Audit and budget ledger',['Hash-linked request evidence','Costs by application and team'],'slate')
d.edge([(480,1030),(428,1030),(428,1064),(388,1064)])
# Cache shortcut terminates at finalization, not an unimplemented provider stage.
d.text(48,1160,1344,28,'DESIGN PATTERNS  Canonical proxy  •  Policy middleware  •  Provider adapters  •  Exact cache  •  Governed retrieval  •  Audit trail',16,'#23354D',True)
d.text(48,1200,1344,24,'Sensitive-data inspection is bounded and heuristic. Sanitization supports a narrow schema, not arbitrary prose or PHI.',16,'#54647B')
d.save()

# Use Draw.io itself for final SVG exports, so wrapping matches the editable source.
# The built-in SVG is a portable preview when the desktop exporter is unavailable.
if __name__ == '__main__':
 import os, shutil, subprocess
 exporter=os.environ.get('DRAWIO') or shutil.which('drawio')
 mac='/Applications/draw.io.app/Contents/MacOS/draw.io'
 if not exporter and Path(mac).exists(): exporter=mac
 if exporter:
  for name in ('covenant-overview','covenant-architecture'):
   for fmt in ('svg','png'):
    subprocess.run([exporter,'--export','--format',fmt,'--theme','light','--size','page','--embed-diagram','--output',str(OUT/(name+'.'+fmt)),str(OUT/(name+'.drawio'))],check=True,timeout=90)
 else:
  print('Draw.io not installed: generated portable SVG previews. Export .drawio files with Draw.io before publishing.')

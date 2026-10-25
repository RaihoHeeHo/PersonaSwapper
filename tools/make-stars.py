# Generates PersonaSwapper.App/Assets/stars-tile.png, a seamless tile of nested stars (original artwork).
# Usage: python3 tools/make-stars.py PersonaSwapper.App/Assets/stars-tile.png 11
import math, random, sys
from PIL import Image, ImageDraw
random.seed(int(sys.argv[2]) if len(sys.argv)>2 else 5)
T=800; SC=2; W=T*SC
RED=(200,16,30); DARK=(110,6,14); BLACK=(10,8,9); WHITE=(244,242,240)
img=Image.new('RGB',(W,W),DARK); d=ImageDraw.Draw(img)
def star(cx,cy,r,rot,fill):
    pts=[]
    for i in range(10):
        a=rot+math.pi*i/5 - math.pi/2
        rr=r if i%2==0 else r*0.45
        pts.append((cx+rr*math.cos(a), cy+rr*math.sin(a)))
    for ox in (-W,0,W):
        for oy in (-W,0,W):
            d.polygon([(x+ox,y+oy) for x,y in pts], fill=fill)
def nested(cx,cy,r,rot,scheme):
    rings=int(random.choice([3,4]))
    cols = [RED,BLACK] if scheme=='red' else ([WHITE,BLACK] if scheme=='white' else [BLACK,RED])
    # black outline first
    star(cx,cy,r*1.08,rot,BLACK)
    for k in range(rings*2):
        rr=r*(1-k/(rings*2))
        if rr<r*0.12: break
        star(cx,cy,rr,rot+random.uniform(-0.03,0.03),cols[k%2])
    if scheme=='white': star(cx,cy,r*0.18,rot,WHITE)
items=[]
for n in range(46):
    r=random.choice([random.uniform(60,110),random.uniform(110,190),random.uniform(190,300)])*SC
    scheme=random.choices(['red','white','dark'],[0.7,0.1,0.2])[0]
    if scheme=='white': r=min(r,150*SC)
    items.append((scheme,r,random.uniform(0,W),random.uniform(0,W),random.uniform(0,2*math.pi)))
order={'dark':0,'red':1,'white':2}
items.sort(key=lambda t:(order[t[0]], -t[1]))
for scheme,r,cx,cy,rot in items: nested(cx,cy,r,rot,scheme)
img=img.resize((T,T),Image.LANCZOS)
img.save(sys.argv[1])

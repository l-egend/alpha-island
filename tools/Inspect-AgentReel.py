import argparse
import json
import math
import sys
from pathlib import Path
from PIL import Image, ImageDraw

parser=argparse.ArgumentParser()
parser.add_argument('video')
parser.add_argument('output')
args=parser.parse_args()
sys.path.insert(0,str(Path('bin/MediaTools/python').resolve()))
import imageio_ffmpeg
reader=imageio_ffmpeg.read_frames(args.video,pix_fmt='rgb24')
metadata=next(reader)
output=Path(args.output);output.mkdir(parents=True,exist_ok=True)
frames=[];next_time=0
for index,raw in enumerate(reader):
    t=index/metadata['fps']
    if t+1e-6<next_time:continue
    frame=Image.frombytes('RGB',tuple(metadata['size']),raw)
    frame.thumbnail((384,240))
    frames.append((t,frame.copy()))
    next_time+=.5
sheet=Image.new('RGB',(4*394,math.ceil(len(frames)/4)*260),'#252525')
draw=ImageDraw.Draw(sheet)
for n,(time,frame) in enumerate(frames):
    x=n%4*394;y=n//4*260
    sheet.paste(frame,(x+(394-frame.width)//2,y+24))
    draw.text((x+10,y+5),f'{time:.3f}s',fill='white')
sheet.save(output/'contact-sheet.jpg',quality=93)
(output/'metadata.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata,indent=2))

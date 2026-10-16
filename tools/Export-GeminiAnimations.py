"""Split user supplied agent reels; assets contain no audio or local paths."""
import argparse
import json
import math
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--video', required=True)
    parser.add_argument('--reference', required=True)
    parser.add_argument('--ffmpeg-python', required=True)
    parser.add_argument('--assets', default='Assets/Agents/Gemini')
    parser.add_argument('--exports', default='bin/AgentAnimationExports/Gemini')
    parser.add_argument('--family', default='gemini', choices=['gemini', 'codex', 'deepseek', 'claude'])
    parser.add_argument('--clip-ranges', default='')
    parser.add_argument('--content-bottom', type=int, default=0)
    parser.add_argument('--caption-masks', default='', help='JSON frame intervals with caption-only rectangles')
    args = parser.parse_args()
    sys.path.insert(0, args.ffmpeg_python)
    import imageio_ffmpeg

    assets, exports = Path(args.assets), Path(args.exports)
    assets.mkdir(parents=True, exist_ok=True)
    exports.mkdir(parents=True, exist_ok=True)
    # Frame boundaries exclude the transition from holding a permission card to error.
    clips = [('idle', 0, 72), ('thinking', 72, 120), ('streaming', 120, 167),
             ('tool-wait', 168, 198), ('error', 208, 240)]
    if args.clip_ranges:
        clips = [tuple(c) for c in json.loads(Path(args.clip_ranges).read_text(encoding='utf-8-sig'))]
    if [c[0] for c in clips] != ['idle', 'thinking', 'streaming', 'tool-wait', 'error']:
        raise ValueError('Exactly five known states are required')
    masks = json.loads(Path(args.caption_masks).read_text(encoding='utf-8-sig')) if args.caption_masks else []
    reader = imageio_ffmpeg.read_frames(args.video, pix_fmt='rgb24')
    metadata = next(reader)
    for mask in masks:
        x0, y0, x1, y1 = mask['rect']
        if not (0 <= mask['first'] < mask['last'] and 0 <= x0 < x1 <= metadata['size'][0]
                and 0 <= y0 < y1 <= metadata['size'][1]):
            raise ValueError('Invalid caption mask')
    frames = []
    bbox = None
    for index, raw in enumerate(reader):
        rgb = Image.frombytes('RGB', tuple(metadata['size']), raw)
        for mask in masks:
            if mask['first'] <= index < mask['last']:
                x0, y0, x1, y1 = mask['rect']
                ImageDraw.Draw(rgb).rectangle((x0, y0, x1 - 1, y1 - 1), fill='white')
        if args.content_bottom:
            rgb = rgb.crop((0, 0, rgb.width, args.content_bottom))
        rgb = rgb.resize((640, round(rgb.height * 640 / rgb.width)), Image.Resampling.LANCZOS)
        array = np.asarray(rgb).astype(np.float32)
        white = ((array.min(axis=2) > 239) & (array.max(axis=2) - array.min(axis=2) < 15)).astype(np.uint8)
        flood = Image.fromarray(white, 'L').copy()
        ImageDraw.floodfill(flood, (0, 0), 128)
        bg = np.asarray(flood) == 128
        opaque = Image.fromarray((~bg).astype(np.uint8) * 255, 'L')
        alpha = opaque.filter(ImageFilter.GaussianBlur(.4))
        a = np.asarray(alpha).astype(np.float32) / 255
        # Remove white fringe from partial alpha without keying the enclosed eye/clothes highlights.
        color = np.clip((array - (1 - a[..., None]) * 255) / np.maximum(a[..., None], .01), 0, 255).astype(np.uint8)
        color[a == 0] = 0
        rgba = Image.fromarray(np.dstack((color, np.asarray(alpha))), 'RGBA')
        b = opaque.getbbox()
        if b and (not masks or any(first <= index < last for _, first, last in clips)):
            bbox = b if bbox is None else (min(bbox[0], b[0]), min(bbox[1], b[1]), max(bbox[2], b[2]), max(bbox[3], b[3]))
        frames.append(rgba)
    if bbox is None or len(frames) < max(c[2] for c in clips):
        raise RuntimeError('The input did not contain the expected five animations')
    # All clips share one anchor/crop: state changes cannot jump the character around.
    x0, y0, x1, y1 = bbox
    crop = (max(0, x0 - 3), max(0, y0 - 3), min(frames[0].width, x1 + 3), min(frames[0].height, y1 + 3))
    size, columns, fps = 192, 8, 12
    normalized = []
    for frame in frames:
        cut = frame.crop(crop)
        cut.thumbnail((size - 8, size - 8), Image.Resampling.LANCZOS)
        canvas = Image.new('RGBA', (size, size))
        canvas.alpha_composite(cut, ((size - cut.width) // 2, (size - cut.height) // 2))
        normalized.append(canvas)
    original = Image.open(args.reference).convert('RGBA')
    original = original.crop(original.getbbox())
    original.thumbnail((size - 8, size - 8), Image.Resampling.LANCZOS)
    fallback = Image.new('RGBA', (size, size))
    fallback.alpha_composite(original, ((size - original.width) // 2, (size - original.height) // 2))
    fallback.save(assets / 'static.png', optimize=True)
    if metadata['fps'] != 24:
        raise ValueError('This exporter expects 24 fps source reels')
    manifest = {'schema': 1, 'family': args.family, 'frameSize': size, 'columns': columns, 'fps': fps, 'clips': []}
    preview = Image.new('RGB', (5 * 230, 320), '#191526')
    draw = ImageDraw.Draw(preview)
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    for index, (state, first, last) in enumerate(clips):
        selected = normalized[first:last:2]
        sheet = Image.new('RGBA', (size * columns, size * math.ceil(len(selected) / columns)))
        for n, frame in enumerate(selected):
            sheet.alpha_composite(frame, ((n % columns) * size, (n // columns) * size))
        sheet.save(assets / (state + '.png'), optimize=True)
        manifest['clips'].append({'state': state, 'file': state + '.png', 'frames': len(selected)})
        # APNG retains alpha and has no audio container; MP4 preserves the original action for editing.
        selected[0].save(exports / (state + '.png'), save_all=True, append_images=selected[1:], duration=1000 / fps, loop=0, disposal=0, blend=0)
        masking = [f"drawbox=x={m['rect'][0]}:y={m['rect'][1]}:w={m['rect'][2]-m['rect'][0]}:h={m['rect'][3]-m['rect'][1]}:color=white:t=fill:enable='between(n,{m['first']},{m['last']-1})'"
                   for m in masks if m['first'] < last and m['last'] > first]
        filters = ','.join(masking + [f'trim=start_frame={first}:end_frame={last}', 'setpts=PTS-STARTPTS'])
        if args.content_bottom:
            filters += f',crop=iw:{args.content_bottom}:0:0'
        command = [ffmpeg, '-hide_banner', '-loglevel', 'error', '-y', '-i', args.video,
                   '-vf', filters,
                   '-an', '-c:v', 'libx264', '-crf', '18', '-pix_fmt', 'yuv420p', '-movflags', '+faststart',
                   str(exports / (state + '.mp4'))]
        subprocess.run(command, check=True)
        info = subprocess.run([ffmpeg, '-hide_banner', '-i', str(exports / (state + '.mp4'))], capture_output=True, text=True)
        if 'Audio:' in info.stderr or 'Video:' not in info.stderr:
            raise RuntimeError('Export audio/video verification failed: ' + state)
        draw.text((index * 230 + 12, 12), state, fill='white')
        preview.paste(selected[len(selected)//2], (index * 230 + 19, 42), selected[len(selected)//2])
        for k, s in enumerate((24, 32, 48)):
            tiny = selected[len(selected)//2].resize((s, s), Image.Resampling.LANCZOS)
            preview.paste(tiny, (index * 230 + 18 + k * 68, 253), tiny)
            draw.text((index * 230 + 18 + k * 68, 305), str(s), fill='white')
    (assets / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    preview.save(exports / 'preview.png')
    label = {'codex': 'GPT / Codex', 'gemini': 'Gemini / Antigravity', 'deepseek': 'DS / DeepSeek Harness', 'claude': 'Claude / Claude Code'}[args.family]
    (exports / 'README.txt').write_text(label + '\n\nidle: 空闲\nthinking: 思考\nstreaming: 输出\ntool-wait: 等待授权\nerror: 出错\n\nMP4：独立原始动作片段，全部已剔除声音。\nPNG：透明背景的循环 APNG，可用于支持 APNG 的预览或后期制作。\n安装包内使用共享 PNG 帧，不启动媒体播放器。\n', encoding='utf-8-sig')
    print(json.dumps({'frames': len(frames), 'crop': crop, 'clips': manifest['clips'], 'audioStreams': 0}, indent=2))


if __name__ == '__main__':
    main()

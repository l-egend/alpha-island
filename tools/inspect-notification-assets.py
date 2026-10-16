"""Read-only diagnosis, restricted to the two notification senders reported by the user."""
import json, os, pathlib, sqlite3, urllib.parse, xml.etree.ElementTree as ET

db = pathlib.Path(os.environ['LOCALAPPDATA']) / 'Microsoft/Windows/Notifications/wpndatabase.db'
with sqlite3.connect(db.as_uri() + '?mode=ro', uri=True) as conn:
    rows = conn.execute('SELECT n.Id, h.PrimaryId, n.Payload FROM Notification n JOIN NotificationHandler h ON h.RecordId=n.HandlerId ORDER BY n.Id DESC LIMIT 150').fetchall()
    found = 0
    for notification_id, app_id, payload in rows:
        if isinstance(payload, bytes):
            payload = payload.decode('utf-16' if payload[:2] in (b'\xff\xfe', b'\xfe\xff') or payload[1:2] == b'\0' else 'utf-8-sig', errors='replace')
        try:
            root = ET.fromstring(payload)
        except (ET.ParseError, TypeError):
            continue
        texts = [e.text or '' for e in root.iter('text')]
        if not ('python' in app_id.lower() or 'battery' in app_id.lower() or any(t == 'Game Exited' or 'OPPO Enco Clip2' in t for t in texts)):
            continue
        found += 1
        images = []
        for img in root.iter('image'):
            source = img.get('src', '')
            parsed = urllib.parse.urlparse(source)
            path = urllib.parse.unquote(parsed.path).lstrip('/') if parsed.scheme == 'file' else source
            images.append({'placement': img.get('placement', 'inline'), 'source': source, 'exists': pathlib.Path(path).is_file() if parsed.scheme in ('', 'file') else None})
        print(json.dumps({'id': notification_id, 'app': app_id, 'image_count': len(images), 'images': images}, ensure_ascii=False))
    print(json.dumps({'matching_notifications': found}))

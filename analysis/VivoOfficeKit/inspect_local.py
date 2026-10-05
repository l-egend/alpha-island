"""Read local vivo metadata without exposing account, address or token values."""
import argparse
import json
import pathlib
import re
import sqlite3
import struct
import sys
import urllib.parse

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ARCHIVE = pathlib.Path(r"D:\vivo\pcsuite\resources\app.asar")

def archive_index():
    with ARCHIVE.open("rb") as stream:
        prefix = stream.read(16)
        _, header_size, _, json_size = struct.unpack("<4I", prefix)
        header = json.loads(stream.read(json_size))
    files = {}
    def visit(nodes, parent=""):
        for name, entry in nodes.items():
            path = parent + name
            if "files" in entry:
                visit(entry["files"], path + "/")
            else:
                files[path] = entry
    visit(header["files"])
    return 8 + header_size, files

def read_entry(path):
    base, files = archive_index()
    entry = files[path]
    if entry.get("unpacked"):
        return pathlib.Path(str(ARCHIVE) + ".unpacked").joinpath(path).read_bytes()
    with ARCHIVE.open("rb") as stream:
        stream.seek(base + int(entry["offset"]))
        return stream.read(entry["size"])

def schema(value, prefix="", depth=2):
    if isinstance(value, dict):
        for key, child in value.items():
            label = prefix + key
            print(f"{label}: {type(child).__name__}" + (f" ({len(child)})" if isinstance(child, (dict, list)) else ""))
            if depth and isinstance(child, dict):
                schema(child, label + ".", depth - 1)
            elif depth and isinstance(child, list) and child and isinstance(child[0], dict):
                schema(child[0], label + "[].", depth - 1)

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["index", "entry", "schema", "facts", "find", "logs", "config", "db"])
    parser.add_argument("target", nargs="?")
    args = parser.parse_args()
    if args.mode == "index":
        base, entries = archive_index()
        print("archive entries:", len(entries), "data offset:", base)
        for name, entry in entries.items():
            if "node_modules/" not in name and (name.endswith((".js", ".json", ".map")) or name.count("/") < 2):
                print(name, entry.get("size", 0))
    elif args.mode == "entry":
        print(read_entry(args.target).decode("utf-8", errors="replace"))
    elif args.mode == "schema":
        schema(json.loads(pathlib.Path(args.target).read_text(encoding="utf-8-sig")))
    elif args.mode == "db":
        uri = "file:" + urllib.parse.quote(pathlib.Path(args.target).as_posix(), safe="/:") + "?mode=ro"
        with sqlite3.connect(uri, uri=True, timeout=2) as connection:
            connection.execute("PRAGMA query_only=ON")
            for (table,) in connection.execute("SELECT name FROM sqlite_master WHERE type='table'"):
                columns = list(connection.execute("SELECT name,type FROM pragma_table_info(?)", (table,)))
                quoted = '"' + table.replace('"', '""') + '"'
                count = connection.execute("SELECT COUNT(*) FROM " + quoted).fetchone()[0]
                print(table, "rows", count, "columns", columns)
    elif args.mode == "find":
        base, entries = archive_index()
        pattern = re.compile(args.target, re.IGNORECASE)
        with ARCHIVE.open("rb") as stream:
            for name, entry in entries.items():
                if not name.endswith(".js") or "node_modules/" in name or "locales-" in name or entry.get("unpacked"):
                    continue
                stream.seek(base + int(entry["offset"]))
                source = stream.read(entry["size"]).decode("utf-8", errors="replace")
                matches = list(pattern.finditer(source))
                if matches:
                    print("FILE", name, "matches", len(matches))
                    for match in matches[:4]:
                        print(source[max(0, match.start()-100):match.end()+180])
    elif args.mode == "logs":
        path = pathlib.Path(args.target)
        pattern = re.compile(r"battery|powerlevel|charg(?:ing|e)|transferprogress|phoneState|connectState|callState|onNotification|notificationReceived|progress", re.IGNORECASE)
        counts = {}
        fields = set()
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            matches = list(pattern.finditer(line))
            if not matches: continue
            for match in matches:
                key = match.group(0).lower()
                counts[key] = counts.get(key, 0) + 1
            fields.update(re.findall(r'"([a-zA-Z][a-zA-Z_0-9]*)"\s*:', line))
        print("LOG", path.name, "keyword counts", json.dumps(counts))
        print("Observed field names:", ", ".join(sorted(fields)))
    elif args.mode == "config":
        data = json.loads(pathlib.Path(args.target).read_text(encoding="utf-8-sig"))
        for key in ["appVersion", "hotVersion", "currentModuleName", "getConnectedDeviceIdList", "getConnectedDeviceBleIdList", "getConnectedDeviceInfoList", "deviceBackup", "backupDir", "fm", "netWork", "syncStatusReportLastTime", "notifyOn"]:
            if key not in data: continue
            value = data[key]
            if key in ["appVersion", "hotVersion", "currentModuleName", "notifyOn"]:
                print(key, ":", value)
            else:
                print(key, type(value).__name__, "length", len(value) if isinstance(value, (dict, list, str)) else "n/a")
                if isinstance(value, dict): schema(value, key + ".", 0)
                elif isinstance(value, list) and value and isinstance(value[0], dict): schema(value[0], key + "[].", 0)
    elif args.mode == "facts":
        data = json.loads(pathlib.Path(args.target).read_text(encoding="utf-8-sig"))
        phone = data.get("last_time_phone", {})
        print("live phone count:", len(data.get("phone", [])))
        for key in ["brand", "model", "deviceType", "androidVersion", "OS_Version", "ROMVersion", "systemVersion", "versionCode", "connectFlag", "connectState", "otherConnectType", "isWifi", "isWifiP2P", "isNearConnected", "lastSuccessTime", "totalStorage", "availableStorage", "availableByte", "widthPixels", "heightPixels", "isSupportBackup", "isSupportCall", "isSupportPCMode", "fileManageAuth", "isConnectAlbum", "hasAlbumBackupInfo", "isLogin", "isOpenIdSame", "systemProperties"]:
            if key in phone:
                value = phone[key]
                if key == "systemProperties":
                    print(key, ":", type(value).__name__)
                    if isinstance(value, dict): schema(value, key + ".", 0)
                else: print(key, ":", value)

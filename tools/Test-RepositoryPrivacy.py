"""Read-only source/privacy audit. Reports locations and categories, never matched text."""
import argparse
import collections
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

TEXT = {'.cs', '.xaml', '.json', '.jsonl', '.md', '.txt', '.ps1', '.cmd', '.bat',
        '.py', '.js', '.mjs', '.ts', '.yml', '.yaml', '.xml', '.config', '.props',
        '.targets', '.csproj', '.sln', '.iss', '.cpp', '.h', '.hpp', '.html', '.svg'}
RULES = {
    'private-key': re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'),
    'provider-token': re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|sk-(?:proj-)?[A-Za-z0-9_-]{24,}|xox[baprs]-[A-Za-z0-9-]{25,}|AKIA[0-9A-Z]{16})\b'),
    'credential-url': re.compile(r'https?://[^\s/"<>:@]{1,80}:[^\s/"<>@]{3,100}@'),
    'literal-secret': re.compile(r'(?i)["\']?(?:api[_-]?key|access[_-]?token|client[_-]?secret|password)["\']?\s*[:=]\s*["\']([^"\'\r\n]{16,160})["\']'),
    'personal-windows-path': re.compile(r'(?i)[A-Z]:[\\/]+Users[\\/]+(?!Public\b|Default\b|<|\{|\$|\*|user\b|test\b|example\b|alice\b|bob\b)[^\\/\s"<>:]+'),
    'email-address': re.compile(r'\b[A-Za-z0-9_.+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b'),
}
SENSITIVE = re.compile(r'(?i)^(?:options\.json(?:\.bak)?|secrets\.(?:dat|json)|auth\.json|credentials\.(?:json|dat)|clipboard_history\.json|integrations\.json|\.env(?:\..*)?|.*\.(?:pfx|p12|key|dmp|sqlite|sqlite3))$')
SKIP = {'.git', 'node_modules', '__pycache__'}
CACHE = {'bin', 'obj', '.vs'}
LIMIT = 12 * 1024 * 1024


def git(root, *args):
    result = subprocess.run(['git', '-c', 'safe.directory=' + root.as_posix(), *args],
                            cwd=root, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return result.stdout


def scan_text(data, relative, output):
    text = data.decode('utf-8-sig', errors='replace')
    for number, line in enumerate(text.splitlines(), 1):
        for rule, pattern in RULES.items():
            if pattern.search(line):
                output.append({'path': relative, 'line': number, 'rule': rule,
                               'lineHash': hashlib.sha256(line.encode('utf-8')).hexdigest()})


def walk(root):
    pending = [root]
    while pending:
        current = pending.pop()
        with os.scandir(current) as entries:
            for entry in entries:
                if entry.is_symlink() or entry.stat(follow_symlinks=False).st_file_attributes & 0x400:
                    continue
                if entry.is_dir(follow_symlinks=False):
                    if entry.name not in SKIP:
                        pending.append(Path(entry.path))
                elif entry.is_file(follow_symlinks=False):
                    yield Path(entry.path)


def audit(root, use_git):
    source = []
    tracked = set()
    if use_git:
        tracked = set(git(root, 'ls-files', '-z').decode('utf-8').split('\0')) - {''}
        candidates = set(git(root, 'ls-files', '--cached', '--others', '--exclude-standard', '-z').decode('utf-8').split('\0')) - {''}
    else:
        candidates = {p.relative_to(root).as_posix() for p in walk(root)
                      if not CACHE.intersection(p.relative_to(root).parts)}
    scanned = 0
    skipped = []
    for relative in sorted(candidates):
        path = root / relative
        if not path.is_file():
            continue
        if SENSITIVE.match(path.name):
            source.append({'path': relative, 'line': 0, 'rule': 'sensitive-file'})
        if path.suffix.lower() not in TEXT and path.name not in {'.gitignore', '.gitattributes'}:
            continue
        if path.stat().st_size > LIMIT:
            skipped.append(relative)
            continue
        scan_text(path.read_bytes(), relative, source)
        scanned += 1
    local = []
    totals = collections.Counter()
    for path in walk(root):
        relative = path.relative_to(root).as_posix()
        totals['files'] += 1
        totals['bytes'] += path.stat().st_size
        if SENSITIVE.match(path.name):
            local.append({'path': relative, 'rule': 'sensitive-file', 'inSource': relative in candidates})
    return {'sourceFilesScanned': scanned, 'trackedPaths': len(tracked),
            'findings': source, 'oversizedTextNotScanned': skipped,
            'localSensitiveFiles': local, 'inventory': dict(totals)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--plain-directory', action='store_true')
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--fail-on-unreviewed', action='store_true')
    args = parser.parse_args()
    result = audit(args.root.resolve(), not args.plain_directory)
    reviewed_path = args.root / 'tools/privacy-reviewed.json'
    reviewed = json.loads(reviewed_path.read_text(encoding='utf-8-sig')) if reviewed_path.exists() else []
    for finding in result['findings']:
        match = next((entry for entry in reviewed if all(entry.get(key) == finding.get(key)
                     for key in ('path', 'rule', 'lineHash'))), None)
        finding['review'] = match['reason'] if match else 'needs-review'
    unreviewed = sum(item['review'] == 'needs-review' for item in result['findings'])
    result['unreviewedSourceFindings'] = unreviewed
    result['limitations'] = ['Pattern-based review; images/audio, binary contents and Git history require separate review.',
                             'Ignored generated files are inventoried by filename, not uploaded or printed.']
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'sourceFilesScanned': result['sourceFilesScanned'],
                      'sourceFindingsByRule': dict(collections.Counter(x['rule'] for x in result['findings'])),
                      'localSensitiveFiles': len(result['localSensitiveFiles']),
                      'unreviewedSourceFindings': unreviewed,
                      'oversizedTextNotScanned': len(result['oversizedTextNotScanned']),
                      'inventory': result['inventory']}, ensure_ascii=False))
    if args.fail_on_unreviewed and (unreviewed or result['oversizedTextNotScanned']):
        raise SystemExit(1)


if __name__ == '__main__':
    main()

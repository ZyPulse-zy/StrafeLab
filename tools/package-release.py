"""Package only allowlisted source and the published application; verify ZIP CRCs."""
import hashlib
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / 'outputs'
EXCLUDED = {'bin', 'obj', '__pycache__', '.git', '.playwright-cli'}
DOCS = ['README.md', 'RESEARCH.md', 'VERIFICATION.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE']


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def included(path, base):
    parts = path.relative_to(base).parts
    return not any(p in EXCLUDED for p in parts) and path.suffix.lower() not in {'.pyc', '.pyo', '.user', '.suo'}


def package(name, entries):
    archive = OUTPUT / name
    count = 0
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        for path, relative in sorted(entries, key=lambda item: str(item[1])):
            bundle.write(path, relative.as_posix())
            count += 1
    with zipfile.ZipFile(archive) as bundle:
        error = bundle.testzip()
        if error:
            raise RuntimeError(f'ZIP CRC failed: {error}')
    result = {'file': name, 'bytes': archive.stat().st_size, 'files': count, 'sha256': sha256(archive), 'crcVerified': True}
    print(json.dumps(result), flush=True)
    return result


def main():
    app = OUTPUT / 'StrafeLab'
    for relative in ['StrafeLab.exe', 'demo-python/python.exe', 'demo-runtime/demoparser2', 'demo-extractor.py']:
        if not (app / relative).exists():
            raise RuntimeError(f'Missing release component: {relative}')
    forbidden = {'gsi-token.txt', 'hall-debug-recovery.json', 'errors.log'}
    for path in app.rglob('*'):
        if path.name.lower() in forbidden or (path.is_dir() and path.name.lower() == 'sessions'):
            raise RuntimeError('User state must not be shipped: ' + str(path.relative_to(app)))
    manifest = {
        'product': 'StrafeLab', 'version': '1.0.0', 'platform': 'win-x64',
        'dotnetRuntime': '8.0.28', 'python': '3.12.10', 'demoparser2': '0.42.0',
        'files': [
            {'path': p.relative_to(app).as_posix(), 'bytes': p.stat().st_size, 'sha256': sha256(p)}
            for p in sorted(app.rglob('*')) if p.is_file() and included(p, app) and p.name != 'release-manifest.json'
        ],
    }
    (app / 'release-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    binaries = [(p, Path('StrafeLab') / p.relative_to(app)) for p in app.rglob('*') if p.is_file() and included(p, app)]
    source = [(ROOT / name, Path('StrafeLab-source') / name) for name in [*DOCS, '.gitignore', 'StrafeLab.sln']]
    for directory in ['src', 'tests', 'tools', 'licenses', 'evidence']:
        base = ROOT / directory
        source += [(p, Path('StrafeLab-source') / p.relative_to(ROOT)) for p in base.rglob('*') if p.is_file() and included(p, base)]
    results = [package('StrafeLab-win-x64.zip', binaries), package('StrafeLab-source.zip', source)]
    (OUTPUT / 'SHA256SUMS.txt').write_text(''.join(f"{r['sha256']}  {r['file']}\n" for r in results), encoding='ascii')
    (OUTPUT / 'package-verification.json').write_text(json.dumps(results, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()

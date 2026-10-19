"""Package compiler outputs only; never include provisioning secrets."""
import hashlib
import json
from pathlib import Path
import re
import sys
import tarfile

root = Path(__file__).resolve().parent
fqbn, core_version, version = sys.argv[1:]
if not re.fullmatch(r"[0-9]{4}\.(0[1-9]|1[0-2])\.(0|[1-9][0-9]*)", version):
    raise SystemExit("Invalid release version; expected YYYY.MM.PATCH")
output = root / 'build/release-output'
names = ['TFirm.ino.bin', 'TFirm.ino.bootloader.bin',
         'TFirm.ino.partitions.bin', 'TFirm.ino.merged.bin']
files = []
for name in names:
    path = output / name
    if not path.is_file() or not path.stat().st_size:
        raise SystemExit(f'Missing compiler output: {path}')
    files.append({'file': name, 'bytes': path.stat().st_size,
                  'sha256': hashlib.sha256(path.read_bytes()).hexdigest()})
manifest = {
    'firmwareVersion': version, 'protocolVersion': 1, 'chip': 'esp32c3',
    'fqbn': fqbn, 'arduinoEsp32Version': core_version,
    'files': files,
    'factoryImage': {'file': 'TFirm.ino.merged.bin', 'offset': '0x0',
                     'warning': 'Full flash image: overwrites saved NVS settings. Use for initial installation only.'},
    'applicationImage': {'file': 'TFirm.ino.bin', 'offset': '0x10000',
                         'warning': 'Only for an existing compatible bootloader and partition layout.'},
}
manifest_path = output / 'manifest.json'
manifest_path.write_text(json.dumps(manifest, indent=2) + '\n')
dist = root / 'dist'
dist.mkdir(exist_ok=True)
archive = dist / f'TFirm-{version}-esp32c3.tar.gz'
if archive.exists() or archive.with_name(archive.name + '.sha256').exists():
    raise SystemExit(f'Release already exists: {archive}; choose another PATCH')
with tarfile.open(archive, 'x:gz') as package:
    for name in names + ['manifest.json']:
        package.add(output / name, arcname=name)
checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_name(archive.name + '.sha256').write_text(f'{checksum}  {archive.name}\n')
print(f'Release archive: {archive}')

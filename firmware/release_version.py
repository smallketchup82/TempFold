"""Calculate the release version once, before compilation and packaging."""
from datetime import datetime
import os
from pathlib import Path
import re
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

patch = os.environ.get('PATCH', '')
if not re.fullmatch(r'0|[1-9][0-9]*', patch):
    raise SystemExit('Supply PATCH as a nonnegative integer, e.g. make release PATCH=0 (no leading zeros).')
try:
    month = datetime.now(ZoneInfo('America/Toronto')).strftime('%Y.%m')
except ZoneInfoNotFoundError:
    raise SystemExit('America/Toronto timezone data is required; install system tzdata or Python tzdata.')
version = f'{month}.{patch}'
archive = Path(__file__).resolve().parent / 'dist' / f'TFirm-{version}-esp32c3.tar.gz'
if archive.exists() or archive.with_name(archive.name + '.sha256').exists():
    raise SystemExit(f'Release already exists: {archive}; choose another PATCH')
print(version)

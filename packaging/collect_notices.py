"""Copy the Python runtime and active build dependency licenses."""
from importlib import metadata
from pathlib import Path
import shutil
import sys

bundle = Path(sys.argv[1])
notices = bundle / 'licenses'
notices.mkdir(exist_ok=True)
for name in ('Pillow', 'PyInstaller'):
    dist = metadata.distribution(name)
    target = notices / name
    target.mkdir(exist_ok=True)
    for item in dist.files or []:
        if any(part.lower().startswith(('license', 'copying')) for part in item.parts):
            source = Path(dist.locate_file(item))
            if source.is_file():
                destination = target / str(item).replace('/', '_').replace('\\', '_')
                shutil.copyfile(source, destination)
source = Path(sys.base_prefix) / 'LICENSE.txt'
if source.is_file():
    shutil.copyfile(source, notices / (source.parent.name + '-' + source.name))

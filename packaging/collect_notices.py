"""Copy the installed runtime's licenses and replaceable pystray source."""
from importlib import metadata
from pathlib import Path
import shutil
import sys

bundle = Path(sys.argv[1])
notices = bundle / 'licenses'
notices.mkdir(exist_ok=True)
for name in ('Pillow', 'pystray', 'six', 'PyInstaller'):
    dist = metadata.distribution(name)
    target = notices / name
    target.mkdir(exist_ok=True)
    for item in dist.files or []:
        if any(part.lower().startswith(('license', 'copying')) for part in item.parts):
            source = Path(dist.locate_file(item))
            if source.is_file():
                destination = target / str(item).replace('/', '_').replace('\\', '_')
                shutil.copyfile(source, destination)
    if name == 'pystray':
        for item in dist.files or []:
            if str(item).startswith('pystray/') and str(item).endswith('.py'):
                destination = bundle / 'dependency-source' / item
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(dist.locate_file(item), destination)
for source in [Path(sys.base_prefix) / 'LICENSE.txt', *Path(sys.base_prefix).glob('tcl/*/license.terms')]:
    if source.is_file():
        shutil.copyfile(source, notices / (source.parent.name + '-' + source.name))

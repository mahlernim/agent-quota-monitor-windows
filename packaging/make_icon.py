from pathlib import Path
from quota.desktop import DesktopApplication

target = Path('build/robot-ring.ico')
target.parent.mkdir(parents=True, exist_ok=True)
app = object.__new__(DesktopApplication)
app._window_icon_image().save(target, format='ICO', sizes=[(16, 16), (32, 32), (64, 64)])

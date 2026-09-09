"""Package an already tested Release build without modifying a Jellyfin server."""
from pathlib import Path
import datetime, hashlib, json, zipfile

root = Path(__file__).resolve().parent.parent
version = '1.0.8'
dll = root / 'src/Jellyfin.Plugin.Companion/bin/Release/net10.0/Jellyfin.Plugin.Companion.dll'
if not dll.is_file():
    raise SystemExit('Run dotnet test -c Release for Companion.Tests before packaging.')
output = root / 'artifacts'
output.mkdir(exist_ok=True)
archive = output / f'Cowabunga.Jellyfin.Companion_{version}_jellyfin-12.0.zip'
metadata = dict(category='General', changelog='Fix UNIQUE constraint failed: Peoples.Id during person reconciliation by rebuilding all credit stubs without persisted database IDs. Preserve names, roles, order and external IDs.',
    description='Media recognition, localized metadata and Cowabunga artwork with per-library controls.',
    guid='0a281de0-d3c8-43ef-bf2d-5fac17fbb8c6', name='Cowabunga Jellyfin Companion',
    overview='Smart Resolver, Choose your Meta and Custom Artwork in one plugin.', owner='Lootfullin',
    targetAbi='12.0.0.0', timestamp=datetime.datetime.now(datetime.timezone.utc).isoformat(),
    version=version+'.0', status='Active', autoUpdate=True, imagePath='assets/cowabunga-logo.jpg')
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as package:
    package.write(dll, dll.name)
    package.write(root / 'assets/cowabunga-logo.jpg', 'assets/cowabunga-logo.jpg')
    package.writestr('meta.json', json.dumps(metadata, indent=2))
    for name in ['README.md', 'UPSTREAM.md', 'VALIDATION.md', 'LICENSE', 'Choose-your-Meta-LICENSE', 'Custom-Artwork-LICENSE', 'Smart-Resolver-LICENSE']:
        if (root/name).exists(): package.write(root/name, name)
with zipfile.ZipFile(archive) as package:
    assert package.testzip() is None
    assert len([name for name in package.namelist() if name.endswith('.dll')]) == 1
    assert json.loads(package.read('meta.json'))['targetAbi'] == '12.0.0.0'
checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix('.zip.sha256').write_text(f'{checksum}  {archive.name}\n',encoding='ascii')
print(archive)
print('SHA256:', checksum)

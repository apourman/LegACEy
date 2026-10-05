"""Attach verified build assets to an existing release-please draft."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.parse
import urllib.request

REPOSITORY = 'apourman/LegACEy'


class GitHub:
    def request(self, method, path, data=None, binary=False):
        url = path if path.startswith('https://') else f'https://api.github.com/repos/{REPOSITORY}/{path}'
        body = data if binary else (json.dumps(data).encode() if data is not None else None)
        headers = {'Authorization': f"Bearer {os.environ['GH_TOKEN']}",
                   'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28',
                   'Content-Type': 'application/octet-stream' if binary else 'application/json'}
        with urllib.request.urlopen(urllib.request.Request(url, body, headers, method=method), timeout=120) as response:
            content = response.read()
            return json.loads(content) if content else None


def find_release(api, tag):
    # The tag endpoint returns published releases; authenticated listings also
    # include drafts. Keep paging so older drafts remain recoverable.
    page = 1
    while True:
        releases = api.request('GET', f'releases?per_page=100&page={page}')
        for release in releases:
            if release['tag_name'] == tag:
                return release
        if len(releases) < 100:
            raise ValueError(f'Release not found for tag: {tag}')
        page += 1


def publish(directory, source, api):
    sha, tag, version = source['sha'], source['tag'], source['version']
    if not re.fullmatch(r'\d+\.\d+\.\d+', version) or tag != f'decal-plugin-v{version}':
        raise ValueError('Invalid plugin release tag or version')
    if json.loads((directory / 'source.json').read_text(encoding='utf-8-sig')) != source:
        raise ValueError('Package provenance does not match the release')
    names = ['LegACEy.Client.DecalPlugin.dll', f'LegACEy-{tag}-windows-x86.zip']
    files = [directory / name for name in names]
    checksum = directory / 'SHA256SUMS.txt'
    expected = ''.join(f'{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}\n' for p in files)
    if checksum.read_text() != expected:
        raise ValueError('Release package checksum mismatch')
    # Resolve both annotated and lightweight tags without creating or moving one.
    if api.request('GET', f'commits/{tag}')['sha'] != sha:
        raise ValueError('Release tag does not match the package source')
    release = find_release(api, tag)
    if not release['draft']:
        print(f'{tag} already published; leaving its assets unchanged')
        return
    for path in [*files, checksum]:
        for asset in release['assets']:
            if asset['name'] == path.name:
                api.request('DELETE', f"releases/assets/{asset['id']}")
        upload_url = release['upload_url'].split('{')[0]
        api.request('POST', upload_url + '?' + urllib.parse.urlencode({'name': path.name}), path.read_bytes(), binary=True)
    api.request('PATCH', f"releases/{release['id']}", {'draft': False, 'make_latest': 'false'})
    print(f'Published {tag}')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--package', type=Path, required=True)
    args = parser.parse_args()
    if os.environ.get('GITHUB_REPOSITORY') != REPOSITORY or os.environ.get('GITHUB_REF') != 'refs/heads/master':
        raise ValueError('Official publishing is restricted to apourman/LegACEy master')
    source = {key: os.environ[f'RELEASE_{key.upper()}'] for key in ['tag', 'sha', 'version']}
    checkout = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    if checkout != source['sha'] or Path('decal-plugin/VERSION').read_text().strip() != source['version']:
        raise ValueError('Checkout does not match the release source and version')
    publish(args.package, source, GitHub())


if __name__ == '__main__':
    main()

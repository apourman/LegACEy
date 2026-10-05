import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from urllib.error import HTTPError
from unittest.mock import patch

import publish_release


class FakeGitHub:
    def __init__(self, sha):
        self.sha = sha
        self.draft = True
        self.fail_upload = False
        self.calls = []
        self.release_pages = None

    def request(self, method, path, data=None, **kwargs):
        self.calls.append((method, path, data))
        if path.startswith('commits/'):
            return {'sha': self.sha}
        release = {'id': 7, 'tag_name': 'decal-plugin-v0.1.0', 'draft': self.draft,
                   'assets': [{'id': 8, 'name': 'LegACEy.Client.DecalPlugin.dll'}],
                   'upload_url': 'https://uploads.github.com/repos/apourman/LegACEy/releases/7/assets{?name,label}'}
        if path.startswith('releases/tags/'):
            if self.draft:
                raise HTTPError(path, 404, 'Drafts are not returned by tag lookup', {}, None)
            return release
        if path.startswith('releases?'):
            page = int(path.split('page=')[-1])
            if self.release_pages is not None:
                return self.release_pages.get(page, [])
            return [release] if page == 1 else []
        if path.startswith('https://uploads.github.com/') and self.fail_upload:
            raise RuntimeError('upload failed')
        if method == 'PATCH':
            self.draft = data['draft']


class PublishingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.source = {'sha': 'source-sha', 'tag': 'decal-plugin-v0.1.0', 'version': '0.1.0'}
        self.api = FakeGitHub(self.source['sha'])
        (self.directory / 'source.json').write_text(json.dumps(self.source), encoding='utf-8-sig')
        names = ['LegACEy.Client.DecalPlugin.dll', 'LegACEy-decal-plugin-v0.1.0-windows-x86.zip']
        for name in names:
            (self.directory / name).write_bytes(b'test build')
        (self.directory / 'SHA256SUMS.txt').write_text(''.join(
            f'{hashlib.sha256((self.directory / name).read_bytes()).hexdigest()}  {name}\n' for name in names))

    def test_uploads_assets_then_publishes_without_changing_release_notes(self):
        publish_release.publish(self.directory, self.source, self.api)
        self.assertFalse(self.api.draft)
        uploads = [c for c in self.api.calls if c[0] == 'POST']
        self.assertEqual(len(uploads), 3)
        self.assertTrue(all(c[1].startswith('https://uploads.github.com/') for c in uploads))
        self.assertEqual(self.api.calls[-1], ('PATCH', 'releases/7', {'draft': False, 'make_latest': 'false'}))

    def test_failed_upload_remains_draft_and_retry_succeeds(self):
        self.api.fail_upload = True
        with self.assertRaises(RuntimeError):
            publish_release.publish(self.directory, self.source, self.api)
        self.assertTrue(self.api.draft)
        self.api.fail_upload = False
        publish_release.publish(self.directory, self.source, self.api)
        self.assertFalse(self.api.draft)

    def test_published_release_assets_are_unchanged_on_retry(self):
        self.api.draft = False
        publish_release.publish(self.directory, self.source, self.api)
        self.assertTrue(all(c[0] == 'GET' for c in self.api.calls))

    def test_wrong_tag_source_and_corrupt_assets_cannot_publish(self):
        self.api.sha = 'different-source'
        with self.assertRaisesRegex(ValueError, 'tag does not match'):
            publish_release.publish(self.directory, self.source, self.api)
        self.api.sha = self.source['sha']
        (self.directory / 'LegACEy.Client.DecalPlugin.dll').write_bytes(b'corrupt')
        with self.assertRaisesRegex(ValueError, 'checksum'):
            publish_release.publish(self.directory, self.source, self.api)
        self.assertTrue(all(c[0] == 'GET' for c in self.api.calls))

    def test_wrong_package_provenance_is_rejected_before_api_access(self):
        source = {**self.source, 'sha': 'another-source'}
        with self.assertRaisesRegex(ValueError, 'provenance'):
            publish_release.publish(self.directory, source, self.api)
        self.assertEqual(self.api.calls, [])

    def test_finds_older_draft_on_second_page(self):
        other = {'tag_name': 'another-tag', 'draft': False}
        target = self.api.request('GET', 'releases?per_page=100&page=1')[0]
        self.api.calls.clear()
        self.api.release_pages = {1: [other] * 100, 2: [target]}
        publish_release.publish(self.directory, self.source, self.api)
        self.assertFalse(self.api.draft)
        self.assertIn(('GET', 'releases?per_page=100&page=2', None), self.api.calls)

    def test_missing_release_fails_without_mutating_any_release(self):
        self.api.release_pages = {}
        with self.assertRaisesRegex(ValueError, 'Release not found'):
            publish_release.publish(self.directory, self.source, self.api)
        self.assertTrue(all(c[0] == 'GET' for c in self.api.calls))

    def test_other_repositories_and_branches_cannot_publish(self):
        for repository, branch in [('someone/LegACEy', 'refs/heads/master'),
                                   ('apourman/LegACEy', 'refs/heads/client-avalonia-ui')]:
            with patch.dict(os.environ, {'GITHUB_REPOSITORY': repository, 'GITHUB_REF': branch}):
                with patch('sys.argv', ['publish_release.py', '--package', str(self.directory)]):
                    with self.assertRaisesRegex(ValueError, 'restricted'):
                        publish_release.main()


if __name__ == '__main__':
    unittest.main()

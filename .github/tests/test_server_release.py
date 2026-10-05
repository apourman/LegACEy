"""Run the actual server source-verification step against offline fixtures."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest

WORKFLOW = Path(__file__).resolve().parents[1] / 'workflows/deploy-main.yml'


def step_script(name):
    section = WORKFLOW.read_text().split(f'      - name: {name}\n', 1)[1]
    block = section.split('        run: |\n', 1)[1].split('\n      - name:', 1)[0]
    return textwrap.dedent(block).strip()


class ServerSourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        package = self.root / 'ace-release'
        package.mkdir()
        self.sha = 'a' * 40
        (package / 'LegACEy-server-linux-x64.tar.gz').write_bytes(b'archive fixture')
        self.digest = hashlib.sha256(b'archive fixture').hexdigest()
        (package / 'source.json').write_text(json.dumps({
            'sha': self.sha, 'tag': 'v0.1.1', 'sha256': self.digest}))
        gh = self.root / 'gh'
        gh.write_text('''#!/usr/bin/env python3
import os, sys
path = sys.argv[2]
if '/commits/' in path:
    print(os.environ.get('TEST_TAG_SHA', 'a' * 40))
elif '/jobs' in path:
    print(os.environ.get('TEST_BUILD_CONCLUSION', 'success'))
else:
    print('b' * 40)
''')
        gh.chmod(0o755)
        self.env = dict(os.environ, RUNNER_TEMP=str(self.root), RELEASE_TAG='v0.1.1',
                        RELEASE_SHA=self.sha, GITHUB_SHA='b' * 40, SOURCE_RUN_ID='',
                        REPOSITORY='apourman/LegACEy', GITHUB_OUTPUT=str(self.root / 'output'),
                        PATH=str(self.root) + os.pathsep + os.environ['PATH'])

    def verify(self):
        return subprocess.run(['bash', '-euo', 'pipefail', '-c', step_script(
            'Verify the release tag matches the package source')],
            env=self.env, text=True, capture_output=True)

    def test_later_push_can_publish_build_of_older_release_commit(self):
        result = self.verify()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.root / 'output').read_text(), f'sha={self.sha}\n')

    def test_manual_retry_uses_build_provenance_instead_of_run_head(self):
        self.env.update(RELEASE_SHA='', SOURCE_RUN_ID='42')
        result = self.verify()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.root / 'output').read_text(), f'sha={self.sha}\n')

    def test_wrong_tag_commit_cannot_publish(self):
        self.env['TEST_TAG_SHA'] = 'c' * 40
        result = self.verify()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Release tag does not match', result.stderr)

    def test_wrong_requested_commit_cannot_publish(self):
        self.env['RELEASE_SHA'] = 'c' * 40
        result = self.verify()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Package source does not match', result.stderr)

    def test_corrupt_archive_cannot_publish(self):
        (self.root / 'ace-release/LegACEy-server-linux-x64.tar.gz').write_bytes(b'corrupt')
        result = self.verify()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('checksum mismatch', result.stderr)

    def test_missing_provenance_cannot_publish(self):
        (self.root / 'ace-release/source.json').unlink()
        self.assertNotEqual(self.verify().returncode, 0)

    def test_retry_requires_successful_server_build(self):
        self.env.update(RELEASE_SHA='', SOURCE_RUN_ID='42', TEST_BUILD_CONCLUSION='failure')
        result = self.verify()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('successful build', result.stderr)

    def test_plugin_tag_cannot_enter_server_publishing(self):
        self.env['RELEASE_TAG'] = 'decal-plugin-v0.1.1'
        result = self.verify()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Expected a server release tag', result.stderr)

    def test_build_records_actual_checkout_and_archive_checksum(self):
        git = self.root / 'git'
        git.write_text('#!/bin/sh\nprintf "%s\\n" "$RELEASE_SHA"\n')
        git.chmod(0o755)
        result = subprocess.run(['bash', '-euo', 'pipefail', '-c', step_script(
            'Record the package source and checksum')], env=self.env, text=True, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        source = json.loads((self.root / 'ace-release/source.json').read_text())
        self.assertEqual(source, {'sha': self.sha, 'tag': 'v0.1.1', 'sha256': self.digest})


if __name__ == '__main__':
    unittest.main()

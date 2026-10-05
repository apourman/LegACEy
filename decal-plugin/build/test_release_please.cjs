// Offline regression checks against release-please 17.3.0; see README.md.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {Manifest, setLogger} = require('release-please');
const root = path.resolve(__dirname, '../..');
const config = JSON.parse(fs.readFileSync(`${root}/release-please-config.json`));
const tracked = JSON.parse(fs.readFileSync(`${root}/.release-please-manifest.json`));
// Bootstrap scenarios must still work after the plugin has shipped.
const initial = {'.': tracked['.']};
assert.equal(require('release-please/package.json').version, '17.3.0');
const silent = {info() {}, warn() {}, debug() {}, error() {}};
setLogger(silent);
const serverBase = 'a'.repeat(40);
const pluginBase = 'b'.repeat(40);
const serverMerge = 'c'.repeat(40);
const laterPush = 'd'.repeat(40);
const commit = (message, files, sha = laterPush) => ({message, files, sha});

async function fixture(commits, versions = initial, merged = []) {
  const releases = [{tagName: `v${initial['.']}`, name: 'server', sha: serverBase, notes: ''}];
  if (versions['decal-plugin']) {
    releases.push({tagName: `decal-plugin-v${versions['decal-plugin']}`,
      name: 'plugin', sha: pluginBase, notes: ''});
  }
  const github = {
    repository: {owner: 'apourman', repo: 'LegACEy'},
    async getFileJson(file) {
      return file === 'release-please-config.json' ? config : versions;
    },
    async *releaseIterator() {yield* releases;},
    async *tagIterator() {},
    async *mergeCommitIterator() {
      yield* commits;
      yield commit('chore: release', ['.release-please-manifest.json'], pluginBase);
      yield commit('chore: release', ['.release-please-manifest.json'], serverBase);
      yield commit('chore: bootstrap', [], config['bootstrap-sha']);
    },
    async *pullRequestIterator() {yield* merged;},
  };
  return Manifest.fromManifest(github, 'master', undefined, undefined, {logger: silent});
}

async function prs(commits, versions) {
  return (await fixture(commits, versions)).buildPullRequests();
}
function paths(pr) {return pr.updates.map(update => update.path).sort();}
function mergedPR(pr, number, sha) {
  return {number, title: pr.title.toString(), body: pr.body.toString(),
    headBranchName: pr.headRefName, baseBranchName: 'master', sha,
    labels: ['autorelease: pending'], files: paths(pr)};
}

(async () => {
  let count = 0;
  for (const {type} of config.packages['decal-plugin']['changelog-sections']) {
    const out = await prs([commit(`${type}: plugin work`, ['decal-plugin/example.cs'])]);
    assert.equal(out.length, 1, `${type} should create only the plugin PR`);
    assert.equal(out[0].version.toString(), '0.1.0');
    assert.deepEqual(paths(out[0]), [
      '.release-please-manifest.json', 'decal-plugin/CHANGELOG.md', 'decal-plugin/VERSION']);
    assert.match(out[0].headRefName, /decal-plugin/);
    count++;
  }
  assert.equal((await prs([commit('fix: old plugin', ['Client/example.cs'])])).length, 0);
  count++;
  assert.equal((await prs([commit('arbitrary plugin message', ['decal-plugin/example.cs'])])).length, 0);
  count++;
  let out = await prs([commit('fix: plugin patch', ['decal-plugin/example.cs'])],
    {...initial, 'decal-plugin': '0.1.0'});
  assert.equal(out.length, 1);
  assert.equal(out[0].version.toString(), '0.1.1');
  count++;

  const serverPR = (await prs([commit('fix: server work', ['Source/ACE.Server/example.cs'])]))[0];
  assert.deepEqual(paths(serverPR), ['.release-please-manifest.json', 'LEGACEy_CHANGELOG.md', 'VERSION']);
  count++;
  out = await prs([commit('fix: mixed work', ['Source/ACE.Server/example.cs', 'decal-plugin/example.cs'])]);
  assert.equal(out.length, 2);
  assert.notEqual(out[0].headRefName, out[1].headRefName);
  count++;

  const pluginPR = (await prs([commit('fix: plugin work', ['decal-plugin/example.cs'])]))[0];
  const versions = {...initial, 'decal-plugin': '0.1.0'};
  const releaseCommit = commit(pluginPR.title.toString(), paths(pluginPR), pluginBase);
  // Root sees the shared manifest, but generated release titles use chore,
  // which produces no root release notes. The plugin tag stops its own history.
  assert.equal((await prs([releaseCommit], versions)).length, 0);
  count++;
  out = await prs([commit('fix: server work', ['Database/example.sql']), releaseCommit], versions);
  assert.equal(out.length, 1);
  assert.ok(paths(out[0]).includes('VERSION'));
  count++;

  const pluginMerged = mergedPR(pluginPR, 1, pluginBase);
  let manifest = await fixture([releaseCommit], versions, [pluginMerged]);
  const pluginReleases = await manifest.buildReleases();
  assert.equal(pluginReleases.length, 1);
  assert.equal(pluginReleases[0].path, 'decal-plugin');
  assert.equal(pluginReleases[0].tag.toString(), 'decal-plugin-v0.1.0');
  assert.equal(pluginReleases[0].sha, pluginBase);
  count++;

  const serverMerged = mergedPR(serverPR, 2, serverMerge);
  manifest = await fixture([], initial, [serverMerged]);
  const serverReleases = await manifest.buildReleases();
  assert.equal(serverReleases.length, 1);
  assert.equal(serverReleases[0].path, '.');
  assert.match(serverReleases[0].tag.toString(), /^v/);
  assert.equal(serverReleases[0].sha, serverMerge);
  count++;

  // A later plugin push can pick up a previously merged server release PR.
  manifest = await fixture([
    commit('fix: plugin work', ['decal-plugin/example.cs'], laterPush),
    commit(serverMerged.title, serverMerged.files, serverMerge),
  ], initial, [serverMerged]);
  const delayed = await manifest.buildReleases();
  assert.equal(delayed.length, 1);
  assert.equal(delayed[0].path, '.');
  assert.equal(delayed[0].sha, serverMerge);
  assert.notEqual(delayed[0].sha, laterPush);
  count++;

  manifest = await fixture([], initial, [serverMerged, pluginMerged]);
  const combined = await manifest.buildReleases();
  assert.equal(combined.length, 2);
  assert.deepEqual(combined.map(release => [release.path, release.sha]), [
    ['.', serverMerge], ['decal-plugin', pluginBase]]);
  count++;

  // Workflow-only fixes must not create a server release, including plugin CI.
  for (const file of ['.github/workflows/release-decal-plugin.yml',
    '.github/workflows/deploy-main.yml']) {
    assert.equal((await prs([commit('fix: workflow work', [file])])).length, 0);
    count++;
  }
  out = await prs([commit('fix: server and workflow work', [
    'Source/ACE.Server/example.cs', '.github/workflows/deploy-main.yml'])]);
  assert.equal(out.length, 1);
  assert.ok(paths(out[0]).includes('VERSION'));
  count++;

  // Other root files remain part of the root package; document this boundary.
  for (const file of ['README.md', 'release-please-config.json']) {
    out = await prs([commit('fix: root work', [file])]);
    assert.equal(out.length, 1);
    assert.ok(paths(out[0]).includes('VERSION'));
    count++;
  }
  out = await prs([commit('fix: altered release title', paths(pluginPR), pluginBase)], versions);
  assert.equal(out.length, 1); // A fix title would make the shared manifest release-relevant.
  assert.ok(paths(out[0]).includes('VERSION'));
  count++;

  console.log(`Passed ${count} release-please 17.3.0 scenarios: versions, commit types, paths, separate and delayed releases, release SHAs, workflow exclusion, and root-file boundaries.`);
})().catch(error => {console.error(error); process.exit(1);});

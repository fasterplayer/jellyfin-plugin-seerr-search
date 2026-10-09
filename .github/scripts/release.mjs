// Release helpers, used by .github/workflows/build.yaml.
//   node .github/scripts/release.mjs check <tag>
//       The tag, Directory.Build.props and build.yaml must carry the same version, and CHANGELOG.md must have its section.
//   node .github/scripts/release.mjs notes <tag>
//       Prints the CHANGELOG.md section of that version.
//   node .github/scripts/release.mjs manifest <tag> <zip> <repository>
//       Adds that version (checksum of the zip, release URL, changelog) to manifest.json, newest first.
import { readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';

const [, , command, tag, zip, repository] = process.argv;
const read = (path) => readFileSync(path, 'utf8').replace(/\r\n/g, '\n');

function field(text, name) {
  const m = text.match(new RegExp(`^${name}:\\s*"?([^"\\n]+)"?\\s*$`, 'm'));
  return m ? m[1].trim() : null;
}

function changelogSection(version) {
  const lines = read('CHANGELOG.md').split('\n');
  const start = lines.findIndex((l) => l.trim() === `## ${version}`);
  if (start < 0) return null;
  let end = lines.findIndex((l, i) => i > start && l.startsWith('## '));
  if (end < 0) end = lines.length;
  return lines.slice(start + 1, end).join('\n').trim();
}

if (command === 'check') {
  const props = read('Directory.Build.props');
  const versions = {
    tag,
    'Directory.Build.props <Version>': (props.match(/<Version>([^<]+)<\/Version>/) || [])[1],
    'Directory.Build.props <AssemblyVersion>': (props.match(/<AssemblyVersion>([^<]+)<\/AssemblyVersion>/) || [])[1],
    'build.yaml': field(read('build.yaml'), 'version')
  };
  const bad = Object.entries(versions).filter(([, v]) => v !== tag);
  if (bad.length) {
    console.error('Version mismatch:', JSON.stringify(versions, null, 2));
    process.exit(1);
  }
  if (!changelogSection(tag)) {
    console.error(`CHANGELOG.md has no "## ${tag}" section`);
    process.exit(1);
  }
  console.log(`Version ${tag}: consistent.`);
} else if (command === 'notes') {
  console.log(changelogSection(tag) || '');
} else if (command === 'manifest') {
  const build = read('build.yaml');
  const manifest = JSON.parse(read('manifest.json'));
  const plugin = manifest[0];
  const checksum = createHash('md5').update(readFileSync(zip)).digest('hex');
  const zipName = zip.split('/').pop();
  const entry = {
    version: tag,
    changelog: (changelogSection(tag) || '').replace(/\s*\n\s*/g, ' ').trim(),
    targetAbi: field(build, 'targetAbi'),
    sourceUrl: `https://github.com/${repository}/releases/download/${tag}/${zipName}`,
    checksum,
    timestamp: new Date().toISOString().replace(/\.\d+Z$/, 'Z')
  };
  plugin.versions = [entry, ...plugin.versions.filter((v) => v.version !== tag)];
  writeFileSync('manifest.json', JSON.stringify(manifest, null, 2) + '\n');
  console.log(`manifest.json: ${tag} added (md5 ${checksum}).`);
} else {
  console.error('usage: release.mjs check|notes|manifest ...');
  process.exit(2);
}

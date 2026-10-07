// Writes the release notes file that vpk pack embeds (the About tab shows it) and that the
// Generate release notes step puts on the GitHub release, so both show the same text.
// Runs before the Tag release step, so the tag for VERSION does not exist yet.
// Never throws past a Gemini problem: the fallback is the raw commit list.

const fs = require('fs');
const { generate, section, STYLE } = require('./gemini.js');

const CHANNEL_RE = {
  stable: /^\d+\.\d+\.\d+$/,
  alpha: /^\d+\.\d+\.\d+-alpha\.\d+$/,
  rc: /^\d+\.\d+\.\d+-rc\.\d+$/,
};

module.exports = async function ({ core, exec }) {
  const { GEMINI_API_KEY, VERSION, CHANNEL, REPOSITORY, NOTES_FILE } = process.env;

  // Previous tag: the highest in this channel, else the highest stable, else none (first release).
  const tagList = await exec.getExecOutput('git', ['tag', '-l', '--sort=-v:refname']);
  const tags = tagList.stdout.split('\n').map((t) => t.trim()).filter((t) => t && t !== VERSION);
  const prev =
    tags.find((t) => CHANNEL_RE[CHANNEL].test(t)) || tags.find((t) => CHANNEL_RE.stable.test(t)) || '';

  const logArgs = ['log', '--no-merges', '--format=%h%x09%s%n%b%x00'];
  if (prev) logArgs.push(`${prev}..HEAD`);
  else logArgs.push('-50');
  const log = await exec.getExecOutput('git', logArgs);
  const commits = log.stdout
    .split('\0')
    .map((r) => r.trim())
    .filter(Boolean)
    .map((r) => {
      const [head, ...body] = r.split('\n');
      const [hash, subject] = head.split('\t');
      return { hash, subject, body: body.map((l) => l.trim()).filter(Boolean) };
    });
  core.info(`Previous tag: ${prev || '(none)'}; commits: ${commits.length}`);

  if (commits.length === 0) {
    fs.writeFileSync(NOTES_FILE, `No changes since ${prev}.\n`);
    return;
  }

  const fallback = commits
    .map((c) => [`- ${c.subject} (${c.hash})`, ...c.body.map((l) => `  ${l}`)].join('\n'))
    .join('\n');

  const prompt = `You are writing release notes for GalactiLog, a Windows desktop application that catalogs astrophotography image files. The reader is a user deciding whether to install this update, not a developer. The notes are derived from the commit messages below.

Produce two things:

1. SUMMARY: one or two sentences in plain language on what changed and why it matters to the user.

2. CHANGES: exactly one line per input commit, in the form:
- <plain language description> (<hash>)

Rules for the CHANGES list:
- Each line ends with the original commit hash in parentheses.
- Keep each description under 100 characters before the hash.
- Rewrite each commit into what the user notices. Drop prefixes like feat:, fix:, chore:.
- Do not add, remove or merge commits: output exactly one line per input commit, in the same order.
- For a commit with no user-visible effect (tests, build, internal cleanup), say so in a few plain words, such as "Internal cleanup, no visible change (abc1234)".

${STYLE}

## Commits
${fallback}

## Output format (follow exactly)
---SUMMARY_START---
<summary>
---SUMMARY_END---
---CHANGES_START---
- <description> (<hash>)
---CHANGES_END---`;

  let summary = null;
  let changes = fallback;
  if (!GEMINI_API_KEY) {
    core.warning('GEMINI_API_KEY is not set; the release notes are the raw commit list.');
  } else {
    try {
      const text = await generate(GEMINI_API_KEY, prompt);
      const s = section(text, 'SUMMARY');
      const c = section(text, 'CHANGES');
      if (!s || !c) throw new Error(`Gemini response missing a section: ${text.slice(0, 500)}`);
      summary = s;
      changes = c;
    } catch (e) {
      core.warning(`Release notes fall back to the commit list: ${e.message}`);
    }
  }

  const parts = [];
  if (summary) parts.push(summary, '');
  parts.push(prev ? `## Changes since ${prev}` : '## Changes', changes);
  if (prev) parts.push('', `**Full changelog**: https://github.com/${REPOSITORY}/compare/${prev}...${VERSION}`);
  fs.writeFileSync(NOTES_FILE, parts.join('\n') + '\n');
};

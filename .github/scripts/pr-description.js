// Runs from pr-description.yml through actions/github-script. Reads the pull request's commits
// and changed files through the API, asks Gemini for a title and description derived from them,
// and writes both back to the pull request. Every failure is a warning, not a failed check: the
// description is cosmetic and must never block a merge.

const { generate, section, STYLE } = require('./gemini.js');

module.exports = async function ({ github, context, core }) {
  const apiKey = process.env.GEMINI_API_KEY;
  if (!apiKey) {
    core.warning('GEMINI_API_KEY is not set; the pull request keeps its own title and description.');
    return;
  }

  const { owner, repo } = context.repo;
  const pull_number = context.payload.pull_request.number;

  // Merge commits carry no change of their own; their text would only repeat the branch name.
  const commits = (await github.paginate(github.rest.pulls.listCommits, { owner, repo, pull_number }))
    .filter((c) => c.parents.length < 2)
    .map((c) => {
      const [subject, ...rest] = c.commit.message.split('\n');
      const body = rest.join('\n').trim();
      const head = `- ${subject.trim()} (${c.sha.slice(0, 7)})`;
      return body ? `${head}\n${body.replace(/^/gm, '  ')}` : head;
    })
    .join('\n');

  const files = (await github.paginate(github.rest.pulls.listFiles, { owner, repo, pull_number }))
    .map((f) => `- ${f.filename} (+${f.additions}/-${f.deletions})`)
    .join('\n');

  const prompt = `You write the title and description of a pull request for GalactiLog, a Windows desktop
application that catalogs astrophotography image files. Derive both from the commit messages
below. Do not invent changes the commits do not describe.

## Commits
${commits || '(none)'}

## Files changed
${files || '(none)'}

## Current title (a hint only; replace it when the commits say something better)
${context.payload.pull_request.title}

${STYLE}

## Title rules
- Under 70 characters, sentence case, no conventional-commit prefix such as "feat:" or "fix:".
- Name the single most important change. Never a comma list of changes, never "and" joining two.
- Specific, not vague: say what changed, not "improvements and fixes".
- Good examples in this repository's commit style: "Library tab: refuse a scan while the filter
  rules are unsaved", "Keep the test fixtures with the tests", "Drop the execution roadmaps and
  point the build script at packaging.md".
- Bad examples: "Update UI, fix bugs, and improve config", "Various improvements and fixes".

## Body rules
- A "### Summary" section: 1 to 3 plain sentences on what the change does for the user and why.
- A "### Changes" section: a flat bullet list, at most 8 bullets, one sentence each. Combine
  related items into one bullet. No sub-categories such as "Features:" or "Bug fixes:".
- No other sections, no conclusion, no footer.

## Output format (exactly this, nothing before or after)
---TITLE_START---
<title>
---TITLE_END---
---BODY_START---
<markdown body>
---BODY_END---`;

  let text;
  try {
    text = await generate(apiKey, prompt);
  } catch (err) {
    core.warning(`Gemini call failed; the pull request keeps its own title and description. ${err.message}`);
    return;
  }

  const title = section(text, 'TITLE');
  const body = section(text, 'BODY');
  if (title === null || body === null) {
    core.warning(`Gemini response had no TITLE or BODY section; the pull request is unchanged. Response: ${text.slice(0, 500)}`);
    return;
  }

  await github.rest.pulls.update({ owner, repo, pull_number, title, body });
  core.info(`Pull request #${pull_number} title: ${title}`);
};

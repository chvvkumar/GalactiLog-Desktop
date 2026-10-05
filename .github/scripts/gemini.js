// Shared Gemini call for pr-description.yml and release.yml. Both run it through
// actions/github-script, which supplies fetch on the runner's own node, so the self-hosted
// Windows runner needs no python, jq or curl for this.

const MODEL = 'gemini-2.5-flash';

// Writing rules every prompt carries. The output is read by users of the application, so the
// rules are the ones the NINA Display project settled on: plain language, no filler, no emojis,
// no dashes used as punctuation, and nothing that says where the text came from.
const STYLE = `## Writing style (follow strictly)
Write the way a developer writes a short note to a teammate: direct, specific, plain.
- Plain language a user of the application understands. No jargon. If a technical term has no
  plain equivalent, say what it means for the user instead of naming it.
- Active voice. Say what changed: "The scan now skips hidden folders", "Fixed the crash when ...".
- Never start with "This pull request", "This PR" or "This release".
- Never use: introduces, enhances, leverages, utilizes, facilitates, ensures, enables, allows,
  streamline, robust, seamless, comprehensive, empower, elevate, holistic, transformative.
- Never use passive constructions such as "was added", "has been implemented".
- Never hedge: no "seems to", "appears to", "might", "likely".
- No emojis. No em dashes or en dashes anywhere: use a comma, a colon or a new sentence.
- Do not mention how the text was written or who or what wrote it.`;

async function generate(apiKey, prompt, maxOutputTokens = 8192) {
  const res = await fetch(
    `https://generativelanguage.googleapis.com/v1beta/models/${MODEL}:generateContent`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-goog-api-key': apiKey },
      body: JSON.stringify({
        contents: [{ parts: [{ text: prompt }] }],
        generationConfig: { temperature: 0.3, maxOutputTokens },
      }),
    },
  );
  if (!res.ok) {
    throw new Error(`Gemini HTTP ${res.status}: ${(await res.text()).slice(0, 500)}`);
  }
  const data = await res.json();
  const text = data?.candidates?.[0]?.content?.parts?.[0]?.text;
  if (!text) {
    throw new Error(`Gemini response carried no text: ${JSON.stringify(data).slice(0, 500)}`);
  }
  return clean(text);
}

// The prompt asks for no dashes and no emojis; this makes it structural rather than a request.
function clean(text) {
  return text
    .replace(/\s*[\u2013\u2014]\s*/g, ', ')
    .replace(/\p{Extended_Pictographic}/gu, '')
    .replace(/[ \t]+$/gm, '');
}

// The text between ---NAME_START--- and ---NAME_END---, or null when the model skipped it.
function section(text, name) {
  const start = text.indexOf(`---${name}_START---`);
  const end = text.indexOf(`---${name}_END---`);
  if (start < 0 || end < start) return null;
  const m = text.slice(start + name.length + 12, end);
  return m.trim();
}

module.exports = { generate, clean, section, STYLE };

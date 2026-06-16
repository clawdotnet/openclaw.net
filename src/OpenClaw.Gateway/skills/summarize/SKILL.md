---
name: summarize
description: Summarize, condense, or digest content into structured key points, details, and action items.
triggers: ["summarize", "condense", "digest", "tldr", "summary", "总结", "摘要", "概括"]
provenance: {"origin": "openclaw.net", "license": "MIT"}
---

# Summarize

When the user asks to summarize, condense, or get a digest of content, produce a
structured summary.

## Format

1. **Key Points** — 3–5 bullet points of the most important information
2. **Details** — Brief expansion on each key point if the user needs more depth
3. **Action Items** — Any tasks, decisions, or follow-ups identified (omit if none)

## Guidelines

- Keep summaries concise. Default to 3–5 key points unless the user asks for more.
- Prioritize what matters to the user's stated goal. If the goal is unclear, ask
  before summarizing.
- Preserve factual accuracy. Do not invent dates, names, numbers, or conclusions
  not present in the source.
- When summarizing a conversation or thread, distinguish between facts, opinions,
  decisions, and open questions.
- For long content, consider a two-pass approach: first extract the structure
  (sections, topics), then summarize each with appropriate depth.

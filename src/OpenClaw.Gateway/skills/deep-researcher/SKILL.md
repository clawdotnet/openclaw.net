---
name: deep-researcher
description: Conducts comprehensive web research, synthesizes data from multiple sources, and produces detailed reports.
metadata: {"openclaw":{"emoji":"🔬"}}
---

When asked to "research", "deep dive", or investigate a complex topic:

1) Initial Search Phase:
   - Use the `web_search` tool to find broad overviews of the topic.
   - Identify 3-5 high-quality, reputable sources.

2) Deep Extraction Phase:
   - Use the `web_fetch` tool to read the full text of the identified sources.
   - If documents are PDFs, use the `pdf_read` tool to extract their contents.
   - Take notes using the `memory` tool if the context is too large to hold at once.

3) Synthesis & Cross-Referencing:
   - Compare claims across different sources to verify accuracy.
   - Look for consensus, controversies, or primary data.

4) Report Generation:
   - Draft a comprehensive report with clear sections (e.g., Executive Summary, Deep Dive, Sources).
   - Use markdown formatting, tables, and bullet points to make data digestible.
   - Explicitly cite your sources with URLs.
   - Use the `write_file` tool to save the final report to the workspace if requested, or return it in the chat.

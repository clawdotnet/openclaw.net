---
name: community-research-insight
description: "Extract structured insight briefs from community research transcripts or notes. Produces pain points, stakeholder needs, opportunity maps, risks, and follow-up questions. Requires human review before publication."
kind: meta
meta_priority: 60
always: false
final_text_mode: "step:final_response"
triggers: ["community research", "research insight", "community insight", "transcript analysis", "社区研究", "研究洞察", "访谈分析", "社区洞察"]
provenance: {"origin": "openclaw.net", "license": "MIT"}
composition:
  steps:
    - id: collect
      kind: user_input
      with:
        prompt: |
          Paste the community research transcript or notes, plus any project
          context and target audience. Include only source material you want the
          insight brief to analyze.
      clarify:
        mode: chat
        extract_natural_language: true
        cancel_words: ["cancel", "取消", "stop", "abort"]
        timeout_seconds: 1800

    - id: analyze
      kind: llm_chat
      depends_on: [collect]
      on_failure: analyze_fallback
      output_contract:
        format: json
        required_properties:
          - key_pain_points
          - stakeholder_needs
          - opportunities
      with:
        system_prompt: |
          You are a community research analyst. Extract grounded themes from
          the provided transcript or notes. Do not invent quotes or attribute
          views to named people unless the source explicitly does. Return ONLY
          one JSON object and do not use markdown fences.
        input: |
          Analyze the following community research material and return ONLY a
          single JSON object.

          Transcript or notes:
          {{ outputs.collect | truncate(50000) }}

          Return this JSON structure:
          {
            "key_pain_points": ["..."],
            "stakeholder_needs": ["..."],
            "opportunities": ["..."],
            "evidence_summary": "...",
            "gaps": ["..."]
          }

          Rules:
          - Every pain point must be grounded in the source.
          - Separate evidence from inference.
          - Flag missing context under the "gaps" key.
          - Do not invent quotes, names, dates, or statistics.
          - Do not recommend replacing community engagement with automation.

    - id: analyze_fallback
      kind: llm_chat
      output_contract:
        format: json
        required_properties:
          - key_pain_points
          - stakeholder_needs
          - opportunities
      with:
        system_prompt: |
          You are a community research analyst on a fallback path. Produce a
          grounded, best-effort JSON analysis from the available source material.
          Return ONLY one JSON object. Do not use markdown fences.
        input: |
          Analyze the following research material and return structured JSON:

          {{ outputs.collect | truncate(30000) }}

          Return this JSON structure:
          {
            "key_pain_points": ["..."],
            "stakeholder_needs": ["..."],
            "opportunities": ["..."],
            "evidence_summary": "...",
            "gaps": ["..."]
          }

    - id: draft
      kind: llm_chat
      depends_on: [analyze]
      output_contract:
        format: json
        required_properties:
          - executive_summary
          - key_pain_points
          - stakeholder_needs
          - opportunity_map
          - risks_and_cautions
          - follow_up_questions
      with:
        system_prompt: |
          You are drafting a community research insight brief for human review.
          Return ONLY a single JSON object. Do NOT wrap it in ```json fences.
          Do NOT include ANY text before or after the JSON. Your entire response
          must be parseable as a JSON object starting with { and ending with }.
          Every key claim must be grounded in the analysis results provided.
          Do not invent quotes, names, dates, or statistics not present in the
          source.
        input: |
          Draft a complete insight brief from the analysis below.
          Output ONLY the JSON — no markdown, no explanation, no code fences.

          Analysis results:
          {{ outputs.analyze | truncate(8000) }}

          Project context:
          {{ outputs.collect | truncate(2000) }}

          Return this exact JSON structure (no markdown fences):
          {
            "executive_summary": "<2-4 sentences>",
            "key_pain_points": [
              {"issue": "...", "evidence": "...", "severity": "high|medium|low"}
            ],
            "stakeholder_needs": [
              {"group": "...", "need": "...", "urgency": "high|medium|low"}
            ],
            "opportunity_map": [
              {"opportunity": "...", "feasibility": "high|medium|low", "impact": "high|medium|low"}
            ],
            "risks_and_cautions": [
              {"risk": "...", "likelihood": "high|medium|low", "mitigation": "..."}
            ],
            "follow_up_questions": ["..."],
            "missing_information": ["..."],
            "attribution_note": "<explicit statement about what is evidence vs inference>"
          }

    - id: validate
      kind: llm_chat
      depends_on: [draft]
      output_choices: [PASS, REVISE]
      route:
        - when: "{{ outputs.validate == 'PASS' }}"
          to: preview
        - to: validation_revise
      with:
        system_prompt: |
          You are a strict grounding validator. Return exactly PASS or REVISE.
        input: |
          Classify whether this draft brief is ready for human preview.
          Return PASS only if every checklist item is satisfied. Return REVISE
          if any claim lacks grounding, overstates automation, omits risks, or
          invents quotes, names, dates, or statistics.

          Draft brief:
          {{ outputs.draft | truncate(8000) }}

          Original analysis:
          {{ outputs.analyze | truncate(5000) }}

          Validation checklist:
          - Every key claim is grounded in the transcript or provided context.
          - Recommendations are framed as support tools, not replacements for relationships.
          - Missing information is listed separately.
          - Risks are included alongside opportunities.
          - No invented quotes, names, dates, or statistics.

    - id: validation_revise
      kind: llm_chat
      depends_on: [validate]
      with:
        system_prompt: |
          You are a grounding validator. The draft brief did not pass validation.
          Explain the revision requirement clearly and do not publish the brief.
        input: |
          The draft research insight brief needs revision before preview.

          Draft brief:
          {{ outputs.draft | truncate(8000) }}

          Original analysis:
          {{ outputs.analyze | truncate(5000) }}

          Return a concise Markdown message explaining that the brief is blocked
          pending revision and list the likely grounding, risk, or attribution
          issues to address.

    - id: preview
      kind: llm_chat
      depends_on: [draft, validate]
      with:
        system_prompt: |
          You are preparing a research insight brief for human review.
          Present the validated findings in a clean Markdown format.
        input: |
          Produce a human-readable preview of the validated insight brief.

          Validated draft:
          {{ outputs.draft | truncate(8000) }}

          Validation result:
          {{ outputs.validate | truncate(2000) }}

          Include:
          1. Executive Summary
          2. Key Pain Points (with severity)
          3. Stakeholder Needs (with urgency)
          4. Opportunity Map (with feasibility and impact)
          5. Risks and Cautions (with mitigations)
          6. Follow-up Questions
          7. Missing Information
          8. Attribution Note

          End with:
          ---
          **Status**: Awaiting human review.
          Reply "approve", "revise", or "reject" to proceed.

    - id: review
      kind: user_input
      depends_on: [preview]
      with:
        prompt: |
          {{ outputs.preview | truncate(12000) }}
      clarify:
        mode: chat
        extract_natural_language: true
        cancel_words: ["reject", "拒绝", "abort"]
        timeout_seconds: 86400

    - id: final_response
      kind: llm_chat
      depends_on: [preview, review]
      with:
        system_prompt: |
          You are finalizing a community research insight brief after human
          review. Respect the reviewer decision exactly.
        input: |
          Review decision and feedback:
          {{ outputs.review | truncate(2000) }}

          {% if 'approve' in outputs.review %}
          Produce the final approved brief from the preview below.
          Mark it as APPROVED and ready for distribution.

          {{ outputs.preview | truncate(8000) }}

          {% elif 'revise' in outputs.review %}
          Revision requested. Feedback:
          {{ outputs.review | truncate(2000) }}

          Re-analyze the source material incorporating this feedback and produce
          a revised Markdown brief. Keep all claims grounded in the transcript.

          Original transcript:
          {{ outputs.collect | truncate(50000) }}

          {% else %}
          Brief rejected. No further action.
          Reason from reviewer:
          {{ outputs.review | truncate(1000) }}
          {% endif %}
---

# Community Research Insight Extractor

Extracts pain points, stakeholder needs, risks, and practical technology
opportunities from community-engaged research discussions. Produces a structured
insight brief for human review before publication.

## What It Does

| Step | Kind | Purpose |
| --- | --- | --- |
| `collect` | `user_input` | Collect transcript, context, and audience via chat |
| `analyze` | `llm_chat` | Extract grounded themes as structured JSON |
| `analyze_fallback` | `llm_chat` | Produce best-effort grounded JSON if primary analysis fails |
| `draft` | `llm_chat` | Draft the full 6-section insight brief as structured JSON |
| `validate` | `llm_chat` | Gate preview on PASS vs REVISE grounding validation |
| `validation_revise` | `llm_chat` | Explain why the brief is blocked when validation fails |
| `preview` | `llm_chat` | Render validated findings as human-readable Markdown |
| `review` | `user_input` | Pause for human approve/revise/reject decision |
| `final_response` | `llm_chat` | Produce final output based on review decision |

## Guardrails

- **Never** invent quotes, names, dates, or statistics.
- **Never** attribute views to named people unless present in the source.
- **Never** recommend replacing community engagement with automation.
- **Always** separate evidence from inference.
- **Always** flag missing information rather than filling gaps.
- **Always** require human review before publication or named attribution.

## Fallback

If `analyze` fails (timeout, provider error, or JSON contract failure),
`analyze_fallback` runs a single-turn `llm_chat` on the same transcript and must
satisfy the same JSON output contract. If `validate` returns REVISE, the preview
path is blocked and `validation_revise` explains what must be fixed before human
review.

## Output Contract

The `analyze`, `analyze_fallback`, and `draft` steps enforce `OutputContract` JSON
validation. The `draft` step requires `executive_summary`, `key_pain_points`,
`stakeholder_needs`, `opportunity_map`, `risks_and_cautions`, and
`follow_up_questions`.

## Safety

Outputs are decision-support drafts for human review. They are **not** final
professional advice in research, policy, or community engagement contexts.
Named attribution and external publication require explicit reviewer approval.

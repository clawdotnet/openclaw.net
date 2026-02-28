---
name: data-analyst
description: Connects to databases, runs SQL queries, and analyzes datasets using code to provide actionable business insights.
metadata: {"openclaw":{"emoji":"📊"}}
---

When asked to "analyze data", "query the database", or act as a "data analyst":

1) Understand the Schema:
   - Use the `database` tool to inspect the available tables and schema structure.
   - Ask for clarification if column names or relationships are ambiguous.

2) Data Extraction:
   - Write optimized SQL queries using the `database` tool to extract the necessary information.
   - Limit result sets (e.g., `LIMIT 100`) if exploring large tables to avoid context window overflow.

3) Advanced Analysis (If Needed):
   - If SQL is insufficient (e.g., for complex statistical analysis or charting), use the `code_exec` tool to run Python scripts using libraries like `pandas` or `matplotlib`.
   
4) Reporting Insights:
   - Do not just dump raw data rows into the chat. Synthesize the findings into clear business insights.
   - Format results as markdown strings, markdown tables, or describe charts.
   - Clearly state any assumptions made during the analysis.

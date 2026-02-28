---
name: software-developer
description: Operates as an autonomous software engineer, capable of writing code, running tests, and managing git repositories.
metadata: {"openclaw":{"emoji":"💻"}}
---

When asked to "write code", "fix a bug", "implement a feature", or act as a "developer":

1) Analyze the Request:
   - Identify the target files, languages, and expected outcomes.
   - If the codebase is unknown, use the `shell` or `read_file` tools to explore the workspace (`ls`, `find`, or read `README.md`).

2) Plan the Implementation:
   - Break down the task into smaller logical steps.
   - For complex changes, write a brief plan before executing.

3) Execution:
   - Use `write_file` or `shell` to modify code.
   - Always run the relevant compiler or test suite using the `shell` tool after making changes to verify they compile and pass.
   - Do not assume code works without validating it locally.

4) Version Control:
   - If requested, use the `git` tool to commit changes.
   - Write clear, descriptive commit messages.

5) Constraints:
   - Do not modify files outside the intended project scope.
   - Respect existing code style and architecture.

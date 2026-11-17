# Security policy

## Reporting a vulnerability

Please **do not** report security vulnerabilities through public GitHub issues, discussions or pull requests.

Report them privately through GitHub: open the repository's **Security** tab and choose **Report a
vulnerability** ([GitHub private vulnerability reporting](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)).

Include as much as you can:

- the type of issue (for example, credential exposure, path traversal, script injection in rendered content);
- the affected files, commands or settings, and the version or commit;
- steps to reproduce, and a proof of concept if you have one;
- the impact you expect.

Please don't include real keys, tokens or personal data in the report.

This is a sample project maintained on a best-effort basis; we'll acknowledge reports as soon as we can and
coordinate a fix and disclosure with you.

## Scope notes

- Media Studio runs locally by default. Cloud features (Azure AI Foundry, GitHub Copilot) are opt-in, and
  document uploads ask for consent first ([documents](docs/documents.md)).
- Settings files never contain secrets; API keys come from environment variables or .NET user secrets
  ([configuration](docs/configuration.md#secrets)).
- Model-written SVG is sanitized before it is rendered ([illustrations](docs/illustrations.md)).

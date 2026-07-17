# Security Policy

## Reporting a vulnerability

Do not open a public GitHub issue for a security vulnerability. Use GitHub's private vulnerability
reporting instead: go to the **Security** tab of this repository → **Report a vulnerability**.

This sends the report privately to the maintainer and creates a private draft security advisory —
it is not visible to the public until a fix is ready.

## Scope

This covers the extensions shipped in this monorepo — currently Renamer and WhisparrSync, and any
extension listed in [`extensions/catalog.json`](extensions/catalog.json), which is the source of
truth for what is in scope. It does not cover Cove core itself — report Cove core vulnerabilities to
that project directly.

## Supported versions

Only the latest released version of each extension receives security fixes.

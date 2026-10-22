# Security policy

Please report security issues privately to the project maintainers rather than
opening a public issue.

Relevant examples include:

- account or refresh-token disclosure
- path traversal in downloaded archives
- unverified executable downloads
- authentication flow mistakes

Kumo never asks for a Microsoft password. Account refresh tokens are
encrypted with Windows DPAPI and short-lived Minecraft access tokens are kept
in memory only.

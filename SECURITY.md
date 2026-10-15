# Security policy

## Supported versions

The library is currently in preview. Security fixes target the latest preview on `main`; there is no long-term support commitment for older preview packages.

## Reporting a vulnerability

Use GitHub's **Report a vulnerability** action on the repository Security tab when it is available. If it is unavailable, contact the repository owner privately using an established private channel and request a secure reporting channel before sharing details. Do not open a public issue containing an exploit or credentials.

Include affected package versions, a sanitized reproduction and the expected security boundary. Never include live API keys, access tokens, private prompts, Session records or private media. If a key was exposed, revoke or rotate it with the upstream provider.

## Credentials and Session data

The caller supplies upstream credentials, HTTP configuration, cache and Session coordination. Keep credentials outside source control and restrict access to the cache and logs. Provider response snapshots can contain sensitive content and require the same handling as the original request. A credential fingerprint is not a replacement for secure cache access.

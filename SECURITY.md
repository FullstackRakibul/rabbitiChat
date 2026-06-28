# Security Policy

## Supported Versions

| Version | Supported |
| ------- | --------- |
| latest  | ✅        |

## Reporting a Vulnerability

If you discover a security vulnerability in RabbitIC, please **do not open a public issue**.

Instead, report it privately by emailing:

**rakibul.979.hasan@gmail.com**

Include the following in your report:
- A description of the vulnerability
- Steps to reproduce
- Potential impact
- Any suggested fix (optional)

You can expect an acknowledgement within **48 hours** and a resolution or status update within **7 days**.

## Scope

This project runs entirely locally (backend + Ollama). The main attack surface to be aware of:

- The backend API binds to `localhost` only — do not expose it to untrusted networks.
- Conversation history is stored in a local SQLite database (`rabbitic.db`). Protect this file accordingly.
- No authentication is implemented by default — this is by design for a local-only tool.

## Out of Scope

- Issues that require physical access to the machine
- Vulnerabilities in Ollama itself (report those to the [Ollama project](https://github.com/ollama/ollama))
- Vulnerabilities in third-party dependencies (report those upstream)

# Contributing

Issues and pull requests are welcome.

1. Install the .NET 10 SDK on Windows.
2. Run `scripts\run-quick-qa.ps1` before opening a pull request.
3. Add or update tests for behavior changes.
4. Use synthetic test data. Never commit real profiles, logs, reports or hardware audits.

Changes must keep these rules:

- Add no writes to security features, services, drivers, firmware or game files other than verified profile settings.
- Never present estimates as measurements, and show unavailable data as unavailable.
- Add every user-facing string to both `tr.json` and `en.json`.
- Keep the application a self-contained single file with no runtime installs.

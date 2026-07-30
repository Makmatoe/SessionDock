## Summary

Describe the user-visible change and link the approved issue.

## Safety and privacy

- [ ] I did not add credentials, cookies, launch tickets, private-server codes,
      local account data, signing material, or generated release artifacts.
- [ ] New network traffic, persistence, process control, browser behavior,
      dependencies, and update/signing changes are explained below.
- [ ] Roblox network traffic remains limited to official Roblox endpoints.
- [ ] Optional local integrations remain loopback-only and opt-in.

Safety/privacy notes:

## Validation

- [ ] `./scripts/Build.ps1 -Configuration Release -Runtime win-x64 -CI`
- [ ] If `discord-release-bot` changed: `npm ci`, `npm test`, and
      `npm run check` pass from that directory without real Discord traffic;
      `npm audit --omit=dev --audit-level=moderate` reports no findings.
- [ ] Relevant manual behavior was exercised on Windows x64.
- [ ] Tests and documentation were added or updated where needed.

Validation details:

## Screenshots

Include sanitized screenshots only when useful. Remove account names, IDs,
private-server data, and local paths containing personal information.

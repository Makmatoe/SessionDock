# Responding to a Microsoft Defender malware detection

A named Microsoft Defender Antivirus detection such as
`Trojan:Win32/Wacatac.B!ml` is a release-blocking security event. It is not the
same as an **Unknown publisher** or Microsoft Defender SmartScreen reputation
warning.

## For users

1. Leave the detected file quarantined or removed. Do not restore it, add an
   exclusion, disable Defender, or strip its Internet-zone metadata.
2. Open **Windows Security > Virus & threat protection > Protection history**
   and record the detection name, affected path, time, action, and security
   intelligence version.
3. If the file ran before it was detected, disconnect that device from the
   network and run **Microsoft Defender Offline scan** before using it again.
4. If the file did not run, keep it quarantined and report the exact download
   URL and file hash to the SessionDock maintainer.
5. Install a replacement only from the canonical release page after Microsoft
   has reviewed the detection and the replacement has a valid, trusted
   publisher signature. Do not install executable attachments from Discord.

The following commands are read-only and can be pasted directly into Windows
PowerShell; they do not require running a downloaded script:

```powershell
Get-MpComputerStatus |
  Select-Object AMProductVersion, AMEngineVersion, AntivirusSignatureVersion,
    AntivirusSignatureLastUpdated, RealTimeProtectionEnabled, IsTamperProtected

Get-MpThreatDetection |
  Select-Object InitialDetectionTime, LastThreatStatusChangeTime, ActionSuccess,
    ThreatID, Resources
```

If Defender has not removed the download, calculate its hash without opening
it:

```powershell
Get-FileHash -LiteralPath .\SessionDock.exe -Algorithm SHA256
```

Microsoft documents Defender's
[threat-response guidance](https://support.microsoft.com/windows/help-protect-my-pc-with-microsoft-defender-offline-9306d528-64bf-4668-5b80-ff533f183d6c)
and provides the supported
[malware-analysis submission portal](https://www.microsoft.com/en-us/wdsi/filesubmission).

## For maintainers

1. Stop distribution and preserve the exact reported bytes as evidence.
2. Compare SHA-256 hashes between the canonical build and downloaded file.
   A mismatch is a possible transport or artifact-selection incident. A match
   rules out byte-level transport modification but does not by itself prove a
   false positive.
3. Reproduce using a real Internet-origin download on a clean, fully updated
   Windows test device. Do not make the test pass by removing Mark of the Web.
4. Submit the exact detected file to Microsoft as a **Software developer**,
   choose **Incorrectly detected as malware/malicious**, include the detection
   and security intelligence version, and retain the submission ID and final
   determination.
5. Audit the clean source commit, dependencies, build provenance, executable
   behavior, package inventory, signatures, SBOM, and checksums.
6. Release only from a clean reviewed commit. Every first-party PE and the
   installer must be Authenticode-signed and RFC 3161 timestamped by the same
   publicly trusted publisher identity. Verify those signatures again after
   downloading the staged release.
7. Distribute executables only through the canonical HTTPS release or Microsoft
   Store. Discord announcements may link to that release but must not attach an
   executable or ZIP.

Defender's
[Block at First Sight](https://learn.microsoft.com/defender-endpoint/configure-block-at-first-sight-microsoft-defender-antivirus)
uses cloud heuristics, machine learning, and automated analysis for files that
originate from the Internet zone. Microsoft also explains that unsigned files
start with no transferable publisher reputation in its
[SmartScreen guidance for Windows developers](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation).

## 2026-08-04 r3 incident record

The executable reported from the Discord CDN was streamed directly into a
SHA-256 calculation without saving or executing it. It was byte-identical to
the local SessionDock integrated-test r3 executable:

- SHA-256:
  `8041C3268B698A2F964654B7B0C5F67BC2B2B1035E44AB767D9074F498612B28`
- Size: `78,382,284` bytes
- Authenticode status: `NotSigned`
- Local Defender trust check: `not a known good file`
- Laptop detection: `Trojan:Win32/Wacatac.B!ml`

Transport modification was therefore ruled out. The r3 binary remains blocked
from redistribution pending Microsoft's determination. It was a compressed,
self-extracting development bundle built from an uncommitted worktree, not a
canonical signed release.

## Distribution hold and replacement audit

On 2026-08-04 the immutable public v3.0.0 binary release was withdrawn. Its
files were not byte-identical to the detected r3 development build, but they
were also unsigned and used the retired compressed single-file layout. Before
withdrawal, all ten assets were preserved locally and matched GitHub's recorded
SHA-256 digests. The `v3.0.0` source tag remains. GitHub's current latest entry
is the immutable, zero-asset `security-hold-20260804` notice, so the stable
latest-download path cannot return an executable.

The current transparent local validation publish is not a release and must not
be distributed. It contains 555 files and 545 structurally valid PE files:

- 539 pinned runtime PEs have valid Microsoft Authenticode signatures;
- exactly six reviewed application/Velopack PEs await the SessionDock publisher
  signature;
- there are no malformed PEs or other signature states;
- `SessionDock.exe` is a 187,904-byte app host with no appended bundle overlay;
- a full Microsoft Defender no-remediation scan found no threats; and
- Defender TrustCheck still reports the app host as not known-good while it is
  unsigned.

That local scan is useful evidence, not a safety guarantee and not permission
to bypass Defender. Public replacement remains blocked until a publicly trusted
publisher identity signs the exact six files and final Setup, Microsoft reviews
the reported sample, and every source-provenance and release gate passes.

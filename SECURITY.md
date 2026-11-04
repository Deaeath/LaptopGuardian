# Security

## Reporting a problem

Please use GitHub's [private vulnerability reporting](https://github.com/Deaeath/LaptopGuardian/security/advisories/new) rather than a public issue.

## How updates work

LaptopGuardian updates itself from this repository's latest release. It downloads `LaptopGuardian.exe` over HTTPS and compares it with the release's `LaptopGuardian.exe.sha256` before replacing anything.

The hash catches a damaged download. It does not protect against a compromised GitHub account, because the hash comes from the same place as the file, and the program is not code-signed yet. You can turn automatic updates off in Settings.

## How releases are built

Releases are built by GitHub Actions from the tagged source, with a [build attestation](https://docs.github.com/en/actions/security-guides/using-artifact-attestations-to-establish-provenance-for-builds) you can check:

```
gh attestation verify LaptopGuardian.exe --repo Deaeath/LaptopGuardian
```

Version 2.1.0 predates this: it was built on the maintainer's PC.

## What it needs

It runs with your normal rights. Only these ask Windows for permission, each when you click them: clearing stuck crash reports, installing driver updates from Windows Update, turning on hibernation, and creating the sleep study. The elevated helper deletes files only inside Windows' own error-report folders.

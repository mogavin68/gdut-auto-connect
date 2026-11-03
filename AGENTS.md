# Development and release policy

AI generated; no maintenance is promised.

Every change must first be committed and pushed to the private development `origin`. Confirm with the GitHub API that `origin` is private. The `public` remote is the reviewed public distribution repository. Do not push changes there until build, tests, source/data review and release integrity checks pass.

Never commit credentials, user settings, recharge records, orders, payment codes, cookies, tokens, actual screenshots, portal captures, diagnostic reports, logs, SDKs or caches. Review every file and reachable history that would become public, along with existing release attachments. Build release binaries from reviewed sources with debugging symbols disabled and build paths mapped to a generic path.

The application updater uses only the fixed public repository, without tokens. Keep explicit user confirmation before replacing and restarting, integrity checks, previous-version recovery and the update-helper protocol. Do not generate recharge orders or payments as part of update tests. Use isolated installation fixtures, preserving the actual user's configuration.

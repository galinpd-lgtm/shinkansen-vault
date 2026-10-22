# Security / Сигурност

> Кратко на български: Shinkansen Vault пази тайните шифровани и изисква Windows Hello за всяко използване. Но AI агентът работи като **същия Windows потребител**, затова „агентът не вижда тайните“ е гарантирано **по дизайн** (няма команда за показване, изходът се маскира, файловете отиват там, където агентът е инструктиран да не чете), **а не криптографски**. Човекът трябва да чете диалога за съгласие преди да въведе PIN.

## Reporting a vulnerability

Please open a private security advisory on GitHub (Security → Report a vulnerability) rather than a public issue.

## What the vault protects

| Asset | Protection |
|---|---|
| Secret values at rest (`vault.svlt`) | AES-256-GCM per entry, random 256-bit master key, AAD binds each entry to the vault id and its reference (entries cannot be swapped). |
| Master key | Wrapped with AES-256-GCM under KEK = HKDF-SHA256(Hello signature over a per-vault challenge, per-vault salt), then wrapped again with DPAPI CurrentUser. |
| Each use of a value | Consent dialog (action, reason, references, exact command or target file), then a Windows Hello prompt. Nothing is cached: every `run`, `render`, `clip` and `add` asks again. |
| Audit trail | `audit.jsonl`: time, action, references, target, reason, approved/denied/failed. Never values. |

### Key derivation detail

1. `init` creates a Windows Hello key (`KeyCredentialManager.RequestCreateAsync`) named `ShinkansenVault-<vaultId>`. It is an RSA-2048 key held by the TPM (or Hello's software container if no TPM).
2. A random 32-byte challenge and 32-byte salt are stored in the vault file.
3. Unlock: `RequestSignAsync(challenge)` shows Windows Hello. RSA PKCS#1 v1.5 signatures are deterministic, so the same 256-byte signature is produced every time. `init` signs twice and refuses to create the vault if the two signatures differ.
4. KEK = HKDF-SHA256(ikm = signature, salt, info = "ShinkansenVault/KEK/v1").
5. The master key is sealed with the KEK, and the result is protected with DPAPI CurrentUser.

This is the same idea used by KeePassXC's Windows Hello quick unlock and similar plugins. The signature is a secret only as long as the Hello key cannot sign without the user; anyone who can make the Hello key sign the challenge can derive the KEK.

## Threat model — honest version

### The agent runs as the same Windows user

This is the key limitation. Claude Code (or any agent) runs under your Windows account. Windows offers no boundary between two processes of the same user. Therefore:

- **"The agent never sees secrets" is enforced by design, not by cryptography.** The mechanisms are:
  - there is no command that prints a value (`list` shows only refs/labels; `run` masks output; `render` prints only a confirmation; `clip` prints nothing);
  - values are never accepted as arguments or from stdin (`add` uses a GUI dialog);
  - output of `run` is masked with `••••` (exact matches only);
  - rendered files go to paths the agent is told not to read (see `docs/AGENT_GUIDE.md`).
- **The human is the real control.** The consent dialog shows the exact command or output path. A malicious or confused agent can request things like `cmd /c echo %PASS% > out.txt` or `render` into a file it then reads. If you approve that, the agent can see the value. **Read the dialog before entering your PIN.**
- A process running as you could, in principle, read the memory of `svault.exe` or of the child process while a value is in use, inject keystrokes into the consent dialog, or read the clipboard during the 20 s window. Windows Hello itself still requires your PIN/biometric, which the agent cannot supply.
- DPAPI CurrentUser alone would not stop a same-user process; that is why the Hello layer exists. DPAPI adds: the vault file is useless on another machine or Windows account.

### Masking limits

- The mask is `••••` when the output encoding supports it, otherwise `****` (e.g. a console on code page 437).
- Only exact, literal matches are masked. Encoded forms (base64, URL-encoding, hex, reversed, split across output with other characters, partially printed) are **not** masked.
- Output is processed as text in the console's encoding; binary output may not be masked.
- Up to (longest secret length − 1) characters of output are held back until more output arrives or the process ends, so interactive prompts may appear late.
- The child process may write the value to files, logs, or the network. That is outside the vault's control.

### Memory hygiene

- Keys, signatures and decrypted byte arrays are zeroed right after use (`CryptographicOperations.ZeroMemory`).
- Values must become .NET `string`s for environment variables, template rendering, masking and the clipboard; strings are immutable and cannot be wiped, so they remain in memory until the garbage collector reuses it. The process is short-lived.
- The `add` dialog's WinForms `TextBox` holds the typed value as a string briefly.

### Other notes

- Labels and references are stored **in plaintext** in `vault.svlt` (so `list` works without Hello). Don't put secrets in labels or ref names.
- The audit log is append-only by convention, not tamper-proof: a same-user process can edit it.
- The clipboard copy is marked to be excluded from Windows clipboard history and cloud clipboard; clipboard managers that ignore these flags may still record it. It is cleared after 20 s only if it still contains the value.
- If Windows Hello is reset (PIN reset that removes keys, TPM cleared, Windows reinstalled) the Hello key is lost and **the vault cannot be opened**. There is no recovery key in this MVP. Keep your own backup of important secrets elsewhere.
- No network code exists in the program. Verify by reading the source; build it yourself with `dotnet publish`.

## Out of scope for the MVP

Recovery keys, multiple devices, secret rotation, removing entries, running the agent as a separate Windows user (which would give a real OS boundary and is the recommended future hardening).

# Agent guide / Правила за AI агенти

Short rules for Claude Code and other agents that work with Shinkansen Vault. Copy this into your project's `CLAUDE.md` if useful.

## Rules

1. **Use references, never values.** Secrets are named like `vault://project/name`. Run `svault list` to see what exists. Never ask the human to paste a password into the chat.
2. **Never try to see a value.** Do not print, echo, log, base64-encode, write to a file you will read, or send a secret anywhere it can come back to you. Do not try to bypass the `••••` masking.
3. **Always give an honest, specific `--reason`.** The human reads it before entering the PIN. Example: `--reason "Deploy varnahub.bg via FTP"`, not `--reason "needed"`.
4. **Inject into a process:**
   ```powershell
   svault run --env FTP_PASS=vault://varnahub/ftp --reason "Upload site build" -- python deploy.py
   ```
   The program reads `FTP_PASS` from its environment. Keep the command minimal and readable; avoid `cmd /c` chains that could redirect the value.
5. **Write config files with `render`:**
   ```powershell
   svault render config/app.env.template C:\deploy\app.env --reason "Create production .env"
   ```
   Template placeholders look like `{{vault://varnahub/db}}`. **Do not read the rendered output file afterwards** (no `cat`, `type`, `Read` tool, `git diff`, upload or attach). Only the target program may read it. Never render into the repo or a folder you routinely read; never commit it.
6. **Clipboard is for the human.** `svault clip vault://ref --reason "..."` is for the human to paste into a browser or app. Do not read the clipboard.
7. **Never ask for a secret in chat.** If you need one that `vault_list` / `svault list` doesn't have, call the MCP tool `vault_add` (ref, label, reason): a dialog opens and the human types the value; you get only the reference. Without MCP, ask the human to run `svault add vault://x/y --label "..."`. There is no way to pass a value as an argument or via stdin; don't try.
8. **If denied (exit code 5), stop and ask.** Do not retry in a loop.
9. **Google sync is human-only.** `svault import-google` / `export-google` handle a plain-text CSV with every password. Never open, read, copy, move or commit such a file; never run these commands yourself unless the human asks, and let the human pick the file in the dialog.
10. `svault log` shows the audit trail; you may read it (it never contains values).

## MCP tools

When the `shinkansen-vault` MCP server is connected you have `vault_list`, `vault_add`, `vault_run` (`env` map, `command` array — not a shell — and `reason`), `vault_render`, `vault_check` (weak/reused passwords, names only), `vault_rotate` (`ref`, `reason`: the human pastes a generated password on the website and confirms; open the site's change-password page for them first) and `vault_log`. A result starting with `DENIED:` means the human said no: stop and ask. If the vault is locked, the dialog may take a while: the human has to read it and enter a PIN. Once they approve, the vault stays unlocked until they sign out, restart or run `svault lock` — do not ask them to approve again, and do not lock it yourself. Every use is still logged with your reason.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | OK (for `run`: the child's exit code is passed through) |
| 2 | Error (no vault, unknown ref, unlock failed, ...) |
| 5 | Denied or cancelled by the human |
| 64 | Usage error |

## Накратко на български

Използвай само референции `vault://...`. Никога не искай пароли в чата — ползвай `vault_add` (MCP) и човекът я въвежда в прозореца. Никога не се опитвай да видиш стойност. Винаги давай честна и конкретна `--reason`. Не чети файлове, създадени с `render`. Клипбордът е за човека. Добавянето на тайни е работа на човека. При отказ (код 5) спри и попитай.

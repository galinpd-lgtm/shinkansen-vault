# Shinkansen Vault

**[Български](#български) · [English](#english)**

---

## Български

Shinkansen Vault (`svault.exe`) е малък трезор за пароли за Windows, отключван с **Windows Hello** (PIN, пръстов отпечатък или лице). Създаден е така, че AI агент (напр. Claude Code) да може да **използва** тайни, без да ги **вижда**.

- Агентът знае само референции, напр. `vault://varnahub/cpanel`.
- Всяко използване на истинска стойност минава през диалог за съгласие + Windows Hello.
- Самият трезор поставя стойността там, където трябва: в средата на процес, във файл или в клипборда за 20 секунди.
- **Няма команда, която отпечатва тайна** на екрана.
- Без сървър, без облак, без телеметрия, без мрежови заявки. Всичко е на вашия компютър.

### Команди

| Команда | Какво прави |
|---|---|
| `svault init` | Създава трезора и регистрира ключ в Windows Hello. |
| `svault add vault://проект/име [--label "текст"] [--replace]` | Отваря прозорец, в който **човекът** въвежда стойността. Никога като аргумент или през stdin. |
| `svault list` | Само референции и етикети, никога стойности. |
| `svault run --env ИМЕ=vault://реф [...] --reason "защо" -- команда ...` | След съгласие + Hello пуска командата с тайните в нейната среда. Всяка тайна в изхода се заменя с `••••` (или `****`, ако конзолата не поддържа този знак). |
| `svault render шаблон изход --reason "защо"` | Заменя `{{vault://реф}}` в шаблона и записва резултата директно във файла. Показва само потвърждение. |
| `svault clip vault://реф --reason "защо"` | Копира в клипборда за 20 s (за поставяне от човека), после го изчиства. |
| `svault log [--tail N]` | Одитен дневник (JSONL): време, референции, команда/цел, причина, разрешено/отказано. Никога стойности. |

Файлове: `%LOCALAPPDATA%\ShinkansenVault\vault.svlt` и `audit.jsonl` (папката може да се смени с променливата `SVAULT_HOME`).

### Как работи криптографията (накратко)

- Всеки запис е шифрован с **AES-256-GCM** с произволен 256-битов главен ключ.
- Главният ключ е обвит два пъти:
  1. с ключ, изведен (HKDF-SHA256) от **подписа на Windows Hello ключа** върху фиксирано предизвикателство за трезора. Hello ключът е RSA и живее в TPM; подписът PKCS#1 v1.5 е детерминистичен, затова дава един и същ ключ всеки път (същият подход като Windows Hello отключването в KeePassXC);
  2. с **DPAPI (CurrentUser)** отгоре.
- Подробности и честен модел на заплахите: [SECURITY.md](SECURITY.md).

### Компилирайте сами

Нужни: Windows 10 2004+ / Windows 11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), настроен Windows Hello PIN.

```powershell
git clone https://github.com/galinpd-lgtm/shinkansen-vault.git
cd shinkansen-vault
dotnet test
dotnet publish src/svault/svault.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
.\publish\svault.exe help
```

Сложете `publish\svault.exe` в папка от `PATH`. Готовите издания в GitHub Releases имат файл `SHA256SUMS.txt` за проверка (`Get-FileHash svault.exe`).

### Как го ползва AI агент

1. **Човекът** добавя тайна: `svault add vault://varnahub/cpanel --label "cPanel Varnahub"` и я въвежда в прозореца.
2. Агентът вижда само `svault list` и използва референцията:
   ```powershell
   svault run --env CPANEL_PASS=vault://varnahub/cpanel --reason "Качване на сайта" -- python deploy.py
   ```
3. Появява се прозорец с причината, тайните и командата. Човекът натиска „Разреши“ и въвежда PIN.
4. Командата получава паролата в `CPANEL_PASS`; ако я отпечата, агентът вижда `••••`.

Правила за агентите: [docs/AGENT_GUIDE.md](docs/AGENT_GUIDE.md). Ръчен тест на реален компютър: [docs/MANUAL_TEST.md](docs/MANUAL_TEST.md).

### Лиценз

GPL-3.0 (вижте [LICENSE](LICENSE)).

---

## English

Shinkansen Vault (`svault.exe`) is a small Windows password vault unlocked with **Windows Hello** (PIN, fingerprint or face). It is built so an AI agent (e.g. Claude Code) can **use** secrets without **seeing** them.

- The agent only knows references such as `vault://varnahub/cpanel`.
- Every use of a real value goes through a consent dialog plus Windows Hello.
- The vault itself puts the value where it is needed: a child process environment, a file, or the clipboard for 20 seconds.
- **There is no command that prints a secret.**
- No server, no cloud, no telemetry, no network calls. Everything stays on your PC.

### Commands

| Command | What it does |
|---|---|
| `svault init` | Creates the vault and registers a Windows Hello key. |
| `svault add vault://project/name [--label "text"] [--replace]` | Opens a dialog where the **human** types the value. Never as an argument or via stdin. |
| `svault list` | References and labels only, never values. |
| `svault run --env NAME=vault://ref [...] --reason "why" -- command ...` | After consent + Hello, runs the command with the secrets in its environment. Any secret in its output is replaced with `••••` (or `****` if the console cannot show that character). |
| `svault render template output --reason "why"` | Replaces `{{vault://ref}}` placeholders and writes the result straight to the output file. Prints only a confirmation. |
| `svault clip vault://ref --reason "why"` | Copies to the clipboard for 20 s (for the human to paste), then clears it. |
| `svault log [--tail N]` | Audit log (JSONL): time, refs, command/target, reason, approved/denied. Never values. |

Files: `%LOCALAPPDATA%\ShinkansenVault\vault.svlt` and `audit.jsonl` (override the folder with `SVAULT_HOME`).

### Crypto in short

- Each entry is encrypted with **AES-256-GCM** under a random 256-bit master key.
- The master key is wrapped twice:
  1. with a key derived (HKDF-SHA256) from the **Windows Hello key's signature** over a fixed per-vault challenge. The Hello key is RSA in the TPM; the PKCS#1 v1.5 signature is deterministic, so it yields the same key every time (same approach as KeePassXC's Windows Hello unlock);
  2. with **DPAPI (CurrentUser)** on top.
- Details and an honest threat model: [SECURITY.md](SECURITY.md).

### Build it yourself

Requires Windows 10 2004+ / Windows 11, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), and a Windows Hello PIN.

```powershell
git clone https://github.com/galinpd-lgtm/shinkansen-vault.git
cd shinkansen-vault
dotnet test
dotnet publish src/svault/svault.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
.\publish\svault.exe help
```

Put `publish\svault.exe` in a folder on your `PATH`. GitHub Releases include `SHA256SUMS.txt` (check with `Get-FileHash svault.exe`).

### How an AI agent uses it

1. **The human** adds a secret: `svault add vault://varnahub/cpanel --label "cPanel Varnahub"` and types it in the dialog.
2. The agent only sees `svault list` and uses the reference:
   ```powershell
   svault run --env CPANEL_PASS=vault://varnahub/cpanel --reason "Deploy the site" -- python deploy.py
   ```
3. A dialog shows the reason, the secrets and the command. The human clicks "Allow" and enters the PIN.
4. The command gets the password in `CPANEL_PASS`; if it prints it, the agent sees `••••`.

Agent rules: [docs/AGENT_GUIDE.md](docs/AGENT_GUIDE.md). Manual test on a real PC: [docs/MANUAL_TEST.md](docs/MANUAL_TEST.md).

### License

GPL-3.0 (see [LICENSE](LICENSE)).

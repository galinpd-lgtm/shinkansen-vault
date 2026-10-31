# Shinkansen Vault

**[Български](#български) · [English](#english)**

---

## Български

Shinkansen Vault (`svault.exe`) е малък трезор за пароли за Windows, отключван с **Windows Hello** (PIN, пръстов отпечатък или лице). Създаден е така, че AI агент (напр. Claude Code) да може да **използва** тайни, без да ги **вижда**.

- Агентът знае само референции, напр. `vault://varnahub/cpanel`.
- Първото използване след влизане в Windows минава през диалог за съгласие + Windows Hello. След това трезорът остава **отключен до излизане, рестарт или изключване** на компютъра (`svault lock` го заключва веднага) — не пита за всяка команда и за всяка нова сесия на агента. `export-google` пита винаги. Всяко използване се записва в журнала с причината.
- Самият трезор поставя стойността там, където трябва: в средата на процес, във файл или в клипборда за 20 секунди.
- **Няма команда, която отпечатва тайна** на екрана.
- Без сървър, без облак, без телеметрия, без мрежови заявки. Всичко е на вашия компютър.

### Команди

| Команда | Какво прави |
|---|---|
| `svault init` | Създава трезора и регистрира ключ в Windows Hello. |
| `svault add vault://проект/име [--label "текст"] [--replace]` | Отваря прозорец, в който **човекът** въвежда стойността. Никога като аргумент или през stdin. |
| `svault list` | Само референции и етикети, никога стойности. |
| `svault run --env ИМЕ=vault://реф [...] --reason "защо" -- команда ...` | Пуска командата (ако трезорът е заключен — след съгласие + Hello) с тайните в нейната среда. Всяка тайна в изхода се заменя с `••••` (или `****`, ако конзолата не поддържа този знак). |
| `svault render шаблон изход --reason "защо"` | Заменя `{{vault://реф}}` в шаблона и записва резултата директно във файла. Показва само потвърждение. |
| `svault clip vault://реф --reason "защо"` | Копира в клипборда за 20 s (за поставяне от човека), после го изчиства. |
| `svault mcp` | MCP сървър за AI агенти (виж по-долу). |
| `svault import-google [файл.csv]` | Внася износа от Google Password Manager: нови записи `vault://google/<сайт>`, сменените пароли се обновяват (старата остава като „предишна“), липсващите не се пипат. Накрая предлага да изтрие файла. |
| `svault export-google [файл.csv]` | Записва файл във формата на Google за внасяне в passwords.google.com и го изтрива, когато потвърдите. |
| `svault check` | Локална проверка: слаби и повторени пароли. Показва само имена. |
| `svault rotate vault://реф --reason "защо"` | Нова силна парола в клипборда → поставяте я на сайта → потвърждавате → записва се. Старата остава като „предишна“. |
| `svault log [--tail N]` | Одитен дневник (JSONL): време, референции, команда/цел, причина, разрешено/отказано. Никога стойности. |
| `svault status` | Казва дали трезорът е отключен за това влизане в Windows. |
| `svault lock` | Заключва веднага; следващото използване пак пита за съгласие + Windows Hello. |

Файлове: `%LOCALAPPDATA%\ShinkansenVault\vault.svlt` и `audit.jsonl` (папката може да се смени с променливата `SVAULT_HOME`).

### Синхронизация с Google Password Manager

Google няма програмен достъп до паролите, затова синхронизацията минава през неговия CSV файл:

1. **Google → трезор:** passwords.google.com → Настройки → *Изнасяне на паролите* → `svault import-google` → изберете файла → съгласие + PIN → „Изтрий файла“.
2. **Поправка:** `svault check` показва слабите и повторените; за всяка парола `svault rotate vault://google/<сайт>`. Когато смените паролата в Chrome, той предлага да я обнови и в Google — така двете копия остават еднакви.
3. **Трезор → Google** (напр. пароли, създадени от трезора): `svault export-google` → Настройки → *Внасяне на пароли* → „Изтрий файла“.

CSV файлът съдържа паролите в явен вид. Дръжте го в локална папка, която **не се синхронизира** (OneDrive, Nextcloud, Syncthing), и го изтривайте веднага.

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
3. Ако трезорът е заключен (първо използване след влизане в Windows), се появява прозорец с причината, тайните и командата; човекът натиска „Разреши“ и въвежда PIN. След това — без питане до излизане/рестарт/изключване.
4. Командата получава паролата в `CPANEL_PASS`; ако я отпечата, агентът вижда `••••`.

Правила за агентите: [docs/AGENT_GUIDE.md](docs/AGENT_GUIDE.md). Ръчен тест на реален компютър: [docs/MANUAL_TEST.md](docs/MANUAL_TEST.md).

### Връзка с AI агенти (MCP)

`svault mcp` пуска MCP сървър (през stdio), така че всяка сесия на Claude на този компютър вижда трезора като инструменти:

| Инструмент | Какво прави |
|---|---|
| `vault_list` | Референции и етикети, без стойности. |
| `vault_add` | Агентът иска нова тайна: на екрана излиза прозорец, **вие** въвеждате стойността, агентът получава само `vault://...`. |
| `vault_run` | Като `svault run`: тайните в средата на командата, изходът маскиран; съгласие + Windows Hello само ако трезорът е заключен. |
| `vault_render` | Като `svault render`: попълва шаблон във файл, връща само потвърждение. |
| `vault_check` | Слаби и повторени пароли — само имена. |
| `vault_rotate` | Смяна на парола на сайт: новата е в клипборда, вие я поставяте и потвърждавате. |
| `vault_log` | Последните записи от одитния дневник. |

Няма инструмент, който връща стойност. „Разреши“ и PIN се искат веднъж след влизане в Windows; после всички сесии на агента ползват отключения трезор до излизане, рестарт, изключване или `svault lock`. `svault status` казва дали е отключен.

Свързване (веднъж, за всички сесии на потребителя):

```powershell
# Claude Code
claude mcp add shinkansen-vault --scope user -- "$env:LOCALAPPDATA\Programs\ShinkansenVault\svault.exe" mcp
```

Claude Desktop — в `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{ "mcpServers": { "shinkansen-vault": { "command": "C:\\Users\\<вие>\\AppData\\Local\\Programs\\ShinkansenVault\\svault.exe", "args": ["mcp"] } } }
```

Новите MCP връзки се виждат само в **нови** сесии.

### Лиценз

GPL-3.0 (вижте [LICENSE](LICENSE)).

---

## English

Shinkansen Vault (`svault.exe`) is a small Windows password vault unlocked with **Windows Hello** (PIN, fingerprint or face). It is built so an AI agent (e.g. Claude Code) can **use** secrets without **seeing** them.

- The agent only knows references such as `vault://varnahub/cpanel`.
- The first use after you sign in to Windows goes through a consent dialog plus Windows Hello. The vault then stays **unlocked until you sign out, restart or shut down** (`svault lock` locks it at once): no prompt per command or per new agent session. `export-google` always asks. Every use is logged with its reason.
- The vault itself puts the value where it is needed: a child process environment, a file, or the clipboard for 20 seconds.
- **There is no command that prints a secret.**
- No server, no cloud, no telemetry, no network calls. Everything stays on your PC.

### Commands

| Command | What it does |
|---|---|
| `svault init` | Creates the vault and registers a Windows Hello key. |
| `svault add vault://project/name [--label "text"] [--replace]` | Opens a dialog where the **human** types the value. Never as an argument or via stdin. |
| `svault list` | References and labels only, never values. |
| `svault run --env NAME=vault://ref [...] --reason "why" -- command ...` | Runs the command (if the vault is locked: after consent + Hello) with the secrets in its environment. Any secret in its output is replaced with `••••` (or `****` if the console cannot show that character). |
| `svault render template output --reason "why"` | Replaces `{{vault://ref}}` placeholders and writes the result straight to the output file. Prints only a confirmation. |
| `svault clip vault://ref --reason "why"` | Copies to the clipboard for 20 s (for the human to paste), then clears it. |
| `svault mcp` | MCP server for AI agents (see below). |
| `svault import-google [file.csv]` | Imports a Google Password Manager export: new logins become `vault://google/<site>`, changed passwords are updated (old value kept as previous), missing ones are left alone. Offers to wipe the file afterwards. |
| `svault export-google [file.csv]` | Writes a Google-format CSV for import at passwords.google.com and wipes it when you confirm. |
| `svault check` | Local check for weak and reused passwords. Names only. |
| `svault rotate vault://ref --reason "why"` | Strong new password in the clipboard → you paste it on the website → confirm → stored. Old value kept as previous. |
| `svault log [--tail N]` | Audit log (JSONL): time, refs, command/target, reason, approved/denied. Never values. |
| `svault status` | Tells whether the vault is unlocked for this Windows sign-in. |
| `svault lock` | Locks at once; the next use asks for consent + Windows Hello again. |

Files: `%LOCALAPPDATA%\ShinkansenVault\vault.svlt` and `audit.jsonl` (override the folder with `SVAULT_HOME`).

### Sync with Google Password Manager

Google offers no API for saved passwords, so sync goes through its CSV file:

1. **Google → vault:** passwords.google.com → Settings → *Export passwords* → `svault import-google` → pick the file → consent + PIN → "Wipe file".
2. **Fix:** `svault check` lists weak and reused passwords; for each, `svault rotate vault://google/<site>`. When you change a password in Chrome it offers to update Google too, so both copies stay the same.
3. **Vault → Google** (e.g. passwords created by the vault): `svault export-google` → Settings → *Import passwords* → "Wipe file".

The CSV holds passwords in plain text. Keep it in a local folder that is **not synced** (OneDrive, Nextcloud, Syncthing) and wipe it right away.

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
3. If the vault is locked (first use after signing in to Windows), a dialog shows the reason, the secrets and the command; the human clicks "Allow" and enters the PIN. After that, no prompts until sign-out, restart or shutdown.
4. The command gets the password in `CPANEL_PASS`; if it prints it, the agent sees `••••`.

Agent rules: [docs/AGENT_GUIDE.md](docs/AGENT_GUIDE.md). Manual test on a real PC: [docs/MANUAL_TEST.md](docs/MANUAL_TEST.md).

### Connecting AI agents (MCP)

`svault mcp` runs an MCP server over stdio, so every Claude session on this PC sees the vault as tools:

| Tool | What it does |
|---|---|
| `vault_list` | References and labels, no values. |
| `vault_add` | The agent asks for a new secret: a dialog opens on screen, **you** type the value, the agent gets only `vault://...`. |
| `vault_run` | Like `svault run`: secrets in the command's environment, output masked; consent + Windows Hello only if the vault is locked. |
| `vault_render` | Like `svault render`: fills a template into a file, returns only a confirmation. |
| `vault_check` | Weak and reused passwords — names only. |
| `vault_rotate` | Change a password on a website: the new one is in the clipboard, you paste and confirm. |
| `vault_log` | Latest audit log entries. |

No tool returns a value. "Allow" and the PIN are asked once after you sign in to Windows; then every agent session uses the unlocked vault until sign-out, restart, shutdown or `svault lock`. `svault status` tells whether it is unlocked.

Connect once for all of the user's sessions:

```powershell
# Claude Code
claude mcp add shinkansen-vault --scope user -- "$env:LOCALAPPDATA\Programs\ShinkansenVault\svault.exe" mcp
```

Claude Desktop — in `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{ "mcpServers": { "shinkansen-vault": { "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\ShinkansenVault\\svault.exe", "args": ["mcp"] } } }
```

New MCP servers only show up in **new** sessions.

### License

GPL-3.0 (see [LICENSE](LICENSE)).

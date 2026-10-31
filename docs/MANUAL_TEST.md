# Ръчен тест на реален Windows компютър / Manual test on a real Windows PC

Windows Hello, DPAPI, диалозите и клипбордът не могат да се тестват в облака. Този списък се изпълнява веднъж на реален компютър. Използва **отделна тестова папка**, така че не пипа истински трезор. Използвайте само фалшиви стойности като `FAKE-test-123`.

*Windows Hello, DPAPI, the dialogs and the clipboard cannot be tested in the cloud. Run this checklist once on a real PC. It uses a separate test folder and only fake values.*

## 0. Подготовка / Setup

- [ ] Windows 10 2004+ или Windows 11, с настроен **Windows Hello PIN** (Settings → Accounts → Sign-in options).
- [ ] Инсталиран .NET 8 SDK (`dotnet --list-sdks` показва 8.0.x).
- [ ] В PowerShell:
  ```powershell
  git clone https://github.com/galinpd-lgtm/shinkansen-vault.git
  cd shinkansen-vault
  dotnet test
  dotnet publish src/svault/svault.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
  $sv = (Resolve-Path .\publish\svault.exe).Path
  $env:SVAULT_HOME = "$env:TEMP\svault-test"
  ```
  Очаквано: тестовете минават, `publish\svault.exe` съществува.

## 1. init

- [ ] `& $sv init`
- Очаквано: Windows Hello пита **до 3 пъти** (създаване на ключ + 2 подписа за проверка на детерминизма). Накрая: `Vault created: ...\svault-test\vault.svlt`.
- [ ] Hello прозорецът появи ли се **отпред**? (Ако е зад конзолата — запишете го.)
- [ ] `& $sv init` отново → грешка „A vault already exists“.

## 2. add

- [ ] `& $sv add vault://test/demo --label "Test demo"`
- Очаквано: прозорец „нова тайна“. Въведете `FAKE-test-123` → „Запази“ → Windows Hello → `Stored vault://test/demo.`
- [ ] `& $sv add vault://test/demo` → грешка „already exists. Use --replace“.
- [ ] `& $sv add vault://test/x --value abc` → отказ („never accepted as arguments“).
- [ ] `& $sv add vault://test/cancel` → натиснете „Откажи“ → изход код 5 (`$LASTEXITCODE`).

## 3. list и файлът на трезора

- [ ] `& $sv list` → `vault://test/demo<TAB>Test demo`, без стойност.
- [ ] `Select-String -Path "$env:SVAULT_HOME\*" -Pattern "FAKE-test-123"` → **нищо** не се намира.

## 4. run

- [ ] ```powershell
  & $sv run --env DEMO=vault://test/demo --reason "Manual test run" -- cmd /c "echo value=%DEMO%"
  ```
  Очаквано: изход `value=••••` (или `value=****`, ако конзолата не е UTF-8). Без диалог и без PIN: `add` в т. 3 вече отключи трезора за това влизане в Windows. Ако все пак се появи диалог (напр. след рестарт) — „Разреши“ → PIN.
- [ ] Доказателство, че стойността е подадена (без да се вижда):
  ```powershell
  & $sv run --env DEMO=vault://test/demo --reason "Check length" -- powershell -NoProfile -Command "`$env:DEMO.Length"
  ```
  Очаквано: `13`.
- [ ] `& $sv lock` → `Locked.`; после повторете първата команда и натиснете **„Откажи“** → `svault: Denied by the user.`, `$LASTEXITCODE` = 5, Hello **не** се появява.
- [ ] Повторете и натиснете „Разреши“, но **откажете** в Windows Hello → `Windows Hello was cancelled.`, код 5. `& $sv status` → `locked`.
- [ ] Esc в диалога = отказ? Enter не разрешава случайно?

## 4а. Отключен за влизането / Unlocked for the sign-in

- [ ] Пуснете първата команда от т. 4 → „Разреши“ → PIN. После я пуснете още 3 пъти → **нито диалог, нито PIN**. `& $sv status` → `unlocked for this sign-in`.
- [ ] Отворете **нов** PowerShell (или нова сесия на агента с `svault mcp`) и пуснете командата → пак без питане.
- [ ] `& $sv export-google "$env:TEMP\svault-out\x.csv"` (ако има записи от Google) → диалогът се появява **винаги**, без PIN.
- [ ] Излезте от Windows (Sign out) и влезте пак → първата команда **пита** отново. Същото след рестарт и след изключване.
- [ ] `& $sv lock` → следващата команда пита отново. В `& $sv log` има ред `"lock"`.

## 5. render

- [ ] ```powershell
  "user=demo`npass={{vault://test/demo}}" | Set-Content "$env:TEMP\t.template"
  & $sv render "$env:TEMP\t.template" "$env:TEMP\svault-out\t.conf" --reason "Manual render"
  ```
  Очаквано: само `Rendered 1 reference(s) into ... Content not shown.` (диалог → PIN само ако трезорът е заключен).
- [ ] Отворете `%TEMP%\svault-out\t.conf` в Notepad (**като човек**) → `pass=FAKE-test-123`.
- [ ] `& $sv render "$env:TEMP\t.template" CON --reason x` → отказ („not a device“).

## 6. clip

- [ ] `& $sv clip vault://test/demo --reason "Paste test"` → „Copied ... cleared in 20 s“ (диалог → PIN само ако е заключен).
- [ ] Поставете (Ctrl+V) в Notepad до 20 s → `FAKE-test-123`.
- [ ] След 20 s поставете отново → нищо.
- [ ] Win+V (история на клипборда, ако е включена) → стойността **не** е там.

## 7. log

- [ ] `& $sv log` → редове JSON с `approved`, `denied`, `failed`, причини и цели. `Select-String -Path "$env:SVAULT_HOME\audit.jsonl" -Pattern "FAKE-test-123"` → нищо.

## 8. (По желание) друг Windows потребител / Optional: other Windows user

- [ ] Копирайте `vault.svlt` при друг потребител и опитайте `clip` → трябва да откаже („Could not unlock“ или Hello грешка).

## 9. Почистване / Cleanup

```powershell
& $sv lock
Remove-Item -Recurse -Force "$env:TEMP\svault-test", "$env:TEMP\svault-out", "$env:TEMP\t.template"
Remove-Item Env:\SVAULT_HOME
```
Тестовият Hello ключ (`ShinkansenVault-<id>`) остава в Windows Hello; безвреден е.

## Какво да докладвате / What to report

За всяка точка: ✅ / ❌ + текста на грешката. Особено важно:
1. Дали `init` успя (т.е. подписите на Hello са детерминистични на вашия TPM).
2. Дали Hello прозорецът се появява отпред или зад конзолата.
3. Колко пъти Hello пита при `init`, при първото използване след влизане и дали след това наистина не пита (т. 4а).

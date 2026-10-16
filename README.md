# PrintStation

Phones on the office Wi-Fi open `http://<pc>:8080`, upload PDF/Word/Excel/PowerPoint/photos, and the PC prints them on its USB printer.

- `src/Program.cs` – HTTP server, upload parsing, conversion (Word/Excel/PowerPoint via COM, LibreOffice fallback) and printing (bundled SumatraPDF).
- `web/index.html` – the phone page.
- `config.json` – per-PC settings (`printer`, `port`, `pin`, `peers`, `dryRun`).
- `build.bat` – compiles with the .NET Framework compiler that ships with Windows (no SDK needed) into `dist/`.
- `install.bat` / `uninstall.bat` – run as administrator on each office PC (firewall, URL permission, start at login).

Copy the `dist` folder to each PC (e.g. `C:\PrintStation`), edit `config.json`, run `install.bat` as administrator.

# Verification - 2026-10-05

Environment: Windows x64, .NET SDK 10.0.401, target .NET 8 WinForms.
Published runtime: Microsoft.NETCore.App / Microsoft.WindowsDesktop.App 8.0.31.
Integration engine: MariaDB 10.4.32 from XAMPP, copied into disposable fixtures.
Final publish: zero build warnings/errors, x64 PE machine 0x8664, 71,641,549 bytes.
Final EXE launch check: input-idle, running and responding in a hidden fixture-only session.
SHA-256: `3D9C23A7BF182BF9F79BA6AB1A5F208423259B32C3DB3D40CC782AD5D1E98754`.

Commands:

```bat
dotnet run --project XamppRecoveryTool.Checks -c Release -- --integration C:\xampp
publish.bat
```

Integration: **35 checks passed**, including:

- Native IPv4/IPv6 listener ownership and refusal to stop unrelated listeners.
- Explicit server configuration ownership, external storage and unsafe path rejection.
- Read-only diagnostics; complete physical copy including empty directories; SHA-256 validation.
- Refusal of empty/tampered backups and rebuild without verified SQL.
- Exclusive recovery lock; rollback with failed candidate retained; recovery from interrupted directory rename.
- Real SQL dump with a global read lock maintained until shutdown.
- InnoDB row preservation through recovery and restore; backup before restore.
- Level 3 rebuild after deliberately corrupting only a disposable candidate's ibdata1.
- Preservation of password-protected root authentication and Vietnamese UTF-8 data through SQL rebuild.
- Targeted offline repair of a corrupt proxies_priv index, including an older .frm creator-version header.
- Preservation of every proxy privilege row and of authenticated access after that repair.
- A corrupt mysql.db data-page checksum remains untouched; startup succeeds with an explicit warning.
- Full SQL dump failure falls back to a verified user-database dump; this dump cannot authorize InnoDB rebuild.

Basic safety suite: 22 checks, also executed by `build.bat` / `publish.bat`.
WinForms layout rendered to `ui-preview.png` in the test artifact directory and visually inspected.

The test suite reads only binaries, libraries and share files from the source installation,
creates its own database, and uses separate ports.

Limits: no claim of recovery from every corruption pattern. UAC prompts, production service
permissions, disk hardware faults and abrupt operating-system power loss were not end-to-end tested.
Crash recovery was exercised by leaving a durable journal at the directory-rename boundary.

## Windows installer verification - 2026-10-06

- `setup.bat`: Release build and publish succeeded with zero warnings/errors; 22 safety checks passed.
- Inno Setup 6.2.2 compiled the per-user x64 installer for version 1.0.0.
- Silent install into an isolated test directory exited 0 without elevation.
- Installed EXE SHA-256 matched the portable EXE; Start menu shortcut targeted that EXE.
- Windows uninstall registration used HKEY_CURRENT_USER and the requested install directory.
- Installed GUI reached input-idle, remained responsive, and closed gracefully via WM_CLOSE in a hidden fixture-only session.
- Uninstall exited 0, removed the EXE, Start menu shortcut and registration, and retained generated logs and a user-file sentinel.
- Installer is unsigned. Interactive wizard, optional desktop shortcut and upgrades from a previous version were not tested.
- Release includes setup, portable EXE and SHA256SUMS.txt. Database copies, SQL, incident logs and private notes are excluded from Git.

# DriveMapper (Intune / Registry-Driven Edition)

DriveMapper is a lightweight Windows utility for **policy-driven network drive mapping** on Entra ID–joined or hybrid devices, designed for **Microsoft Intune deployments**.

This fork replaces JSON / AD group–based configuration with **registry values delivered via Intune ADMX**, enabling modern, cloud-native drive mapping without logon scripts or Group Policy Preferences.

---

## Architecture Overview

- **DriveMapper.exe**
  - Runs in the context of the **logged-in user**
  - Reads drive mapping configuration from registry policy keys
  - Maps, remaps, or removes drives as required
  - Logs all actions and errors to the Windows Application Event Log

- **Install.exe**
  - Copies files to a target directory
  - Creates scheduled tasks (logon + network change)
  - Creates the Event Log source used by DriveMapper
  - Fully uninstallable
  - Designed for Intune Win32 deployment

---

## Configuration Model (Intune / ADMX)

Drive mappings are controlled via **ADMX-backed registry policy** applied to **users** (not machines).

### Registry Locations

For each drive letter (`A`–`Z`), DriveMapper reads:

#### User policy
```
HKCU\Software\Policies\DriveMapper\Drives\<LETTER>\
```


### Supported Values

| Value Name | Type | Description |
|-----------|------|-------------|
| `Enabled` | DWORD (0/1) | Enables or disables the mapping |
| `Path` | REG_SZ | UNC path (e.g. `\\server\\share`) |
| `Name` | REG_SZ | Optional display name (best-effort) |
| `Reconnect` | DWORD (0/1) | Persistent mapping |

---

## Runtime Behavior

### Per-User Execution

DriveMapper runs via **Scheduled Tasks configured with `InteractiveToken`**, meaning:

- The task is created once, system-wide
- At runtime, Windows executes it as the **currently logged-in user**
- Each user receives **their own drive mappings**
- Users do not affect each other, even on shared machines

---

### Idempotent Mapping Logic

For each drive letter:

- If the drive is already mapped to the correct path → **skip**
- If mapped to a different path → **remap**
- If `Enabled=0` → **remove mapping**
- If policy no longer exists → **remove mapping (if previously applied)**

DriveMapper only removes drives it previously created, tracked per user under:

```
HKCU\Software\DriveMapper\State\AppliedDrives
```

This prevents accidental removal of user-managed drives.

---

## Logging

All actions and errors are written to the **Windows Application Event Log**:

- **Log:** Application
- **Source:** `DriveMapper`

Logged events include:
- Drive mapped
- Drive removed
- Mapping skipped (already correct)
- Configuration errors
- Win32 API failures

The Event Log source is created by the installer during install (admin required).

---

## Installer (`Install.exe`)

`Install.exe` is a standalone .NET 8 installer utility designed for Intune Win32 deployment.

### Features

- Copies files to a target directory (e.g. `Program Files`)
- Registers scheduled tasks:
  - On user logon
  - On network profile change (VPN / Wi-Fi / Ethernet)
- Runs DriveMapper **as the logged-in user**
- Creates the `DriveMapper` Event Log source
- Writes version info to HKLM for Intune detection
- Fully uninstallable

---

## Installer Configuration (`install.config.json`)

Example:

```json
{
  "TargetDirectory": "C:\\Program Files\\DriveMapper",
  "ExeName": "DriveMapper.exe",
  "ShortcutName": "",
  "Version": "1",
  "ScheduledTasks": [
    {
      "TaskName": "DriveMapper",
      "Arguments": "",
      "CreateLogonTask": true,
      "CreateNetworkTask": true,
      "CreateBootTask": false
    }
  ]
}
```

### Field Descriptions

| Field | Description |
|------|-------------|
| `TargetDirectory` | Destination directory for binaries |
| `ExeName` | Executable launched by scheduled tasks |
| `ShortcutName` | Optional Start Menu shortcut |
| `Version` | Written to HKLM for Intune detection |
| `ScheduledTasks` | One or more task definitions |

---

## Installation & Uninstallation

Run from an **elevated prompt**:

### Install
```powershell
Install.exe install.config.json install
```

### Uninstall
```powershell
Install.exe install.config.json uninstall
```

---

## Intune Deployment (Win32 App)

1. Publish binaries as **self-contained single-file executables**
2. Package:
   - `Install.exe`
   - `DriveMapper.exe`
   - `install.config.json`
3. Create `.intunewin` package
4. Configure:
   - **Install command:**  
     `Install.exe install.config.json install`
   - **Uninstall command:**  
     `Install.exe install.config.json uninstall`
   - **Detection rule:**  
     HKLM `SOFTWARE\<ExeNameWithoutExtension>\Version`

Assignments should typically target **users**, not devices.

---

## Troubleshooting

- Check registry policy:
  ```powershell
  Get-ChildItem HKCU:\Software\Policies\DriveMapper\Drives
  ```
- Check Event Viewer:
  - Windows Logs → Application
  - Source: **DriveMapper**
- If drives don’t map immediately, wait for:
  - Network change
  - Next logon

---

## License

GPL.  
Original project by `icds250`.  
This fork extends functionality for Intune / Entra ID environments.

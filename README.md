# ST2 — Herramientas SQL

App de escritorio Windows para **backup, restore, consultas y traza** de bases Bejerman.

> Nombre técnico del proyecto en código: `SBBackup`. El producto visible es **ST2 — Herramientas SQL**.

Las herramientas ST2 no representan productos ni posiciones oficiales de Thomson Reuters.

## Qué incluye

| Módulo | Descripción |
|--------|-------------|
| **Conexión** | Detecta el servidor por registro Bejerman; login SQL local o autenticación Windows |
| **Backup** | Backup de bases por empresa, shrink de log y empaquetado |
| **Restore** | Restore de `.bak` (local o remoto) con preview de archivos |
| **Programado** | Perfiles de backup con Tarea Programada de Windows |
| **Consultas** | Editor T-SQL, catálogo de scripts frecuentes y resultados en grilla |
| **Profiler** | Traza tipo profiler con Extended Events |
| **IA** | Explica consultas y errores leyendo el esquema de la base conectada (Groq) |

## Requisitos

- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) para compilar
- SQL Server accesible (local o remoto)

Para el `.exe` autocontenido no hace falta .NET en la PC destino.

## Configuración local

```powershell
copy SBBackup\appsettings.local.json.example SBBackup\appsettings.local.json
```

Editá `appsettings.local.json` con la clave SQL y, si usás IA, la API key. Ese archivo está en `.gitignore` y no debe subirse.

También se puede dejar el mismo archivo junto al `.exe` o en `%LocalAppData%\ST2\appsettings.local.json`.

## Ejecutar

```powershell
dotnet run --project SBBackup\SBBackup.csproj
```

## Publicar el .exe

```powershell
.\publicar.ps1
```

Queda un solo archivo en `publish\ST2 - Herramientas SQL.exe`.

## Estructura

```
ST2-Herramientas-SQL/
├── SBBackup.sln
├── publicar.ps1
└── SBBackup/
    ├── Program.cs
    ├── HomeForm.cs            ← conexión y menú
    ├── Form1.cs               ← backup
    ├── RestoreForm.cs
    ├── QueryForm.cs
    ├── TraceForm.cs
    ├── Models/
    ├── Services/              ← SQL, backup, restore, IA, scheduler
    └── Ui/
```

## Suite ST2

- [ST2 WEB](https://github.com/SoyTolei/ST2-WEB) — portal del agente
- [ST2 BAT](https://github.com/SoyTolei/ST2-BAT) — consola de campo PowerShell
- [ST2 Chile](https://github.com/SoyTolei/ST2-Chile) — backups LpContab / LpRemu

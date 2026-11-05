# ioBroker-NewGen – Projektdokumentation

Diese Dokumentation beschreibt den aktuellen Entwicklungsstand des Projekts
**ioBroker-CSharp-Fork (ioBroker-NewGen)** mit RSEV.Utilities **3.0.0.0**.

- **Version:** beta0.2.0 / Entwicklungszweig
- **Zielplattform:** .NET 9
- **RSEV.Utilities:** 3.0.0.0
- **Repository:** https://github.com/MrRSEV/ioBroker-NewGen

## Projektvision

Eine moderne, stabile, objektorientierte Smarthome-Matrix mit isolierten
Legacy-Runtimes und einer einheitlichen Adapter-API.

## Aktueller Umsetzungsstand

- Sprint 1: Core Fundament – umgesetzt
- Sprint 2: TCP-Bridge & Legacy Runtime – weitgehend umgesetzt, externe Abnahme offen
- Sprint 3: CustomAdapter API, externe DLLs und optionale Dependencies – umgesetzt
- Sprint 4: StateEngine & EventBus – umgesetzt
- Sprint 5: AdminUI als externe AdminUI.dll – weitgehend umgesetzt
- Sprint 6: Installation & Deployment – lokale/HTTP-Pakete umgesetzt
- Sprint 7: Kritische Adapter – native DLLs, Hardwaretests offen
- Sprint 8: Stabilisierung & Performance – Smoke-/Basislasttests vorhanden
- Sprint 9: Migration Phase 2 – native DLLs, Geräte-Fixtures offen
- Sprint 10: Migration Phase 3 – native DLLs, Cloud-/CCU-Fixtures offen
- Sprint 11: Finalisierung – Release-Candidate-Publish verifiziert, produktive Abnahme offen
- beta0.3: Core.Security v1 (SQLite, Benutzer, Rollen, Sitzungen) – umgesetzt, AdminUI-Anbindung offen

## Dokumentation

- [Architekturübersicht](architektur.md)
- [Core/Matrix – Semantische Zustandsmatrix](core-matrix.md)
- [Core/Bus – Systembus](core-bus.md)
- [Core/Routing – AdapterRouter](core-routing.md)
- [Core/Bridge – TCP-Bridge & Node.js-Adapter-Runtime](core-bridge.md)
- [Core/Registry – AdapterRegistry](core-registry.md)
- [Core/Security – Benutzer, Rollen, Sitzungen](core-security.md)
- [Core/Localization – LocalizationService, Sprachweiche](localization.md)
- [Konfiguration (config.toml)](konfiguration.md)
- [Sprintplan & Roadmap](sprintplan.md)
- [Developer Guide](developer-guide.md)
- [Adapter SDK](adapter-sdk.md)
- [Adaptermigration](migration.md)

# Fileman

Versionssicherung für lokal verwaltete Dateien auf eine USB-SSD. Fileman ist für Anwender ohne technischen Hintergrund gedacht.

- Sichert **mehrere Quellordner**: auf Wunsch als Delta seit der letzten Sicherung, später auch automatisch.
- **Löschungen gefährden die Sicherung nicht.** Gesicherte Inhalte werden nie überschrieben oder entfernt.
- **Dokumente werden wiedererkannt**, auch nach Umbenennen oder Verschieben. Die Suche findet sie über jeden früheren Pfad.
- Gleiche Inhalte werden nur **einmal gespeichert** (Deduplizierung).

## Aufbau

| Projekt | Inhalt |
|---|---|
| `src/Fileman.Core` | Gesamte Logik als Bibliothek, ohne Oberfläche. Einzige Abhängigkeit: `Microsoft.Data.Sqlite`. |
| `src/Fileman.Cli` | Kommandozeile `fileman`, eine dünne Schicht über dem Kern. Gedacht für Support, Skripte und Aufgabenplanung. |
| `tests/Fileman.Tests` | xUnit-Tests der Kernlogik. Laufen unter Windows und Linux. |
| *(folgt)* `src/Fileman.App` | WinForms-Tray-Anwendung über demselben Kern. |

Wichtigster Einstiegspunkt für Oberflächen ist `FilemanWorkspace`. Er übernimmt:
- Einstellungen laden
- die Sicherungsplatte finden
- die Sicherung ausführen
- den Katalog öffnen

### Archiv auf der Sicherungsplatte

```
E:\FilemanArchiv\
  fileman-target.json     Kennung der Platte. Damit wird sie auch unter anderem Laufwerksbuchstaben gefunden.
  catalog.db              SQLite-Katalog: Dokumente, Versionen, Pfadverlauf, Ereignisse.
  blobs\ab\cd\<sha256>.gz Dateiinhalte. Jeder Inhalt genau einmal, gzip-komprimiert, schreibgeschützt.
  fileman.lock            Verhindert, dass zwei Programme gleichzeitig schreiben.
```

Lokal unter `%LOCALAPPDATA%\Fileman` (überschreibbar mit `FILEMAN_HOME` oder `--home`) liegen:
- `settings.json`
- `catalog-cache.db`: eine Kopie des Katalogs. Damit funktioniert die Suche auch ohne angesteckte Platte.

### Wiedererkennung von Dokumenten

Für jede neue oder geänderte Datei prüft der Kern nacheinander:

1. **Gleicher Pfad:** Es ist dasselbe Dokument. Bei geändertem Inhalt entsteht eine neue Version.
2. **Gleiche Datei-ID:** NTFS-File-ID, unter Linux die Inode. Erkennt Umbenennen und Verschieben auf demselben Laufwerk, auch wenn die Datei dabei geändert wurde.
3. **Gleicher Inhalt (SHA-256):** Erkennt Verschieben über Laufwerksgrenzen sowie „Kopieren und Original löschen“.
4. **Gleicher Dateiname:** greift nur, wenn genau eine Datei dieses Namens fehlt.

Als Kandidaten gelten Dokumente, die an ihrem bisherigen Ort fehlen. Dazu kommen Dokumente, die in den letzten 15 Minuten als gelöscht erkannt wurden. Das ist wichtig für den Automatik-Modus, wo Löschung und Neuanlage getrennt ankommen.

### Parallelität

- **Durchsuchen:** Mehrere Threads arbeiten eine gemeinsame Ordner-Warteschlange ab (`ScanThreads`, Standard: Anzahl Kerne, 2–16).
- **Sichern:** Datei-ID ermitteln, lesen, hashen, komprimieren und ablegen läuft parallel (`FileThreads`, Standard: halbe Kernzahl, 2–8).
  - Den Katalog schreibt genau ein Thread, denn SQLite erlaubt nur einen Schreiber.
  - Er übernimmt die Ergebnisse streng in Pfad-Reihenfolge. Die Zuordnung ist deshalb trotz Parallelität immer gleich.
- **Prüfen** (`verify`) läuft ebenfalls parallel.
- Beide Werte stehen in `settings.json`. `0` bedeutet automatisch.
- Messung mit 20.000 Dateien (2,1 GB) auf 4 Kernen:
  - Erstsicherung: 96 s mit 1 Thread, 26 s mit 8 Threads.
  - Delta-Lauf ohne Änderungen: unter 1 s.

### Sicherheitsregeln im Kern

- Ein **nicht erreichbarer Quellordner** wird übersprungen, z. B. ein getrenntes Netzlaufwerk. Seine Dateien werden **nicht** als gelöscht markiert.
- Für **nicht lesbare Unterordner** gilt dasselbe, z. B. bei fehlenden Rechten. Sie werden als Warnung gemeldet.
- Ein Inhalt wird erst in eine temporäre Datei geschrieben und dann umbenannt. Der Katalogeintrag folgt erst danach. Wird die Platte mitten in der Sicherung abgezogen, bleibt nichts halb Gesichertes zurück.
- Ändert sich eine Datei während des Lesens, versucht Fileman es erneut. Gelingt das nicht, folgt sie beim nächsten Lauf.
- Das Archiv selbst wird nie mitgesichert, auch wenn es in einem Quellordner liegt.
- Beim Wiederherstellen wird **nie überschrieben**. Ist der Name belegt, entsteht `Name (Stand 2026-10-12 14-32).ext`.
- Ausgeschlossen sind standardmäßig Office-Sperrdateien (`~$*`), `*.tmp`, `Thumbs.db` und `desktop.ini`. Dazu kommen Cloud-Platzhalter, die nur online verfügbar sind, und Verknüpfungspunkte (Junctions).

## Kommandozeile

```
fileman init-target E:\                          Sicherungsziel einrichten (legt E:\FilemanArchiv an)
fileman roots add D:\Mandanten --name Mandanten  Quellordner hinzufügen (beliebig viele)
fileman roots add C:\Users\me\Documents
fileman backup                                   Änderungen sichern
fileman status                                   Zustand anzeigen
fileman find bilanz huber                        Suche, auch in früheren Pfaden und Namen
fileman show 42                                  Pfadverlauf und Versionen von Dokument 42
fileman changes --count 30                       letzte Änderungen
fileman restore 117 --to C:\Temp                 Version 117 wiederherstellen
fileman verify                                   alle Inhalte auf Beschädigung prüfen
```

## Bauen und Testen

Voraussetzung: .NET 10 SDK (LTS, Support bis November 2028).

```
dotnet build
dotnet test
dotnet publish src/Fileman.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Nächste Schritte

- **Automatik-Modus:** FileSystemWatcher mit Ruhezeit und regelmäßigem Abgleich, dazu ein lokaler Zwischenspeicher, solange die SSD nicht angesteckt ist.
- **Tray-Anwendung (WinForms):**
  - Popup „Letzte Änderungen“
  - Archiv-Browser mit Ansehen, Wiederherstellen und Metadaten-Vergleich

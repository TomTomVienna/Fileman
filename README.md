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
| `src/Fileman.App` | WinForms-Tray-Anwendung (`Fileman.exe`) über demselben Kern. |
| `tests/Fileman.Tests` | xUnit-Tests der Kernlogik. Laufen unter Windows und Linux. |

Wichtigster Einstiegspunkt für Oberflächen ist `FilemanWorkspace`. Er übernimmt:
- Einstellungen laden
- die Sicherungsplatte finden
- die Sicherung ausführen
- den Katalog öffnen

### Archiv auf der Sicherungsplatte

Die Ablage ist **auch ohne Fileman lesbar**. Im Notfall findet man die Dateien mit dem Explorer.

```
E:\FilemanArchiv\
  LIESMICH.txt                         Erklärung der Struktur für den Notfall
  Dateien\D\Mandanten\Huber GmbH\      Ordnerstruktur der Quellen (erster Ordner = Laufwerk, "UNC" für Netzwerkpfade)
    Bilanz.xlsx                        zuletzt gesicherter Stand unter exaktem Namen
    Bilanz (2026-10-01 09-10).xlsx     jede Version mit ihrem Änderungszeitpunkt
    Bilanz (2026-10-12 14-32).xlsx
  Dateien\D\Mandanten\Alt\
    _Fileman – Ordner verschoben.txt   Hinweis im alten Ordner, wenn ein ganzer Ordner verschoben wurde
  catalog.db                           SQLite-Katalog: Dokumente, Versionen, Pfadverlauf, Ereignisse
  fileman-target.json                  Kennung der Platte, damit sie auch unter anderem Laufwerksbuchstaben gefunden wird
  fileman.lock, tmp\                   Schreibsperre und Zwischenablage während einer Sicherung
```

- **Gelöschte Dateien** bleiben mit allen Versionen liegen.
- **Verschobene oder umbenannte Dateien:**
  - Im alten Ordner bleiben die bisherigen Versionen und der letzte Stand.
  - Im neuen Ordner erscheinen alle Versionen unter dem neuen Namen.
- **Ordner-Verschiebungen** erhalten einen Hinweis. Ein Ordner gilt als verschoben, wenn der alte Ordner nicht mehr existiert. Den Hinweis bekommt nur der oberste verschobene Ordner.
- **Hardlinks** (NTFS) speichern gleiche Inhalte, also den aktuellen Stand, Kopien nach Verschiebungen und identische Dateien, nur einmal. Auf Dateisystemen ohne Hardlinks wird kopiert. Deshalb sollte die SSD mit **NTFS** formatiert sein.
- Alle Dateien im Archiv sind **schreibgeschützt**. Gleichnamige Dateien des Anwenders werden nie überschrieben. Ein abweichender Name bekommt Sekunden oder einen Zähler.
- Der Katalog ist für Fileman da (Suche, Wiedererkennung, Vergleich). Die Dateien selbst hängen nicht von ihm ab.
- **Lange Pfade:** Fileman kommt mit Pfaden über 260 Zeichen zurecht, der Windows-Explorer unter Umständen nicht. Kurze Ordnernamen am Ziel helfen, z. B. `E:\FilemanArchiv`.

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

### Automatik-Modus (`BackupService`)

- Ein **FileSystemWatcher** pro Quellordner sammelt nur die betroffenen Pfade.
- Nach der **Ruhezeit** (`DebounceSeconds`, Standard 10 s) wird gezielt in diesen Bereichen gesichert. Spätestens nach 2 Minuten wird auch dann gesichert, wenn sich laufend etwas ändert.
- Jeder Pfad wird rekursiv geprüft. Dadurch werden auch umbenannte oder verschobene Ordner samt Inhalt erkannt.
- **Vollständiger Abgleich** findet statt:
  - beim Start
  - nach einem Pufferüberlauf des Wächters
  - wenn ein Quellordner wieder erreichbar ist
  - nach dem Anstecken der Platte
  - regelmäßig, standardmäßig alle 60 Minuten
- **Ohne angesteckte Platte** werden geänderte Dateien lokal zwischengespeichert, standardmäßig bis 2 GB:
  - Speicherort ist `%LOCALAPPDATA%\Fileman\spool`.
  - Beim Anstecken folgt erst ein vollständiger Abgleich. Danach werden die Zwischenstände als zusätzliche Versionen übernommen.
- **„Jetzt sichern“** läuft auch dann, wenn die Automatik pausiert ist.
- Probleme landen im Protokoll `%LOCALAPPDATA%\Fileman\fileman.log`.

### Tray-Anwendung

- **Symbol im Infobereich**, die Farbe zeigt den Zustand:
  - grün: alles gesichert
  - blau: Änderungen erkannt oder Sicherung läuft
  - gelb: Platte nicht angesteckt
  - rot: Fehler
  - grau: pausiert
- **Linksklick** öffnet das Popup **„Letzte Änderungen“**. Ein Doppelklick auf einen Eintrag zeigt dessen Versionen.
- **Archiv durchsuchen:**
  - Suche nach Name oder Ordner, auch über frühere Pfade
  - Versionen und Pfadverlauf
  - **Ansehen**: schreibgeschützt mit dem Standardprogramm
  - **Wiederherstellen**: nie überschreibend
  - **Vergleichen**: Datei-Eigenschaften sowie Office- und PDF-Eigenschaften nebeneinander, Unterschiede markiert
- **Einstellungen:**
  - Quellordner
  - Sicherungsplatte (ein vorhandenes Archiv wird übernommen)
  - Start mit Windows
  - Ruhezeit
  - Ausnahmen
- Läuft nur einmal pro Benutzer. Beim ersten Start öffnen sich die Einstellungen.

### Sicherheitsregeln im Kern

- Ein **nicht erreichbarer Quellordner** wird übersprungen, z. B. ein getrenntes Netzlaufwerk. Seine Dateien werden **nicht** als gelöscht markiert.
- Für **nicht lesbare Unterordner** gilt dasselbe, z. B. bei fehlenden Rechten. Sie werden als Warnung gemeldet.
- Ein Inhalt wird erst in `tmp` geschrieben und dann an seinen Platz verschoben. Der Katalogeintrag folgt erst danach. Wird die Platte mitten in der Sicherung abgezogen, bleibt nichts halb Gesichertes zurück.
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
fileman watch                                    Automatik-Modus im Konsolenfenster (Test/Support)
```

## Bauen und Testen

Voraussetzung: .NET 10 SDK (LTS, Support bis November 2028).

```
dotnet build
dotnet test
dotnet publish src/Fileman.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/Fileman.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Die Tray-Anwendung lässt sich dank `EnableWindowsTargeting` auch unter Linux bauen. Ausführen lässt sie sich nur unter Windows.

## Nächste Schritte

- Erste Tests der Tray-Anwendung unter Windows (Layout, Benachrichtigungen, NTFS-Datei-ID).
- Schutz vor Massenänderungen: Automatik pausieren und warnen, wenn plötzlich sehr viele Dateien geändert werden (Verdacht auf einen Erpressungstrojaner).
- Optionale Verschlüsselung des Archivs.
- Installations- bzw. Verteilpaket.

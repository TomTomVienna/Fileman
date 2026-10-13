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
| `src/Fileman.Cli` | Kommandozeile `fileman-cli.exe`, eine dünne Schicht über dem Kern. Gedacht für Support, Skripte und Aufgabenplanung. |
| `src/Fileman.App` | WinForms-Tray-Anwendung (`Fileman.exe`) über demselben Kern. |
| `tests/Fileman.Tests` | xUnit-Tests der Kernlogik. Laufen unter Windows und Linux. |
| `installer/` | Inno-Setup-Skript, Build-Projekt für das Setup und Kurzanleitung für Anwender. |

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
  - violett: angehalten wegen Massenänderung
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
- **Startoptionen:** `Fileman.exe --archiv`, `--einstellungen` oder `--altbestand` öffnet das jeweilige Fenster, auch in einer bereits laufenden Instanz. Das Startmenü enthält „Fileman - Archiv durchsuchen“.

### Schutz vor Massenänderungen (`MassChangeGuard`)

Ein Verschlüsselungstrojaner kann ältere Versionen im Archiv nicht zerstören, denn Fileman überschreibt nie etwas. Er würde aber den „aktuellen Stand“ im Archiv durch unbrauchbare Dateien ersetzen und die Platte füllen. Deshalb gibt es zwei Prüfungen.

1. **Vor dem Schreiben:** Fileman zählt geänderte und verschwundene bekannte Dateien.
   - Umbenennungen und Verschiebungen werden über die Datei-ID erkannt und zählen nicht.
   - Im Automatik-Modus wird über ein Zeitfenster aufsummiert (`WindowMinutes`, Standard 10 Minuten). So fallen auch langsame Angriffe auf.
   - Grenze im Automatik-Modus: `MinFiles` (Standard 200).
   - Grenze bei vollständigen Abgleichen: zusätzlich `MinPercent` des Gesamtbestands (Standard 10 %). So schlagen z. B. normale Änderungen nach einer Woche ohne Platte nicht an.
2. **Beim Lesen:** Fileman prüft, ob der Anfang einer Datei noch zu ihrer Endung passt.
   - Geprüft werden Office (`PK` bzw. OLE), PDF, RTF, JPEG/PNG/GIF/TIFF, ZIP/7z/RAR.
   - Solche Stände werden zurückgehalten und erst am Ende des Laufs abgelegt.
   - Anschlag ab `SuspiciousContentMinFiles` (Standard 20), wenn mindestens die Hälfte der geprüften Dateien betroffen ist. Die zurückgehaltenen Stände werden dann verworfen.

**Bei einem Alarm:**
- Die Automatik hält an. Es wird auch nichts mehr zwischengespeichert.
- Das Tray-Symbol wird **violett**, und ein Warnfenster mit betroffenen Ordnern und Beispielen erscheint.
- Der Alarm übersteht Neustarts (Datei `massenaenderung.json`).
- Mit **„Das war ich – Änderungen sichern“** wird einmal ohne Prüfung vollständig gesichert, danach greift der Schutz wieder.
- In der CLI entspricht das `fileman-cli backup --force` (Rückgabewert 5 = angehalten).
- Alle Werte stehen unter `MassChange` in `settings.json`.

### Altbestand übernehmen (`Legacy*`)

Für alte Sicherungen, bei denen ein Teilbaum immer wieder in Datumsordner kopiert wurde, etwa `E:\Sicherungen\Sicherung 2024-06-30\Mandanten\…`.

**Rollen:**
- **Master** sind die gesicherten Quellordner.
- **Altbestände** sind abgeschlossene Momentaufnahmen. Sie sind älter als der Master und ändern sich nicht mehr. Deshalb werden sie nicht überwacht, sondern einmal eingelesen.

**Ablauf:**
1. **Eintragen:** in den Einstellungen im Reiter „Altbestand“.
   - Am einfachsten über **„Oberordner durchsuchen …“**: Man wählt nur den Ordner, in dem die alten Sicherungen liegen, z. B. `E:\Sicherungen` oder `E:\`. `LegacyDetector` erkennt die Momentaufnahmen selbst und liest dafür nur Ordnernamen.
   - Erkennungsmerkmale sind ein Datum im Namen und die gleiche Ordnerstruktur wie andere Kandidaten. So werden auch undatierte Kopien wie „Kopie Mandanten“ erkannt.
   - Eine Zwischenebene ohne Datum, die mehrere Datumsordner enthält, wird automatisch durchsucht.
   - Die Übereinstimmung mit den gesicherten Ordnern wird als Hinweis angezeigt. Fremde Ordner wie „Privat“ oder „Vorlagen“ erscheinen ohne Häkchen.
   - Eingetragen wird erst nach Bestätigung. Einzelne Ordner lassen sich weiterhin von Hand hinzufügen (Mehrfachauswahl).
2. **Einlesen** (`LegacyImporter`): Alles wird unverändert unter `Dateien\_Altbestand\<Laufwerk>\…\<Datumsordner>\…` abgelegt.
   - Ein Stand ist definiert durch relativen Pfad, Größe und Änderungsdatum.
   - Ein Stand, der schon aus einem anderen Datumsordner bekannt ist, wird nicht erneut gelesen, sondern verlinkt. Gleiche Inhalte liegen nur einmal auf der Platte.
   - Ein abgebrochenes Einlesen wird beim nächsten Mal fortgesetzt.
3. **Zuordnung vorschlagen** (`LegacyMapper`): Dafür ist keine Konfiguration nötig.
   - Für jede Altbestand-Datei werden Master-Dokumente gleichen Namens bewertet. Gleicher Inhalt (Hash eines bekannten Stands) zählt am meisten, danach die Zahl der Pfadebenen, die von hinten übereinstimmen.
   - Die Stimmen aller Altbestände werden gemeinsam ausgewertet. Was in einer Sicherung belegt ist, gilt auch für ältere.
   - Ergebnis sind eine Hauptzuordnung, z. B. `Mandanten` → `D:\Mandanten`, und Ausnahmen für umbenannte Ordner, z. B. `Mandanten\Dr. Steiner` → `D:\Mandanten\Dr. Steiner & Partner`.
   - Stände, die jünger sind als der Master, werden als unplausibel markiert.
4. **Übernehmen** (`LegacyConsolidator`, nach Bestätigung):
   - Jeder unterschiedliche Stand wird als Version (Art „Altbestand“) am Master-Pfad abgelegt, nur per Hardlink. Duplikate fallen weg.
   - Dateien, die es im Master nicht mehr gibt, werden als „gelöscht“ mit Geschichte geführt.
   - Nicht Zuordenbares bleibt unter `_Altbestand`.
   - Die Übernahme lässt sich wiederholen, ohne etwas doppelt anzulegen.

Während Einlesen und Übernehmen pausiert die automatische Sicherung.

**Aufruf:**
- **Tray-App:** Menü „Altbestand übernehmen …“ oder `Fileman.exe --altbestand`.
- **CLI:** `fileman-cli legacy scan <oberordner> [--add]|add|list|import|propose|consolidate|report`.
- **Übersichtsbericht vorab:** `fileman-cli legacy-analyze <ordner>` zeigt Kopien, geänderte Stände und gelöschte Dateien.

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
fileman-cli init-target E:\                          Sicherungsziel einrichten (legt E:\FilemanArchiv an)
fileman-cli roots add D:\Mandanten --name Mandanten  Quellordner hinzufügen (beliebig viele)
fileman-cli roots add C:\Users\me\Documents
fileman-cli backup [--force]                         Änderungen sichern (--force: auch bei Massenänderung)
fileman-cli status                                   Zustand anzeigen
fileman-cli find bilanz huber                        Suche, auch in früheren Pfaden und Namen
fileman-cli show 42                                  Pfadverlauf und Versionen von Dokument 42
fileman-cli changes --count 30                       letzte Änderungen
fileman-cli restore 117 --to C:\Temp                 Version 117 wiederherstellen
fileman-cli verify                                   alle Inhalte auf Beschädigung prüfen
fileman-cli watch                                    Automatik-Modus im Konsolenfenster (Test/Support)
```

## Bauen und Testen

Voraussetzung: .NET 10 SDK.

```
dotnet build
dotnet test
```

### Installationspaket

```
dotnet build installer/Fileman.Installer.proj -c Release
```

Ergebnis ist `artifacts/installer/Fileman-Setup-<Version>.exe` (ca. 38 MB). Die Version stammt aus `Directory.Build.props`.

**So entsteht das Setup:**
- Das Projekt veröffentlicht `Fileman.exe` (Tray) und `fileman-cli.exe` eigenständig für win-x64 in einen gemeinsamen Ordner. Die .NET-Laufzeit ist enthalten, auf dem Zielrechner muss nichts installiert sein.
- Danach kompiliert es `installer/Fileman.iss` mit **Inno Setup**. Der Compiler kommt automatisch aus dem NuGet-Paket `Tools.InnoSetup`.
- Unter Linux/macOS wird der Compiler über Wine ausgeführt. Dafür ist Wine mit 32-Bit-Unterstützung nötig (Paket `wine32`).

**Das Setup:**
- installiert **pro Benutzer** ohne Administratorrechte nach `%LOCALAPPDATA%\Programs\Fileman`
- legt Startmenü-Einträge an (Fileman und Kurzanleitung)
- trägt optional den Autostart ein (vorausgewählt) und legt optional ein Desktop-Symbol an
- startet Fileman auf Wunsch direkt nach der Installation

**Updates:** Einfach das neue Setup ausführen. Ein laufendes Fileman wird vorher mit `Fileman.exe --exit` beendet. Einstellungen und Archiv bleiben erhalten.

**Deinstallation:**
- Sie entfernt das Programm und den Autostart-Eintrag.
- Danach fragt sie, ob auch Einstellungen und Zwischenspeicher gelöscht werden sollen. Vorbelegt ist „Nein“.
- Das Archiv auf der Sicherungsplatte wird nie angetastet.

**Stille Installation**, z. B. für die IT-Betreuung:

```
Fileman-Setup-0.9.0.exe /VERYSILENT /SUPPRESSMSGBOXES /TASKS="autostart"
```

**Kein Code-Signing:** Das Setup ist nicht signiert, Windows SmartScreen warnt deshalb beim ersten Start („Weitere Informationen“ → „Trotzdem ausführen“). Für eine Verteilung an mehrere Kanzleien empfiehlt sich ein Code-Signing-Zertifikat.

### Automatischer Bau (GitHub Actions)

`.github/workflows/build.yml`:
- führt die Tests unter Linux **und** Windows aus
- baut bei jedem Push das Setup (als Artefakt herunterladbar)
- hängt es bei einem Tag `v<Version>` (z. B. `v0.9.0`) an ein GitHub-Release an

## Nächste Schritte

- Erste Tests der Tray-Anwendung unter Windows (Layout, Benachrichtigungen, NTFS-Datei-ID).
- Optionale Verschlüsselung des Archivs.

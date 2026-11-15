# FTX1-CATandRemote · Etapa 4 — Android și audio în fundal

Control CAT al Yaesu FTX-1 printr-un server Windows și un client desktop Avalonia.
Etapa 4 din [PLAN.md](PLAN.md): client Android cu control CAT, audio Opus și recepție cu ecranul stins.
Comportamentul CAT provine din `Eugen68/ftx1-pult`, commit `d453771` (2.10.2).
Meniul și fereastra „Setări stație” adăugate de proprietar în Etapa 1 sunt păstrate în client.

Etapele anterioare rămân în PR-urile #1, #2 și #3. Etapa 2 a fost testată și confirmată de proprietar;
această etapă continuă pe un branch separat, fără merge automat în `main`.

## Aplicația Android

1. Instalează Tailscale pe telefon și pe PC-ul stației, în aceeași rețea Tailscale.
2. Instalează **FTX1-Remote-Android.apk** pe telefon (Android 8.0 / API 26 sau mai nou, ARM64;
   pachetul conține și x86_64 pentru emulator). Este un APK de test semnat debug, autonom, fără .NET separat.
   Android poate cere activarea instalării din sursa prin care ai descărcat APK-ul.
3. Pornește serverul Windows din Etapa 3 sau mai nou, inițial în simulator. Generează un QR nou în server.
4. În aplicația Android, **Setări → Scanează QR**, permite camera și îndreapt-o spre ecranul PC-ului.
   Asocierea folosește amprenta certificatului și codul de unică folosință. Poți lipi și datele JSON de sub QR.
5. Apasă **Conectare** și permite microfonul și notificările; permisiunea Bluetooth este necesară pentru SCO.
   Folosește căști. Așteaptă afișarea indicatorului Opus, apoi ține apăsat PTT pentru emisie.
6. Ecranul principal are frecvență mare, glisare orizontală pentru tuning, pas selectabil în Hz, − / +,
   mod, bandă, S-metru / PO-SWR-ALC, AF, putere, MIC, NB/DNR, latență și nivelul tamponului audio.
   Tab-urile secundare conțin setări, consola CAT și profilul dispozitivului asociat.
7. **Recepția continuă cu ecranul stins** printr-un serviciu în prim-plan cu notificare și WakeLock parțial.
   Nu glisa notificarea/nu opri serviciul dacă dorești recepție continuă. Deconectarea este disponibilă din notificare.
   PTT se eliberează întotdeauna la ieșirea din ecranul aplicației sau la stingerea ecranului.
8. Apelurile / pierderea focusului audio, căștile deconectate, schimbarea/pierderea rețelei și oprirea serviciului
   eliberează PTT. Revenirea, recâștigarea focusului și reconectarea **nu pornesc TX**.
   După deconectarea căștilor folosește **Reia audio** când ai verificat ruta audio.
9. Tokenul este criptat AES-GCM cu o cheie neexportabilă Android Keystore. Nu este salvat în JSON necriptat;
   backup-ul aplicației este dezactivat. Lista și revocarea dispozitivelor rămân în administrarea serverului Windows.

Testul automat pe emulator include recepția în fundal. Politicile de economisire a bateriei ale producătorului,
SCO și apelurile reale trebuie verificate pe telefonul tău. APK-ul de debug nu este o versiune Play Store;
la schimbarea cheii de debug între build-uri poate fi necesară dezinstalarea și o nouă asociere.
PTT din notificare sau din butonul căștii nu este implementat (opțional în plan).

## Pachetul Windows

1. Extrage arhiva. Nu este necesară instalarea .NET separat.
2. Pe PC-ul stației, deschide **FTX1-Server.exe**, alege porturile și audio, apoi apasă **Pornește serverul**. La deschiderea manuală setările sunt editabile; pornirea automată cu Windows (`--tray`) pornește direct serverul. Inițial este selectat simulatorul.
3. Panoul serverului conține porturile CAT/PTT, simulatorul, limita emisiei, pornirea cu Windows,
   QR-ul de asociere și lista dispozitivelor revocabile. Pentru modificarea conexiunii oprește serverul,
   schimbă setările, apoi pornește-l din nou.
4. Pe același PC pornește **FTX1-CATandRemote.exe** și apasă **Conectare**. Serverul a scris un token
   de dispozitiv și amprenta certificatului în profilul utilizatorului; localhost nu ocolește autentificarea.
5. Pentru un alt PC instalează Tailscale pe ambele calculatoare. Generează un QR nou în server.
   În client, **Stație → Setări stație (F2)**: copiază datele JSON de sub QR în câmpul de asociere,
   alege un nume, apasă **Asociază dispozitivul**, apoi **Conectare**. Codul este valabil cinci minute
   și este consumat o singură dată. Scanarea cu camera este disponibilă în clientul Android din Etapa 4.
6. **Unelte → Consolă CAT (F12)** păstrează jurnalul și butonul **Transmite** / Enter.
   Acesta trimite o comandă CAT, nu activează emisia radio. Comenzile brute de emisie sunt blocate.

Nu deschide porturi în router. Serverul ascultă implicit numai pe `127.0.0.1` și adresele locale
Tailscale `100.64.0.0/10`, port 8443. Dacă Tailscale nu este detectat, QR-ul este utilizabil numai local.
API-ul de configurare acceptă alte adrese numai cu `AllowOtherInterfaces=true`; nu există un implicit
`0.0.0.0`. Certificatul este generat la prima pornire; clientul verifică amprenta SHA-256 din asociere.

Fereastra serverului se ascunde în tray la X. Oprirea completă se face prin **Ieșire din aplicație**
în meniul tray. Închiderea clientului îl deconectează și eliberează PTT, dar lasă serverul pornit.

## Protecții la emisie

- PTT se ține apăsat (buton sau Space). Eliberarea, pierderea focusului, Esc și închiderea clientului îl eliberează.
- Un singur proprietar al emisiei; și tunerul/comenzile de emisie trec prin `TransmitGuard`.
- Heartbeat la 250 ms; lipsa lui timp de 1,5 s oprește emisia. Reconectarea (backoff 1/2/4/8/10 s)
  reface starea completă și pornește întotdeauna cu PTT eliberat.
- Limita serverului este 180 s implicit, configurabilă 10–600 s. Deconectarea, pierderea portului,
  revocarea dispozitivului și oprirea serverului trimit `KY00;TX0;` și coboară RTS/DTR.
- SWR mai mare de 3:1 timp de peste 0,5 s oprește emisia. Calibrarea folosește tabela proiectului sursă;
  trebuie verificată pe hardware înaintea operării nesupravegheate.
- La pornire se citește `EX030112` (`GEN_TX_TIMEOUT`). Clientul avertizează dacă TOT este zero sau
  nu a putut fi citit și oferă **Setează TOT la 3 minute**. Unitatea și codul EX trebuie verificate pe FTX-1.
- `transmit.jsonl` păstrează cine, când, frecvența, durata și motivul opririi.

## Audio prin internet

- Actualizează **ambele executabile** din același pachet: protocolul este v2; clienții Etapei 2 sunt refuzați explicit.
- În server, alege perechea USB Audio CODEC (intrare RX / ieșire TX) sau lasă detectarea automată.
  Se păstrează preferința pentru „USB Audio CODEC”, apoi „USB Audio Device”, din `audio.py:guess_codec`.
  Nu se alege automat un difuzor fără legătură cu stația. Simulatorul nu necesită un dispozitiv USB.
- În client, **F2 → Audio prin internet**: activează audio, alege microfonul și căștile și debitul
  **12000 / 24000 / 32000 biți/s**. Modifică aceste setări când clientul este deconectat, apoi reconectează.
  Dispozitivele implicite sunt cele Windows pentru comunicații. Folosește căști pentru evitarea ecoului.
- WASAPI în modul shared folosește formatul endpoint-ului Windows (de regulă 48 kHz);
  WDL face conversia cu filtrare la **16 kHz mono**. Ieșirea este convertită de NAudio la formatul endpoint-ului.
  Opus/Concentus, VOIP, **20 ms**, implicit 24 kbit/s, fără DTX: tăcerea produce în continuare cadre valide.
- Audio circulă în cadre binare pe același WSS autentificat. Tamponul adaptiv urmărește 60–120 ms;
  cozile au dimensiune fixă și elimină cadrele vechi după blocaje, fără acumulare nelimitată de latență.
  Indicatorul arată tamponul curent/ținta și numărul de cadre lipsă ori întârziate. Latența afișată separat
  este RTT-ul de control, **nu** o măsurătoare a latenței acustice de la un capăt la altul.
- PTT vocal necesită audio activ. Microfonul este codificat/transmis numai pentru deținătorul PTT.
  Tampoanele se golesc la pornirea și oprirea emisiei; fiecare emisie primește un identificator nou,
  astfel încât cadrele vechi sau ale altui client nu pot alimenta emisia curentă.
- **Lipsa audio valid peste 1 s oprește TX**, chiar dacă heartbeat-ul de control continuă.
  Oprirea audio, pierderea dispozitivului sau închiderea clientului eliberează PTT.
- Simulatorul generează zgomot și tonuri de 700/1200 Hz și un ecou al microfonului întârziat cu 200 ms.
  Nu este o portare a decodoarelor FT8/CW. Pentru test, serverul rămâne pe Simulator, iar clientul
  folosește microfonul/căștile reale. Astfel se poate verifica fluxul complet fără emisie radio.
- UDP se adaugă numai după măsurători 4G care depășesc constant pragul de 400 ms din plan.
  Android și funcționarea cu ecranul stins sunt implementate în Etapa 4.

### Protocol audio v2

`audioConfig{on,bitrate}` → `audioReady{audioAvailable,audioEpoch,bitrate}`. `pttState` pentru
proprietar include un `audioEpoch` separat, nou la fiecare emisie. Tipurile binare sunt 1 (RX) și 3 (microfon).
Antetul little-endian: tip u8, epoch i64, secvență u32, timp monoton al expeditorului în ms i64,
apoi un singur pachet Opus de 20 ms (maxim 1275 octeți). Secvențele/timpii se referă la flux, nu la ora UTC.
Antetul și audio sunt protejate de TLS; nu există endpoint audio separat neautentificat.

## Date și configurație

Preferințele audio sunt în `server-audio.json` și `desktop-audio.json` în același profil.
Serverul păstrează certificatul, hash-urile tokenurilor, jurnalul și `station/config.json` în
`%APPDATA%/FTX1-CATandRemote/`. `desktop-connection.json` conține tokenul clientului local.
Fișierele de autentificare sunt private; nu le distribui și nu le copia din proiectul Python.
Pe alt PC, asocierea salvează propriul token. Revocarea invalidează conexiunile active și autentificările viitoare.

## Dezvoltare și teste

```sh
dotnet restore Ftx1.sln
dotnet build Ftx1.sln -c Release
dotnet test Ftx1.sln -c Release
dotnet run --project src/Ftx1.Server -- --sim
dotnet run --project src/Ftx1.Desktop
```

Server fără UI, inclusiv pentru teste Linux:

```sh
dotnet run --project src/Ftx1.Server -- --headless --sim --port 8443 --data-dir ./private-station
```

Proiectele server și protocol vizează `net8.0`, astfel încât testele TLS/Kestrel să ruleze și pe Linux;
accesul fizic la stație și autostart-ul sunt funcții Windows. `global.json` acceptă SDK-uri .NET 8.0.1xx și ulterioare.

- `Ftx1.Core`, `Ftx1.Sim`: nucleul Etapei 1 și vectorii Python; 680 de teste.
- `Ftx1.Protocol`: JSON cu source generator, protocol v2 și cadre audio binare cu epoch/secvență/timp.
- `Ftx1.Audio`: Opus/Concentus, jitter buffer adaptiv și simulator audio, fără dependențe Windows.
- `Ftx1.Audio.Windows`: WASAPI/NAudio și conversia PCM pentru server și desktop.
- `Ftx1.Mobile`: UI Avalonia comună, verificabilă și pe desktop fără SDK Android.
- `Ftx1.Android`: gazda nativă, Camera2/ZXing, Keystore, audio nativ și serviciu în prim-plan.
- `Ftx1.Server`: Kestrel, TLS, asociere, tokenuri hash-uite, limitarea încercărilor pe IP, PTT, TOT, SWR, audit și tray.
- `Ftx1.Client`: conexiune cu pinning, reconectare, heartbeat, starea și ViewModel-urile comune.
- `Ftx1.Desktop`: fereastra Avalonia și meniul, fără acces la porturile CAT/PTT.
- Testele de rețea folosesc conexiuni TLS reale locale și simulatorul: token greșit, incompatibilitate de protocol,
  pin greșit, cod expirat/reutilizat, doi clienți, pierderea heartbeat-ului, socket întrerupt,
  reconectare, revocare, SWR, TOT și jurnal. Testele Avalonia verifică fereastra și oprirea PTT la închidere.

CI validează Windows și Ubuntu. La un PR de etapă gata de review, după validare, generează executabilele
și verifică pe Windows serverul publicat (TLS, autentificare, CAT, audio Opus RX/TX simulat și PTT on/off). Există și pornire manuală
cu `publish_windows`. Nu publică executabile doar pentru un push pe `main`.

**De verificat pe stație/telefon:** porturile și toate metodele PTT, citirea/scrierea TOT, pragul SWR,
Tailscale între două dispozitive reale, eliberarea PTT la pierderea internetului și revenirea după schimbarea rețelei.
Clientul Android este implementat în Etapa 4. Audio fizic WASAPI trebuie verificat pe PC-ul stației; testele automate folosesc dispozitive simulate.

Proveniența codului este în [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Proprietarul a declarat
în PLAN.md că aspectul legal este rezolvat; acest repo nu acordă o licență nouă asupra surselor originale.

## De verificat pe stație / PC-ul client

- Selecția perechii USB RX/TX, rata de mixaj Windows și nivelurile microfonului/ALC, inițial cu sarcină artificială.
- Audio RX și emisie SSB cu căști, prin Tailscale; latența acustică și eventualele întreruperi pe conexiunea reală.
- Scoaterea USB/microfonului în TX, pierderea conexiunii, schimbarea clientului și reconectarea fără reactivarea PTT.
- TOT, pragul SWR și limitele PTT din etapele anterioare se păstrează.

Testele automate folosesc dispozitive simulate. Nu înlocuiesc verificarea WASAPI pe placa USB fizică.

## Build și test Android

Soluția `Ftx1.sln` păstrează build/test Windows și Linux fără workload Android. Proiectul mobil se construiește separat:

```sh
dotnet workload install android --skip-manifest-update
dotnet build src/Ftx1.Android -c Debug -p:AndroidSdkDirectory=/path/to/android-sdk -p:JavaSdkDirectory=/path/to/jdk17 -p:EmbedAssembliesIntoApk=true
```

Necesită SDK Android 34 / build-tools 34 și JDK 17. APK-ul autonom semnat este în
`src/Ftx1.Android/bin/Debug/net8.0-android/*-Signed.apk`.
CI construiește întâi APK-ul distribuit. Un al doilea build, cu `SmokeTestBuild=true`, include exclusiv pentru
emulator un test automat cu simulatorul: QR generat/decodat, asociere, Keystore, TLS CAT, audio nativ RX/TX,
focus pierdut, reconectare, PTT eliberat la stingerea ecranului și audio menținut de serviciu/WakeLock.
Intrarea de test nu este compilată în APK-ul distribuit. Camera fizică și Bluetooth se verifică separat pe telefon.

## Oprire și diagnostic audio

**Oprește serverul** oprește conexiunile fără a închide administrarea; setările porturilor și audio se reactivează.
Deschiderea manuală a executabilului nu mai pornește automat serverul, ca să poți configura porturile.
**Ieșire din aplicație** din tray închide întregul program. Erorile de eliberare a resurselor sunt păstrate în
`%APPDATA%/FTX1-CATandRemote/server-errors.log`, iar administrarea rămâne deschisă.

În server, **Verifică audio** testează deschiderea endpoint-urilor și afișează dispozitivele sau cauza exactă.
Erorile din client precizează dacă problema este pe SERVER (USB-ul stației) sau CLIENT (microfon/căști).
Verifică selecțiile, dispozitivele active și permisiunea Windows de acces la microfon pentru aplicații desktop.


### Actualizare 0.4.1: oprire și asociere în LAN

Pe calculatorul server, cu serverul oprit, activați **Permite conexiuni din rețeaua locală (LAN)** și selectați/adaptați adresa IPv4 privată a acelui calculator (de exemplu `192.168.55.167`). Porniți serverul și generați date de asociere noi. Copiați întregul JSON în client și apăsați Asociere; nu combinați manual adresa sau amprenta din două coduri diferite. Codul de asociere este temporar și de unică folosință. Dacă Windows cere acces în firewall, permiteți aplicația în rețeaua privată; pentru conexiuni LAN este necesar TCP 8443. Nu este necesară redirecționarea porturilor în router.

Implicit rămân active numai localhost și Tailscale. LAN necesită activare explicită și este limitat la adresa privată configurată. Asocierea locală nu folosește proxy HTTP; validarea amprentei certificatului rămâne obligatorie. Mesajele disting certificatul diferit, codul expirat și serverul inaccesibil.

Audio WASAPI rulează într-un proces separat. La oprire, serverul dezactivează mai întâi emisia; procesul audio primește comanda de oprire și este terminat dacă driverul se blochează. Erorile audio sunt în `server-audio-worker.log` / `client-audio-worker.log`, în directorul de date al aplicației. Aceste modificări sunt verificate automat cu audio simulat, inclusiv cădere și blocare a procesului; testarea driverului USB real necesită transceiverul utilizatorului.


### Corecție 0.4.2: crash la citirea portului CAT

Evenimentul Windows a identificat o `OperationCanceledException` netratată în firul de citire CAT. Anularea citirii la închiderea portului este acum normală; dacă apare în timpul funcționării, este raportată ca deconectare către Rig, cu protecțiile PTT existente. Sunt testate anularea la oprire, erorile din driver și reluarea buclei de citire. Excepțiile globale netratate sunt înregistrate în `server-errors.log`. Nu este necesară resetarea asocierilor sau a certificatului. Driverul USB real rămâne de verificat pe stație.

# FTX1-CATandRemote · Etapa 2 (fără audio)

Control CAT al Yaesu FTX-1 printr-un server Windows și un client desktop Avalonia.
Etapa 2 din [PLAN.md](PLAN.md) folosește planul v2: **server și protocol înaintea audio**.
Comportamentul CAT provine din `Eugen68/ftx1-pult`, commit `d453771` (2.10.2).
Meniul și fereastra „Setări stație” adăugate de proprietar în Etapa 1 sunt păstrate în client.

Etapa 1 este încă în PR #1. Această etapă este un PR separat bazat pe el și include planul actualizat
 din `main`, fără să facă merge în locul proprietarului. Testarea pe stație și decizia de merge rămân la proprietar.

## Pachetul Windows

1. Extrage arhiva. Nu este necesară instalarea .NET separat.
2. Pe PC-ul stației, pornește **FTX1-Server.exe**. Inițial este selectat simulatorul.
3. Panoul serverului conține porturile CAT/PTT, simulatorul, limita emisiei, pornirea cu Windows,
   QR-ul de asociere și lista dispozitivelor revocabile. Pentru modificarea conexiunii oprește serverul,
   schimbă setările, apoi pornește-l din nou.
4. Pe același PC pornește **FTX1-CATandRemote.exe** și apasă **Conectare**. Serverul a scris un token
   de dispozitiv și amprenta certificatului în profilul utilizatorului; localhost nu ocolește autentificarea.
5. Pentru un alt PC instalează Tailscale pe ambele calculatoare. Generează un QR nou în server.
   În client, **Stație → Setări stație (F2)**: copiază datele JSON de sub QR în câmpul de asociere,
   alege un nume, apasă **Asociază dispozitivul**, apoi **Conectare**. Codul este valabil cinci minute
   și este consumat o singură dată. Scanarea cu camera telefonului vine în Etapa 4.
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

**Nu există audio în Etapa 2.** Regula „microfon oprit peste o secundă ⇒ TX0” se implementează
împreună cu fluxul audio în Etapa 3. Evenimentele Android și serviciul de fundal vin în Etapa 4.
Testele de purtător / PTT se fac doar cu sarcină artificială, în modul adecvat; nu se pretinde că
PTT produce singur un purtător în fiecare mod al transceiverului.

## Date și configurație

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
- `Ftx1.Protocol`: JSON cu source generator, versiune și tipuri binare rezervate pentru etapele viitoare.
- `Ftx1.Server`: Kestrel, TLS, asociere, tokenuri hash-uite, limitarea încercărilor pe IP, PTT, TOT, SWR, audit și tray.
- `Ftx1.Client`: conexiune cu pinning, reconectare, heartbeat, starea și ViewModel-urile comune.
- `Ftx1.Desktop`: fereastra Avalonia și meniul, fără acces la porturile CAT/PTT.
- Testele de rețea folosesc conexiuni TLS reale locale și simulatorul: token greșit, incompatibilitate de protocol,
  pin greșit, cod expirat/reutilizat, doi clienți, pierderea heartbeat-ului, socket întrerupt,
  reconectare, revocare, SWR, TOT și jurnal. Testele Avalonia verifică fereastra și oprirea PTT la închidere.

CI validează Windows și Ubuntu. La un PR de etapă gata de review, după validare, generează executabilele
și verifică pe Windows serverul publicat (TLS, autentificare, CAT și PTT on/off). Există și pornire manuală
cu `publish_windows`. Nu publică executabile doar pentru un push pe `main`.

**De verificat pe stație/telefon:** porturile și toate metodele PTT, citirea/scrierea TOT, pragul SWR,
Tailscale între două dispozitive reale, eliberarea PTT la pierderea internetului și revenirea după schimbarea rețelei.
Android și audio nu sunt încă implementate. Nu a fost efectuat un test pe FTX-1 fizic.

Proveniența codului este în [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Proprietarul a declarat
în PLAN.md că aspectul legal este rezolvat; acest repo nu acordă o licență nouă asupra surselor originale.

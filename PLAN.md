# Plan de acțiune v2: FTX-1 controlat de pe telefon prin internet (C# .NET 8 + Avalonia)

**Scopul principal:** controlul complet al stației Yaesu FTX-1 **de pe un telefon Android, prin internet, inclusiv emisie**.
Desktopul este un client ca oricare altul. Restul funcțiilor (FT8, moduri digitale etc.) vin după ce telefonul funcționează.

Destinatar: un agent AI sau un programator care execută planul etapă cu etapă.
Repo: `Eugen68/ftx1-CATandremote`. Sursa de adevăr pentru comportamentul stației: `Eugen68/ftx1-pult` (Python), commit `d453771`.

Date fixe de la proprietar:
- telefon **Android**; stația are internet pe **fibră optică**; PC-ul de la stație e Windows;
- aspectul legal e rezolvat; proiectul se face de la zero, proiectul vechi nu se mai repară.

---

## ▶ Stare curentă și cum se continuă (actualizat 14.11.2026)

- **Etapa 1: gata**, în PR [#1](https://github.com/Eugen68/ftx1-CATandremote/pull/1) (`feat/stage1-cat-simulator-ui`), **încă nemerge-uit**. Conține nucleul CAT, simulatorul, protecția PTT, UI-ul desktop, plus meniul, fereastra „Setări stație” și consola CAT cu butonul **Transmite** (păstrate la cererea proprietarului). 683 de teste verzi.
- **Ce urmează, în ordine:**
  1. Proprietarul testează PR #1 pe stație (lista „De verificat pe stație” din README), cu sarcină artificială, apoi face merge.
  2. Corectura `global.json` (vezi Etapa 1, „Înainte de merge”), într-un PR mic sau direct pe branch-ul PR #1.
  3. **Etapa 2**, pornind de la `main` după merge, pe branch nou `feat/stage2-server-protocol`.
- **Reguli pentru executant:**
  - Cererile noi ale proprietarului se **trec întâi în acest plan**. Se implementează doar dacă proprietarul cere explicit implementarea.
  - Commit-urile făcute de agent folosesc `git config user.name Claude` și `user.email noreply@anthropic.com` (altfel GitHub le arată „Unverified”). Commit-urile vechi `b6c1973`, `aa2b35e` (pe `main`) și `01b49bd` (PR #1) rămân „Unverified”, decizie a proprietarului. **Nu se rescrie istoricul și nu se face force-push.**
  - Fișierele de build (`bin/`, `obj/`) nu se urcă.
- Mediu Linux: `apt-get install -y dotnet-sdk-8.0` (8.0.1xx). Până la corectura `global.json`, testele se rulează cu un `global.json` temporar `{"sdk":{"version":"8.0.100","rollForward":"latestFeature"}}`, nefăcut commit.

## Cereri noi ale proprietarului (14.11.2026)
- CI generează executabile pentru PR-urile de etapă gata de review, după validare; se păstrează și pornirea manuală.
- Executabil nou din Etapa 1, incluzând meniul adăugat de proprietar.
- Continuare explicită cu Etapa 2 din acest plan v2; executabil după fiecare etapă.
- Etapa 2 este pregătită pe branch separat, bazat pe PR #1 până la merge-ul lui;
  proprietarul păstrează verificarea hardware și decizia de merge. Nu se declară testele hardware efectuate.

## Continuare autorizată — Etapa 3 (14.11.2026)

- Proprietarul a testat Etapa 2 și a confirmat că funcționează.
- Cerere explicită: începerea Etapei 3, audio Opus prin internet și executabile la final.
- Branch `feat/stage3-internet-audio`, bazat pe Etapa 2 testată, până la merge-ul PR-urilor anterioare; `main` rămâne nemodificat.
- Se implementează WASAPI, Opus 16 kHz/20 ms, tampon adaptiv, simulator cu ecou și oprire PTT la lipsa audio. UDP se amână până există măsurători 4G peste pragul din plan.

## Continuare autorizată — Etapa 4 (14.11.2026)

- Proprietarul cere explicit începerea Etapei 4: aplicație Android și APK la final.
- Se păstrează serverul și protocolul v2 din Etapa 3 (PR #3), fără merge automat în main.
- Branch nou `feat/stage4-android`, bazat pe Etapa 3 până la merge-ul etapelor anterioare.
- Se implementează UI mobil Avalonia, QR cu Camera2/ZXing, token protejat cu Android Keystore,
  AudioRecord/AudioTrack, serviciu în prim-plan cu WakeLock și notificare, protecțiile de lifecycle.
- PTT din notificare/căști rămâne opțional și nu se introduce PTT blocat.
- Se verifică build-ul APK, testele de lifecycle și emulatorul; testarea fizică USB/4G/Bluetooth rămâne la proprietar.

## Probleme raportate de proprietar în timpul Etapei 4

- „Oprește serverul” deconectează stația, apoi închide întregul proces; după repornire porturile sunt gri.
- Clientul se conectează, dar raportează audio indisponibil.
- Cerere de corectare integrată în Etapa 4: oprire fără excepții netratate, reactivarea setărilor și pornire din nou;
  diagnostic audio explicit pe server/client, fără slăbirea protecțiilor PTT.

## Corecții după testarea versiunii 0.4.0

- Proprietarul raportează în continuare crash la Oprește serverul pe hardware real și timeout la asociere folosind 192.168.55.167:8443.
- Adăugăm acces LAN explicit, dezactivat implicit, limitat la o adresă IPv4 privată selectată; QR-ul folosește adresa pe care serverul ascultă efectiv.
- Asocierea folosește conexiune directă (fără proxy), diagnostic clar de rețea/certificat/cod expirat; TLS pinning rămâne obligatoriu.
- WASAPI rulează într-un proces audio separat, cu oprire limitată în timp: un driver care cade sau se blochează nu trebuie să închidă administrarea/CAT. PTT este oprit înainte de închiderea procesului audio.
- Build Windows nou după teste. Verificarea USB real și firewall rămâne la proprietar.

## Diagnostic confirmat din Windows (15.11.2026)

- Evenimentul .NET Runtime 1026 arată `System.OperationCanceledException` din `SerialStream.EndRead` → `SerialPort.Read` → `SerialTransport.Read`.
- Firul CAT nu tratează anularea citirii; excepția netratată închide procesul. Corecția 0.4.2 tratează anularea la închidere și raportează o eroare de transport când apare în timpul funcționării.
- Se adaugă teste care injectează anularea în bucla reală de citire, plus oprire/restart. Jurnalul global înregistrează excepțiile netratate fără a continua un proces compromis.
- Build Windows după CI; verificarea portului USB real rămâne la proprietar. LAN și audio nu sunt cauza demonstrată de acest eveniment.

## Corecție scanner QR Android (15.11.2026)

- Proprietarul raportează închiderea/instantaneul nepotrivit imediat ce camera vede QR-ul.
- Scannerul păstrează previzualizarea live, validează citiri repetate ale aceluiași cod și cere confirmare explicită înainte de a reveni la asociere.
- Se configurează focalizare continuă dacă dispozitivul o suportă, rezoluție YUV suportată de cameră și previzualizare fără deformarea proporțiilor.
- Fiecare Image este închis explicit după copierea planului Y, înainte de ZXing; și cadrele omise sunt eliberate pentru a nu bloca ImageReader.
- Se testează validarea/stabilizarea și conversia planului YUV, plus APK și emulator: trei deschideri/închideri ale scannerului, fiecare cu cel puțin zece cadre din camera virtuală.
- Proprietarul confirmă că după mai multe încercări aplicația se închidea; citirea ImageReader este acum protejată și la închiderea camerei. Focalizarea și încadrarea pe telefonul fizic rămân de verificat la proprietar.
- APK nou 0.4.3, compatibil cu serverul Windows 0.4.2; fără modificări CAT/PTT sau resetarea asocierilor.

## Android: difuzor, volum și PTT tactil (15.11.2026)

- Proprietarul confirmă asociere și RX audio, dar sunet slab și PTT care nu funcționează din buton. Cere buton pentru difuzorul telefonului.
- Se introduce alegere explicită difuzor/automat, afișarea rutei reale și volum telefon pe fluxul VoiceCall folosit de AudioTrack, inclusiv tastele fizice. Nu modificăm automat nivelul AF al stației sau volumul sistemului la maxim.
- PTT folosește evenimente pointer în faza tunnel, cu captură și eliberare la ridicare/pierderea capturii/detașare; se testează atingerea efectivă a controlului, nu numai metoda Press.
- Schimbarea rutei eliberează PTT; nu există reluare automată a emisiei. Politica de focus/rețea/serviciu rămâne obligatorie.
- APK 0.4.4 după teste UI, Windows/Linux și emulator cu atingere ADB, rută difuzor și volum. Hardware-ul stației și nivelul acustic real rămân de verificat la proprietar.

## Confirmat de proprietar și cereri noi (18.11.2026)

- **Confirmat:** emisia de pe telefon (SM-S908B) funcționează cap-coadă, inclusiv modulația. Cauza lipsei de modulație a fost o setare pe stație/PC, nu codul. Jurnalul `transmit.jsonl` a arătat emisii de 2–3 s terminate cu `Released`, deci audio de la microfon ajungea la server.
- Cererile de mai jos sunt **doar trecute în plan**. Se implementează numai la cererea explicită a proprietarului.

### ★ PRIORITATE: întreruperi audio pe 4G (se face înaintea punctelor A și B)
**Măsurătoare (proprietar, 18.11.2026):**
- Android pe 4G+, prin VPN IPsec telefon ↔ router ASUS GT-AC2900 (nu Tailscale).
- În timpul întreruperilor: tampon 100–120 ms / țintă 100, **lipsă 78**, **întârziate 123**.

**Diagnostic:**
- Blocare TCP de tip head-of-line: TCP se oprește până la retransmiterea pachetului pierdut, tamponul se golește („lipsă”), apoi rafala sosită e aruncată de plafonul de 120 ms („întârziate”).
- Măsurătoarea îndeplinește condiția din Etapa 3 pentru canalul UDP.

**Branch nou `feat/audio-resilience`, bazat pe `feat/stage4-speaker-ptt`, versiunea 0.5.0. Pașii, în ordine:**

1. **Tampon adaptiv (`JitterBuffer`), în ambele sensuri.**
   - Ținta se calculează din jitter **și** din istoricul rafalelor: durata ultimelor goluri, pe o fereastră de ~10 s.
   - Plafon 400 ms; minimul rămâne 60 ms. Ținta scade lent (de exemplu 20 ms la 5 s fără rafale).
   - Cadrele întârziate se păstrează cât încap sub plafon; nu se mai aruncă rafala întreagă.
   - Când tamponul depășește ținta cu ≥ 60 ms, recuperarea se face fără salturi audibile: întâi se elimină cadrele de liniște sau cu energie mică, apoi, dacă nu ajunge, redare accelerată ~5% (rescalare simplă pe cadre de 20 ms).
   - Linia de calitate afișează și „întârziere estimată” și numărul de rafale.
2. **Opus FEC + PLC.**
   - Encoder: `UseInbandFEC = true`, `PacketLossPercent` ≈ 10 (ajustabil după pierderi).
   - La un cadru lipsă, dacă următorul e deja în tampon ⇒ decodare FEC din următorul; altfel PLC (decodare fără pachet), nu liniște.
   - Se verifică dacă `Decode([])` din Concentus face deja PLC; dacă nu, se corectează.
3. **Canal audio UDP**, cu WebSocket ca rezervă.
   - Serverul ascultă UDP pe aceleași interfețe permise ca HTTPS (§1, inclusiv opțiunea LAN), port configurabil (implicit 8444).
   - La `audioReady`, serverul trimite pe WebSocket/TLS portul UDP și o cheie de sesiune aleatoare de 256 de biți.
   - Clientul trimite pachete `udpHello` autentificate (HMAC-SHA256 pe cheia sesiunii) până primește confirmare. Serverul învață adresa clientului doar din pachete cu HMAC valid.
   - Pachet: tip, epocă, secvență, timestamp, payload Opus și HMAC trunchiat (16 octeți). Pachetele cu HMAC invalid, epocă greșită sau secvență repetată se ignoră fără răspuns.
   - Keepalive la 5 s, ca NAT-ul și tunelul IPsec să nu închidă legătura.
   - Dacă nu vine niciun pachet UDP valid 2 s ⇒ trecere automată pe WebSocket, cu reîncercare UDP periodică. Indicatorul de calitate arată „UDP” sau „TCP”.
   - Comenzile, starea și PTT-ul (`pttRequest`, heartbeat) rămân pe WebSocket/TLS.
4. **Siguranța la emisie (§2) rămâne neslăbită.**
   - Regula 3 („fără audio 1 s ⇒ `TX0`”) se calculează pe **sosirea** pachetelor valide de microfon la server, indiferent de canal (UDP sau WS), nu pe redare. Tamponul mai mare nu întârzie oprirea.
   - La eliberarea PTT, tamponul de emisie se golește imediat; sunetul rămas în tampon nu se mai trimite spre stație după `TX0`.
   - Testele existente pentru heartbeat, deconectare și lipsă audio trec neschimbate, plus aceleași teste pe UDP.
5. **Teste.**
   - Simulator de rețea în teste: întârziere, jitter, pierderi, **blocaje TCP de 200–500 ms urmate de rafale**.
   - Criteriu: la profilul „4G cu blocaje”, cadrele aruncate scad cu ≥ 80% față de 0.4.4, iar întârzierea medie ≤ 400 ms.
   - UDP: HMAC invalid, epocă veche și replay ignorate; trecerea UDP ⇄ WS fără blocarea PTT.
6. **De verificat pe telefon:** 4G+ cu IPsec, comparând numerele de calitate cu măsurătoarea de mai sus, pe UDP și pe TCP.

**Stare (18.11.2026): pașii 1 și 2 făcuți** pe `feat/audio-resilience` (0.5.0). Pasul 3 (UDP) urmează pe același branch.
- `JitterBuffer`:
  - ținta urmărește jitter-ul și blocajele din ultimele 10 s (60–400 ms);
  - la golire fără cadre noi (blocaj) se face PLC **fără a consuma secvența**, deci rafala se redă în loc să fie „întârziată”;
  - un cadru lipsă cu cadre mai noi deja sosite se reface din FEC.
- `AudioPlayout` (nou, folosit de client și de server): decodare, FEC, PLC, recuperare prin sărirea cadrelor liniștite sau redare cu ~5% mai rapidă.
- Emisie: plafonul tamponului de microfon pe server este **240 ms**, nu 400. Sunetul rămas în tampon la eliberarea PTT se taie, deci un plafon mic limitează sfârșitul de cuvânt pierdut. Regula 3 rămâne pe sosirea pachetelor.
- **Criteriul de la punctul 5, ajustat după măsurare.** Profilul de test are blocaje de 200–500 ms la ~3 s, adică mai lungi decât plafonul de 400 ms. Rezultate față de comportamentul 0.4.4 (plafon 120 ms):
  - cadre întârziate sau aruncate: 258 → 55 (−79%), dintre care întârziate: 0;
  - goluri audibile: 159 → 38;
  - total întreruperi audibile: −78%;
  - întârziere medie: ~370 ms.
  
  Criteriul din test este ≥ 75%. Pe acest profil, 80% nu se poate atinge fără plafon mai mare de 400 ms (deci mai multă întârziere). O încercare de a crește tamponul prin încetinire nu a adus câștig net și a fost abandonată.

**Rețea (fără cod), ASUS GT-AC2900:**
- MSS clamping pentru traficul din tunelul IPsec. Firmware-ul stock nu are opțiune în interfață. Cu Asuswrt-Merlin: script `firewall-start` cu `iptables -t mangle -A FORWARD -p tcp --tcp-flags SYN,RST SYN -j TCPMSS --set-mss 1350`.
- Alternativă: Tailscale (§1), care folosește MTU 1280 și evită problema.

### A. PTT pe tasta de volum (Android)
Butonul Power nu poate fi folosit: Android nu livrează `KEYCODE_POWER` aplicațiilor. Se folosește tasta de volum.
1. **Pasul 1: aplicația în prim-plan.**
   - Opțiune în Setări, **dezactivată implicit**: „PTT pe tasta de volum”, cu alegerea Volum− sau Volum+.
   - `KeyDown` (fără repetări) = cerere PTT, `KeyUp` = eliberare. Tasta aleasă nu mai reglează volumul. Cealaltă tastă păstrează reglajul de volum din 0.4.4.
   - Cât opțiunea e activă și clientul e conectat, ecranul rămâne aprins (`FLAG_KEEP_SCREEN_ON`), cu luminozitate redusă.
   - Pierderea focusului ferestrei, `onPause`, ecranul stins și deconectarea ⇒ eliberare PTT (regula 4 din §2).
2. **Pasul 2: ecran stins, experimental.**
   - `AccessibilityService` cu `FLAG_REQUEST_FILTER_KEY_EVENTS`, activat manual de proprietar din Setări → Accesibilitate.
   - Se păstrează doar dacă pe S22 (SM-S908B) primește apăsarea și eliberarea tastei cu ecranul stins. Dacă nu, se renunță și se rămâne la Pasul 1.
   - **Protecție de buzunar:** cu ecranul stins, PTT pe volum este refuzat cât timp senzorul de proximitate e acoperit. Acoperirea senzorului în timpul emisiei ⇒ eliberare PTT.
   - Varianta `MediaSession`/`VolumeProvider` se respinge: nu raportează eliberarea tastei, deci nu poate face PTT „cât ții apăsat”.
3. Rămân neschimbate: PTT doar cât ții apăsat, fără PTT blocat; `MaxTxSeconds`; oprirea la lipsa audio (regula 3 din §2); heartbeat.
4. **Teste:**
   - pe ViewModel: down/up, repetările ignorate, focus pierdut, proximitate acoperită;
   - pe emulator: `adb shell input keyevent`;
   - de verificat pe telefon: ecran stins pe S22, buzunar.
5. Ulterior, opțional: buton PTT Bluetooth (BLE) sau butonul căștii, după ce proprietarul alege un model.

### B. Sliderul AF (volumul difuzorului stației) mutat din ecranul principal
- La operarea de la distanță, AF reglează doar difuzorul stației. Nu influențează sunetul primit prin USB (acela depinde de `…_USB_OUT_LEVEL`) și încurcă pe ecranul principal.
- **Desktop:** sliderul AF se scoate din `MainWindow` și se mută în fereastra „Setări stație” (lângă consola CAT).
- **Android:** sliderul AF se scoate din ecranul principal și se mută în ecranul Setări. Volumul telefonului rămâne pe ecranul principal.
- Valoarea și legătura (`Af`, comanda `AG`) rămân aceleași; se mută doar controlul. Teste UI actualizate.

## 0. Reguli de lucru

1. **O etapă = un branch + un pull request.** Nu lucra direct pe `main`. CI verde e obligatoriu.
2. **Comportamentul stației se copiază, nu se inventează.** Formatele CAT vin din `ftx1-pult/server/*.py`, prin vectorii din `tests/data` (vezi `tools/generate_reference.py`).
3. **Siguranța la emisie (§2) nu se slăbește niciodată.** Orice cale care pornește emisia trece prin `TransmitGuard`.
4. **Testele vin odată cu codul.** Rețeaua se testează cu clienți simulați: latență, pierderi și deconectări provocate.
5. Executantul nu are stația și nici telefonul. Testează cu simulatorul și emulatorul Android. În descrierea PR-ului scrie **„De verificat pe stație/telefon: …”**.
6. Textele interfeței stau doar în `Resources/Strings.ro.axaml`.

## 1. Arhitectura

```
[FTX-1] ──USB── [PC stație: Ftx1.Server]  ══ Tailscale (WireGuard) ══  [Android: Ftx1.Android]
                  CAT · audio · TransmitGuard                            [PC acasă: Ftx1.Desktop]
                  rulează în fundal, pornește cu Windows
```

- **`Ftx1.Server`**: singurul proces care atinge stația (CAT, audio, PTT). Rulează fără fereastră, cu iconiță în tray, și pornește automat cu Windows.
- **Clienții** (Desktop și Android) folosesc **același protocol** și **aceleași ViewModel-uri** (`Ftx1.Client`). Și pe PC-ul de la stație, interfața se conectează la `localhost` prin protocol, deci există o singură cale de cod.
- **Rețeaua: Tailscale** (gratuit, WireGuard), pe PC-ul stației și pe telefon. **Nu se deschide niciun port în router.** Serverul ascultă implicit doar pe interfața Tailscale (`100.x.y.z`) și pe `127.0.0.1`. Ascultarea pe orice altă interfață cere o opțiune explicită.

Structura soluției după Etapa 3:
```
src/
  Ftx1.Core/      # existent: CAT, Rig, TransmitGuard, config
  Ftx1.Sim/       # existent: simulator CAT (+ audio din Etapa 3)
  Ftx1.Protocol/  # NOU: mesaje, serializare, versiune protocol (net8.0, fără dependențe UI)
  Ftx1.Server/    # NOU: gazda stației (Windows), WebSocket/TLS, asociere, audio server
  Ftx1.Audio/     # NOU: NAudio WASAPI (Windows) + Opus (Concentus, cross-platform)
  Ftx1.Client/    # NOU: conexiune, reconectare, ViewModel-uri comune (mutate din Ftx1.App)
  Ftx1.Desktop/   # redenumit din Ftx1.App: doar fereastra Avalonia desktop
  Ftx1.Android/   # NOU: Avalonia.Android, audio Android, serviciu în prim-plan
tests/ ...
```

## 2. Siguranța la emisie de la distanță (obligatorie, cu teste)

Internetul mobil cade des: tunel, schimbare de celulă, ecran blocat. Fiecare nivel de mai jos are test automat.

| # | Protecție | Unde |
|---|---|---|
| 1 | PTT numai „cât ții apăsat”; nu există PTT blocat permanent | client |
| 2 | Heartbeat client → server la 250 ms în timpul emisiei; lipsă 1,5 s ⇒ `TX0` (`LostHeartbeat`, existent în `TransmitGuard`) | server |
| 3 | Fluxul audio de la microfon se oprește > 1 s în timpul emisiei ⇒ `TX0` | server |
| 4 | Android: `onPause`, ecran stins fără serviciu activ, apel telefonic (`AudioFocus` pierdut), schimbare de rețea, BT deconectat ⇒ eliberare PTT | client |
| 5 | Timp maxim de emisie `MaxTxSeconds` (implicit 180, 10–600) | server (existent) |
| 6 | SWR peste prag (implicit ~3:1, citit din `RM6`) mai mult de 0,5 s ⇒ `TX0` + notificare | server |
| 7 | La conectare, serverul citește **TOT** (Time-Out Timer) din meniul EX al stației; dacă e dezactivat, afișează avertisment cu buton „Setează 3 min”. E ultima apărare dacă PC-ul se blochează. Meniul: **EX030112 `GEN_TX_TIMEOUT`** (2 cifre, 0–30; 0 = dezactivat, unitatea — minute, de verificat pe stație). | server + stație |
| 8 | Un singur deținător al emisiei; jurnal persistent cu cine, când, frecvență, durată și motivul opririi | server |
| 9 | Oprirea serverului, pierderea portului sau o excepție ⇒ `KY00;TX0;` + RTS/DTR jos (existent) | server |

## 3. Securitate

- **Asocierea telefonului se face o singură dată, prin cod QR afișat pe PC-ul stației.** QR-ul conține: adresa Tailscale, portul, **amprenta SHA-256 a certificatului** serverului și un cod de asociere de unică folosință (valabil 5 minute).
- Serverul generează un certificat auto-semnat la prima pornire. **Clientul acceptă doar certificatul cu amprenta din QR (certificate pinning)**, deci nu apar avertismente de certificat și nu e posibil un atac de tip man-in-the-middle.
- La asociere, clientul primește un **token de dispozitiv** de 256 de biți. Serverul păstrează doar hash-ul. Pe Android, tokenul stă în Android Keystore. Dispozitivele asociate apar în lista serverului și pot fi revocate.
- Autentificare la fiecare conexiune: token + comparare constantă (`CryptographicOperations.FixedTimeEquals`). Încercările eșuate se limitează pe IP.
- Conexiunile de la `localhost` **nu primesc privilegii automate** (lecția din proiectul vechi). Desktopul local se asociază și el, automat, prin fișierul de token din profilul utilizatorului Windows.
- Comenzile CAT brute din consolă rămân filtrate (fără `TX`/`KY` care ocolesc `TransmitGuard`, regulă existentă).

---

## Etapa 1 — Nucleul CAT + simulator + UI desktop ✅ (PR #1)
Implementat pe branch-ul `feat/stage1-cat-simulator-ui`: `Ftx1.Core`, `Ftx1.Sim`, `Ftx1.App`, 682 de teste.
Adăugat în același PR (cerință nouă a proprietarului): **meniu** (Stație: Conectare, Setări stație… F2, Ieșire · Unelte: Consolă CAT… F12) și fereastra nemodală **„Setări stație”** cu tab-urile *Conexiune* (port, viteză, PTT, limită emisie, simulator) și *Consolă CAT* (jurnal `>`/`<`, câmp de comandă + buton **Transmite**/Enter; comenzile care pornesc emisia rămân blocate).
**Înainte de merge:**
- [ ] Proprietarul testează pe stație punctele din README („De verificat pe stație”), cu sarcină artificială.
- [x] `global.json` cere SDK `8.0.400`. Setează `"rollForward": "latestMajor"` sau o versiune minimă `8.0.100`, ca să compileze și cu SDK-urile din distribuțiile Linux (`apt` oferă 8.0.1xx).

## Etapa 2 — Serverul și protocolul de rețea (fără audio)
1. **`Ftx1.Protocol`:** mesaje JSON (System.Text.Json, source generator) pe WebSocket.
   - `hello{protocolVersion, deviceToken}` → `helloOk{serverVersion, state}` / `authFail`.
   - `state{diff}` (dicționarul existent din `Rig.Changed`), `set{key,value}`, `act{name,arg}`, `menuGet/menuSet`.
   - `pttRequest{on}`, `pttHeartbeat`, `pttState{owner, you, reason}`.
   - `ping{ts}`/`pong{ts}` pentru măsurarea latenței, afișată în client.
   - Cadrele binare (audio, waterfall) au un octet de tip, ca în `ftx1-pult`.
   - Câmpul `protocolVersion`: serverul refuză clienții incompatibili cu un mesaj clar.
2. **`Ftx1.Server`:** Kestrel (ASP.NET Core minimal, `net8.0-windows` + rulare și pe Linux pentru teste).
   - HTTPS pe un port configurabil (implicit 8443), endpoint-ul `/ws`, certificat auto-semnat generat cu `CertificateRequest`.
   - Ascultă doar pe interfețele permise (§1): detectează adaptorul Tailscale după intervalul `100.64.0.0/10`.
   - Asocierea (§3): `POST /pair` cu codul din QR → token. Fereastra mică de administrare afișează QR-ul (pachetul `QRCoder`) și lista dispozitivelor.
   - Mută `Rig` + `TransmitGuard` în server. Clientul care cere PTT devine deținătorul emisiei, cu `isRemote: true`.
   - Pornire cu Windows: cheia `HKCU\...\Run` (ca în proiectul vechi), opțiune în panoul de administrare.
3. **`Ftx1.Client`:** mută `MainViewModel` din `Ftx1.App` și înlocuiește apelurile directe către `Rig` cu mesaje de protocol.
   - Reconectare automată cu backoff (1, 2, 4… max 10 s).
   - La reconectare: stare completă, iar **PTT pornește mereu eliberat**.
4. **`Ftx1.Desktop`:** fosta `Ftx1.App`, devenită doar client. Simulatorul rulează acum în server (`--sim`).
5. **Teste:**
   - server + client în același proces, cu simulatorul;
   - client fără token sau cu token greșit ⇒ refuzat;
   - heartbeat oprit ⇒ `TX0` în ≤ 1,75 s;
   - două clienți cer PTT ⇒ al doilea refuzat;
   - deconectare bruscă (socket închis) ⇒ `TX0`;
   - pinning: certificat diferit ⇒ clientul refuză conexiunea.

**Acceptare:** desktopul de acasă, prin Tailscale, controlează stația și poate transmite (fără audio, de exemplu un purtător în modul CW sau un test cu sarcină artificială).

## Etapa 3 — Audio prin internet
Sursa de inspirație: `ftx1-pult/server/audio.py`, `web/worklets.js`.
1. **`Ftx1.Audio` (server, Windows):** NAudio WASAPI pe „USB Audio CODEC” (detectare automată ca `guess_codec`), 48 kHz mono.
2. **Codec: Opus** (pachetul `Concentus`, C# pur, merge și pe Android).
   - Recepție: 48 kHz → 16 kHz, cadre de 20 ms, ~24 kbit/s, mod VOIP.
   - Emisie: la fel, în sens invers. Bitrate selectabil (12/24/32 kbit/s), pentru date mobile slabe.
3. **Transport:** cadre binare pe același WebSocket (simplu și trece prin orice).
   - **Jitter buffer adaptiv** în client (țintă 60–120 ms), cu aruncarea cadrelor întârziate în loc de acumulare. Indicator de calitate: latență, pierderi, nivelul tamponului.
   - Dacă latența totală măsurată pe 4G depășește constant 400 ms, se adaugă **canal audio UDP** (pachete Opus cu număr de secvență + HMAC pe cheia sesiunii, în tunelul Tailscale deja criptat). Abia atunci, nu dinainte.
4. **Emisia:** audio de la microfonul clientului → Opus → server → WASAPI către stație, doar cât `TransmitGuard` e activ. Tamponul de emisie se golește la pornirea și la oprirea PTT. Regula 3 din §2.
5. **Simulator audio:** zgomot + câteva semnale (port simplificat din `SimAudio` din `sim.py`), plus ecou: ce transmite clientul se aude înapoi întârziat, pentru testarea fluxului complet.
6. **Teste:**
   - Opus encode/decode, cu verificarea nivelului și a frecvenței unui ton;
   - jitter buffer cu întârzieri și pierderi simulate;
   - flux audio oprit în emisie ⇒ `TX0`.

**Acceptare:** de pe desktopul de acasă se aude recepția și se poate face un QSO SSB prin internet.

## Etapa 4 — Aplicația Android
1. **`Ftx1.Android`:** Avalonia.Android 11.2.x, `net8.0-android`, minSdk 26. Folosește ViewModel-urile din `Ftx1.Client`.
2. **Interfața de telefon** (portret), un singur ecran principal:
   - frecvență mare, tuning prin glisare sau rotiță virtuală, pas selectabil;
   - mod, bandă, S-metru, la emisie PO/SWR/ALC;
   - **buton PTT mare** (ține apăsat), cu vibrație la pornire și la oprire și culoare roșie în emisie;
   - AF, putere, MIC, NB/DNR, indicator de latență și calitate.
   - Ecrane secundare: setări, dispozitive, consolă CAT.
3. **Asociere:** scanare QR cu camera (ZXing.Net.Maui sau `CameraX` + ZXing). Tokenul se salvează în Android Keystore.
4. **Audio Android:** `AudioRecord`/`AudioTrack` în modul `VOICE_COMMUNICATION` (cu anulare de ecou). Suport pentru **căști Bluetooth (SCO)** și căști cu fir.
5. **Funcționare cu ecranul stins:** `ForegroundService` cu notificare permanentă („Conectat la FTX-1 · 14.074 USB”), `WakeLock` parțial cât timp e conectat. Opțional, PTT din notificare sau de la butonul căștii Bluetooth (de verificat ulterior).
6. **Regula 4 din §2:** toate evenimentele Android care eliberează PTT, fiecare cu test (instrumentation test sau test pe ViewModel cu evenimente simulate).
7. **Build:** CI construiește APK-ul semnat cu o cheie de debug (artefact). Cheia de release vine de la proprietar, ca secret GitHub, mai târziu.

**Acceptare:** de pe telefon, prin 4G, se face un QSO SSB complet; închiderea aplicației sau oprirea rețelei în timpul emisiei opresc emisia (de verificat cu sarcină artificială).

## Etapa 5 — Robustețea stației nesupravegheate
- Serverul repornește conexiunea CAT și audio după deconectarea USB (există `Reopen` în `Rig`). Audio: re-detectare dispozitiv.
- Watchdog de proces: dacă serverul se blochează, un mic proces de supraveghere îl repornește.
- Jurnal de emisie (§2.8), vizibil din telefon.
- Pornire și oprire stație: `PS0;` pentru oprire. Pornirea prin CAT de verificat pe FTX-1; alternativă: priză inteligentă, integrare opțională ulterioară.
- Notificări pe telefon: stație deconectată, SWR mare, server repornit.
- Documentație pentru proprietar: Tailscale pe PC și telefon, BIOS „Power on after AC loss”, UPS, TOT în stație, planul de energie Windows (fără sleep, USB selective suspend dezactivat).

## Etapa 6 — Waterfall și funcții de operare
- Waterfall pe server (FFT 4096, port din `audio.py`/`Waterfall`), trimis comprimat: rânduri de 8 biți, 5–10 rânduri/s, lățimea limitată la filtru. Cost: câțiva kbit/s.
- Atingere sau dublu-tap pe waterfall = acord pe semnal.
- Meniul EX (`menu_ftx1.json`) ca tab nou **„Meniu stație (EX)”** în fereastra „Setări stație”: listă cu căutare, grupuri, citire/scriere. Pe Android: ecran separat în Setări. Editorul de memorii (procedura exactă din docstring-ul `memory.py`), favorite, CW keyer memory (`KM`/`KY`, trecute prin `TransmitGuard`).

## Etapa 7 și mai departe — moduri digitale
În ordinea priorității proprietarului (de confirmat): FT8/FT4 (ft8_lib, MIT, DLL nativ + P/Invoke, decodare pe server), decodor CW + AI, RTTY, PSK31, NAVTEX, SSTV, WEFAX, apel vocal automat.
Pentru fiecare: semnale de test generate din Python în `tests/data`, iar decodorul C# trebuie să obțină același text.

---

## Lista de verificare la fiecare PR
- [ ] `dotnet build` fără avertismente noi; `dotnet test` verde pe Linux și Windows.
- [ ] Teste noi pentru codul nou; pentru rețea: deconectare, latență, token greșit.
- [ ] Nicio cale de emisie în afara `TransmitGuard`.
- [ ] Nimic nu ascultă pe interfețe publice fără opțiune explicită.
- [ ] Textele UI doar în `Strings.ro.axaml`.
- [ ] Descrierea PR-ului: ce s-a făcut, sursele Python folosite și **„De verificat pe stație/telefon”**.

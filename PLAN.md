# Plan de acțiune: portarea FTX-1 Pult în C# (.NET 8 + Avalonia UI)

Destinatar: un agent AI sau un programator care face portarea pas cu pas.
Repo țintă: `Eugen68/ftx1-CATandremote` (acum gol).
Sursa de adevăr: `Eugen68/ftx1-pult` (Python + web). Se citește codul Python pentru fiecare modul portat; acest plan spune **ce**, **în ce ordine** și **cum se verifică**.

> ⚠️ Drepturi de autor: `ftx1-pult` este un fork al `Evgeny161rus/ftx1-pult`, fără licență. Înainte de publicarea codului portat, proprietarul repo-ului trebuie să obțină acordul autorului original (sau o licență, ideal MIT). `ft8_lib` (Karlis Goba) este MIT și poate fi folosit liber, cu păstrarea fișierului LICENSE.

---

## 0. Reguli de lucru (pentru executant)

1. **Un pas = un branch + un pull request.** Nu lucra direct pe `main`. Fiecare PR trebuie să compileze și să treacă testele.
2. **Nu inventa comportamentul stației.** Comenzile CAT, formatele și ciudățeniile vin din `ftx1-pult/server/cat.py`, `memory.py`, `sim.py`. Copiază exact formatele (`%09d`, `%03d` etc.).
3. **Siguranța la emisie e obligatorie** (vezi §3). Orice cale care pune stația pe TX trebuie să aibă `try/finally` care trimite `TX0;`.
4. **Testele vin odată cu codul.** Pentru DSP: generezi semnalul cu funcția Python de sinteză, îl salvezi ca fișier `.wav`/`.f32` în `tests/data/`, iar testul C# trebuie să-l decodeze la fel.
5. Executantul nu are stația fizică. Testează cu simulatorul. Testele pe FTX-1 real le face proprietarul; trece în descrierea PR-ului „de verificat pe stație: …”.
6. Interfața: în română acum, dar **toate textele într-un singur loc** (`Strings.ro.axaml` ca ResourceDictionary), ca să se poată adăuga engleză/rusă ulterior.

## 1. Mediu de lucru

- .NET SDK 8. Pe Ubuntu 24.04: `apt-get install -y dotnet-sdk-8.0` (scriptul `dot.net/dotnet-install.sh` poate fi blocat de proxy).
- Testele pe Linux merg pentru `Core`, `Sim`, `Dsp`. Audio (NAudio/WASAPI) și portul serial real se testează doar pe Windows.
- Python 3.12 + `pip install -r ftx1-pult/requirements.txt`, pentru generarea datelor de test din codul original.

## 2. Structura soluției

```
ftx1-CATandremote/
  Ftx1.sln
  src/
    Ftx1.Core/        # CAT, Rig, PTT, config, logare — fără UI, fără audio specific Windows
    Ftx1.Audio/       # interfață IAudioDevice + implementare NAudio (WASAPI) — Windows
    Ftx1.Dsp/         # decimare/interpolare, FFT/waterfall, CW, RTTY, PSK31, NAVTEX, SSTV, WEFAX
    Ftx1.Ft8/         # P/Invoke către ft8_lib (DLL nativ) + secvențiere QSO + ADIF/UDP
    Ftx1.Sim/         # simulatorul stației (CAT + „eter” audio)
    Ftx1.App/         # Avalonia UI (MVVM, CommunityToolkit.Mvvm)
  native/ft8_lib/     # copiat din ftx1-pult/server/ftx/lib + CMakeLists.txt
  tests/
    Ftx1.Core.Tests/  # xUnit
    Ftx1.Dsp.Tests/
    data/             # semnale de referință generate din Python
  .github/workflows/ci.yml
  README.md
```

Pachete NuGet (versiuni fixe):
- `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter` — 11.2.x
- `CommunityToolkit.Mvvm` 8.3.x
- `System.IO.Ports` 8.0.0
- `NAudio` 2.2.1 (numai în `Ftx1.Audio`)
- teste: `xunit` 2.9.x, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` 17.11.x

## 3. Siguranța la emisie (se implementează în Etapa 1 și nu se slăbește niciodată)

Port din `ftx1-pult/server/main.py` (`set_ptt`, `_watchdog`) și `cat.py` (`Rig.close`, `Rig.ptt`):
- O singură „sursă” de emisie pe rând (manual, FT8, RTTY, CW, apel vocal). Dacă una transmite, celelalte sunt refuzate cu mesaj.
- Timp maxim de emisie configurabil (`MaxTxSeconds`, implicit 180). La depășire: `TX0;` și mesaj.
- La închiderea aplicației, deconectare, excepție sau pierderea portului: se trimite `KY00;TX0;` și se coboară linia RTS/DTR.
- În modul „remote” (Etapa 5): dacă clientul nu trimite semnal de viață 1,5 s, emisia se oprește.
- Teste obligatorii: emisia se oprește după `MaxTxSeconds`; `Dispose()` trimite `TX0;`; a doua sursă e refuzată.

---

## Etapa 1 — Nucleul CAT + simulator + UI minimal

### 1.1 `Ftx1.Core/Cat/CatProtocol.cs`
Port din `cat.py`:
- `MODES` (cod → nume) și inversul `MODE_CODES`; `METERS`; lista `CTCSS` (50 de valori); `BandType(freq)` (0=HF/50, 1=144, ≥420 MHz → 2).
- `PARSERS`: lista de regex-uri ancorate `^...$` → dicționar de modificări. Valori: `int`, `bool`, `double` (PC), `string` (radio_id, fw). Toate cele ~60 de reguli, în aceeași ordine (contează: `VE(\d)(\d{2})-?\.?(\d{2})` înaintea `VE(\d)(.+)`).
- `ParseFrame(string frame)` → `IReadOnlyDictionary<string, object>` (gol dacă nu se potrivește nimic sau dacă parsarea aruncă excepție).
- `IsQuery(cmd)`: lista de comenzi fără parametri + tabelul `prefix → lungime` din `_is_query`.

Teste: câte un caz pentru fiecare regulă (de exemplu `FA014074000` → `freq_a=14074000`; `IS01-0100` → `ifshift_on=1, ifshift=-100`; `RI00000001` → `busy=true`; `VE00110` → `fw0="1.10"`).

### 1.2 `ITransport`, `SerialTransport`, `SerialPtt`
- `ITransport { void Open(Action<byte[]?> onBytes); void Write(byte[]); void Close(); bool Failed; void Reopen(); }`
- `SerialTransport` (System.IO.Ports): 8N1, RTS/DTR **false** la deschidere, fir de citire dedicat, `onBytes(null)` + `Failed=true` la eroare.
- `SerialPtt`: al doilea port COM, linia RTS sau DTR, inițial jos.

### 1.3 `Ftx1.Core/Cat/Rig.cs` — port fidel al clasei `Rig`
- Starea: dicționar `string → object` protejat de lock + eveniment `Changed(IReadOnlyDictionary<string, object> diff)` (doar valorile schimbate).
- Coada de comenzi: `Channel<object>` nelimitat. Elemente: comandă text; marcaj de frecvență `@freq_a`/`@freq_b`; secvență `(List<string>, TaskCompletionSource<List<string?>>)` cu pauze `~NNN;`.
- **Frecvența „ultima câștigă”**: `SetParam("freq_a")` pune comanda în `_freqPend[key]` și un singur marcaj în coadă; worker-ul trimite doar ultima valoare, apoi `FA;`. Fără citire `FA;/FB;` în următoarele 0,5 s.
- `Xfer(cmd)`: notifică ascultătorii consolei (`">"+cmd`); pentru interogări setează `pending=(prefix 2 caractere, TCS)` și așteaptă max 250 ms; pentru comenzi de setare așteaptă 12 ms. La eroare de scriere: `connected=false`, pauză 1 s.
- Recepție: tampon de octeți, împărțire la `;`, ASCII, `Trim`; tampon >512 → golit. `OnFrame`: consolă (`"<"+frame+";"`), răspunsuri `EX` către cei care așteaptă meniul, `ParseFrame` → `Set`, rezolvare `pending` dacă `frame=="?"` sau începe cu prefixul.
- Worker (buclă): (a) dacă transportul a căzut: pauză 3 s, `Reopen`, `AI1;`; (b) toată coada de utilizator; (c) interogare rapidă `FAST_TX` dacă `tx`, altfel `FAST_RX`; (d) la 0,3 s `MEDIUM`; (e) 2 elemente din `SLOW` circular (`{band}` → `BandType(freq_a)`), sărind comenzile cu ≥3 răspunsuri `?` consecutive; (f) pauză 30 ms; (g) `connected=false` dacă nu s-a primit nimic 3 s, `true` dacă s-a primit în ultima secundă.
- `Start()`: deschide transportul, `connected=true`, trimite `ID; VE0..VE5; AI1;`.
- `Refresh(delay)`: pune în coadă `FA; FB; MD0;` + toată lista `SLOW`.
- `MenuGet(ex)` (timeout 1,5 s), `MenuSet(ex, value, signed, digits)`: aceeași lățime ca valoarea citită, semn `+/-` dacă valoarea citită are semn.
- `Ptt(on)`: `TX1;`/`TX2;`/`TX0;` + `TX;`, sau linia serială + `TX;`.
- `SetParam(key, value)`: **toate** ramurile din `set_param`, cu aceleași limite. `clamp` = `Math.Round(x, MidpointRounding.ToEven)` (Python `round`). Puterea: cap 2 (`pwr_head==2`) → `PC2%03d` 5..100; altfel 0,5..10 W, `PC1005;` pentru întregi, `PC15.5;` pentru zecimale (InvariantCulture). Același mod nu se retrimite (`MD` resetează filtrul). Apoi citirea de verificare (tabelul `readback` + regula `re.sub(r'[+\-0-9.]+;$','',cmd)`), plus `Refresh(0.3)` la schimbarea modului sau a benzii.
- `Action(name, arg)`: dicționarul de acțiuni + `band` (`BS0%02d`), `cw_play`, `cw_store` (max 50 caractere, filtrul `[^A-Z0-9 /?.,=+\-]`), `voice_play`, `mem_ch` (`MC0%05d`), `power_off`; apoi `FA; FB; MD0; AC;` și `Refresh(0.4)` pentru acțiunile de bandă/memorie.
- `Dispose()`: oprește worker-ul, scrie `KY00;TX0;`, coboară PTT serial, închide porturile.

Teste (contra simulatorului): conectare și `radio_id=="0840"`; `SetParam("af",128)` → stare `af=128`; 100 de setări rapide de frecvență → stația primește doar ultima valoare (contor în simulator); 3×`?` → comanda iese din polling; `Dispose` → simulatorul vede `TX0`.

### 1.4 `Ftx1.Sim`
Port din `sim.py`: `SimRadio.handle` (starea inițială `s`, ID, VE, RI, SM, RM, EX, BS, KM/KY, citire și scriere generică cu „cea mai lungă cheie”, timer de 2,5 s pentru tuner), memorii (`_sim_mem`: MR/MW/MT/MZ/MC/VM/AM exact ca în Python). `SimTransport`: răspuns după ~4 ms pe alt fir. Audio-ul simulatorului vine în Etapa 2.

### 1.5 `Ftx1.App` — UI minimal (fereastră principală)
- Bară de conexiune: port COM (`SerialPort.GetPortNames()`), viteză (38400 implicit), metodă PTT (TX1/TX2/RTS/DTR + port), bifă **Simulator**, Conectare/Deconectare, stare (conectat, ID, firmware).
- Frecvența VFO-A mare: rotița mouse-ului pe o cifră o modifică; câmp de introducere cu regula din pult: `7.074` = MHz, `14200` = kHz, `145500000` = Hz (vezi `web/app.js`, căutare după „Набрать частоту”). VFO-B mic, butoane A⇄B, A→B, SPLIT.
- Benzile: lista `BANDS` din `web/app.js` (indexul = codul `BS`). Modurile: lista `MODES` din `app.js`.
- Contoare: S (tabelul `S_CAL`), la emisie PO/SWR/ALC (`PO_CAL`, `SWR_CAL`) — toate în `app.js`.
- Slidere: AF, RF, SQL, putere, MIC. Butoane: NB, DNR, DNF, ATT, IPO/AMP, AGC, NAR, LOCK, Tuner.
- PTT: buton „ține apăsat” + tasta Space; cronometru; Esc oprește emisia.
- Consola CAT: câmp de comandă brută + jurnal `>`/`<`.
- Configurarea în `config.json` lângă exe (cu rezervă în `%APPDATA%\FTX1-CATandRemote\`), scriere atomică (fișier temporar + `File.Move` cu suprascriere), validare (porturi, `MaxTxSeconds` între 10 și 600).
- Evenimentele `Rig.Changed` vin pe alt fir → `Dispatcher.UIThread.Post`.

**Criteriu de acceptare Etapa 1:** pe Windows, cu FTX-1, frecvența, modul, contorul S și sliderele funcționează în ambele sensuri; PTT pornește și oprește emisia; închiderea ferestrei în timpul emisiei oprește emisia. În simulator: totul, fără stație.

### 1.6 CI — `.github/workflows/ci.yml`
- La push și PR: `ubuntu-latest` → `dotnet build` + `dotnet test`.
- `windows-latest` (la push pe `main` și manual): `dotnet publish src/Ftx1.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` → artefact `FTX1-CATandRemote.exe` + SHA256.

---

## Etapa 2 — Audio + waterfall
Sursă: `audio.py`, `worklets.js`, partea de waterfall din `app.js`.
- `IAudioDevice`: recepție 48 kHz mono float → callback; emisie: tampon cu jitter-buffer. Implementare NAudio WASAPI; detectarea automată a „USB Audio CODEC” (`guess_codec`).
- Ascultarea recepției pe difuzorul PC-ului (redare locală), volum, mute.
- Microfonul PC-ului → emisie (doar când PTT e activ, tamponul golit la pornire și oprire).
- Waterfall: FFT 4096 la 48 kHz, intervalul 0..lățimea filtrului, randare `WriteableBitmap`; dublu-click = acord pe semnal.
- Squelch la distanță (doar FM), înregistrarea QSO-ului în WAV 16 kHz (`recorder.py`).
- Audio pentru simulator: zgomot + semnale (port din `SimAudio` din `sim.py`).
- Teste: decimator/interpolator (răspuns în frecvență comparat cu Python), FFT pe un ton cunoscut.

## Etapa 3 — Meniul EX + editorul de memorii
- Meniul EX: `menu_ftx1.json` copiat ca resursă; grid cu căutare și grupuri (`MENU_GROUPS`), citire și scriere prin `MenuGet`/`MenuSet`.
- Editorul de memorii: port din `memory.py` + `web/mem.js`. **Respectă exact procedura documentată în docstring-ul din `memory.py`** (MW nu setează tonul → VFO-A + `AM`; `AM` șterge numele → `MT` după; restaurarea VFO-A și a cursorului). Import/export CSV + CHIRP, grupuri, „scrie doar modificările” cu verificare prin recitire.
- Teste: toate operațiile contra simulatorului (care implementează aceleași reguli).

## Etapa 4 — FT8/FT4
- `native/ft8_lib`: CMake → `ftx.dll` (Windows x64) / `libftx.so` (Linux, pentru teste). Interfață C mică, la fel ca `server/ftx/_ftxmodule.c`: `encode(text, ft4) → tonuri`, `decode(samples12k, ft4) → listă`.
- Port din `ft8.py`: sloturi UTC, GFSK, autosecvența (vezi docstring-ul), Auto-CQ, ADIF (`ft8_log.adi`), UDP WSJT-X (QSO Logged + Logged ADIF), PSK Reporter (`pskreporter.py`), DXCC (`dxcc.py` + `cty.dat`, actualizare lunară).
- Teste: autotest „encode → decode” ca în `selftest()` din `main.py`; secvența QSO contra simulatorului (`SimFt8`).

## Etapa 5 — Moduri digitale + CW + AI
Ordinea sugerată (de la ușor la greu): RTTY (`digi.py`) → PSK31 (`psk31.py`) → NAVTEX (`navtex.py`) → WEFAX (`digi.py`) → SSTV (`sstv.py`) → decodorul CW (`cw.py`) → CW desk + AI (`cwdesk.py`, `ai.py`, LM Studio, API compatibil OpenAI) → apel vocal (`voicecq.py`).
- Pentru fiecare: funcția de sinteză din Python generează `tests/data/<mod>_*.f32` + textul așteptat; testul C# trebuie să-l decodeze. Exact testele din `selftest()` din `main.py`.
- Siguranța AI de la răspunsul automat se păstrează: transmite doar dacă indicativul propriu apare în textul primit, cu numărătoare inversă care poate fi anulată și cu limită de răspunsuri consecutive.

## Etapa 6 — Acces de la distanță + telefon
- Serverul în aplicația PC: WebSocket sau gRPC pe TLS, **parolă obligatorie**, limitare de încercări pe IP, comparare constantă (`CryptographicOperations.FixedTimeEquals`). Fără privilegii automate pentru „localhost”.
- Client Android: Avalonia.Android, refolosind ViewModel-urile din `Ftx1.App` (de aceea `App` trebuie scris cu MVVM curat, fără logică în code-behind).
- Audio pe rețea: Opus (pachetul `Concentus`) în loc de PCM brut.
- Watchdog-ul de 1,5 s pentru clienții remote (§3).

---

## Lista de verificare la fiecare PR
- [ ] `dotnet build` fără avertismente noi; `dotnet test` verde.
- [ ] Teste noi pentru codul nou.
- [ ] Nicio cale de emisie fără `finally { TX0 }`.
- [ ] Textele UI doar în `Strings.ro.axaml`.
- [ ] În descriere: ce s-a portat (fișierele Python sursă), ce rămâne de verificat pe stația reală.
